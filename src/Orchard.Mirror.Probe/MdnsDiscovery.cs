using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Orchard.Mirror.Probe;

internal sealed record MdnsService(
    string PeerId,
    int Port,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> TxtKeys);

internal static class MdnsDiscovery
{
    private const string CompanionLinkService = "_companion-link._tcp.local";
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<MdnsService>> DiscoverAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        byte[] query = DnsWire.CreatePtrQuery(CompanionLinkService, requestUnicastResponse: true);
        foreach (IPAddress localAddress in GetOperationalIPv4Addresses())
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    localAddress.GetAddressBytes());
                await socket.SendToAsync(query, SocketFlags.None, MulticastEndpoint, cancellationToken);
            }
            catch (SocketException)
            {
                // Continue across interfaces; VPN and virtual adapters commonly reject multicast.
            }
        }

        DateTime deadline = DateTime.UtcNow + duration;
        Dictionary<string, MutableService> services = new(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[65_535];
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            using CancellationTokenSource receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(remaining);
            try
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    receiveTimeout.Token);
                DnsWire.ReadServices(
                    buffer.AsSpan(0, received.ReceivedBytes),
                    CompanionLinkService,
                    services);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }

        return services.Values.Select(
            static service => new MdnsService(
                Fingerprint(service.InstanceName),
                service.Port,
                service.Addresses.Order(StringComparer.Ordinal).ToArray(),
                service.TxtKeys.Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    private static IEnumerable<IPAddress> GetOperationalIPv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(static adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(static address => address.Address)
            .Where(static address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address))
            .Distinct();
    }

    private static string Fingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"mdns-{Convert.ToHexString(hash)[..10].ToLowerInvariant()}";
    }

    internal sealed class MutableService(string instanceName)
    {
        public string InstanceName { get; } = instanceName;

        public string? HostName { get; set; }

        public int Port { get; set; }

        public HashSet<string> Addresses { get; } = new(StringComparer.Ordinal);

        public HashSet<string> TxtKeys { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// DNS wire encoding and decoding (RFC 1035 §4). Internal rather than private so
    /// <see cref="SelfTest"/> can assert the encoding against the RFC without going near a network.
    /// </summary>
    internal static class DnsWire
    {
        public static byte[] CreatePtrQuery(string name, bool requestUnicastResponse)
        {
            using MemoryStream stream = new();
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteName(stream, name);
            WriteUInt16(stream, 12);
            WriteUInt16(stream, requestUnicastResponse ? (ushort)0x8001 : (ushort)1);
            return stream.ToArray();
        }

        public static void ReadServices(
            ReadOnlySpan<byte> message,
            string wantedService,
            Dictionary<string, MutableService> services)
        {
            if (message.Length < 12)
            {
                return;
            }

            int offset = 4;
            int questionCount = ReadUInt16(message, ref offset);
            int answerCount = ReadUInt16(message, ref offset);
            int authorityCount = ReadUInt16(message, ref offset);
            int additionalCount = ReadUInt16(message, ref offset);

            for (int index = 0; index < questionCount; index++)
            {
                _ = ReadName(message, ref offset);
                offset = checked(offset + 4);
                if (offset > message.Length)
                {
                    return;
                }
            }

            List<ResourceRecord> records = [];
            int recordCount = answerCount + authorityCount + additionalCount;
            for (int index = 0; index < recordCount && offset < message.Length; index++)
            {
                string name = ReadName(message, ref offset);
                ushort type = ReadUInt16(message, ref offset);
                _ = ReadUInt16(message, ref offset);
                offset = checked(offset + 4);
                ushort dataLength = ReadUInt16(message, ref offset);
                int dataOffset = offset;
                offset = checked(offset + dataLength);
                if (offset > message.Length)
                {
                    return;
                }

                records.Add(new ResourceRecord(name, type, dataOffset, dataLength));
            }

            foreach (ResourceRecord record in records.Where(
                         record => record.Type == 12 &&
                                   string.Equals(record.Name, wantedService, StringComparison.OrdinalIgnoreCase)))
            {
                int pointerOffset = record.DataOffset;
                string instance = ReadName(message, ref pointerOffset);
                services.TryAdd(instance, new MutableService(instance));
            }

            foreach (ResourceRecord record in records)
            {
                if (record.Type == 33 && services.TryGetValue(record.Name, out MutableService? service))
                {
                    int srvOffset = record.DataOffset + 4;
                    service.Port = ReadUInt16(message, ref srvOffset);
                    service.HostName = ReadName(message, ref srvOffset);
                }
                else if (record.Type == 16 && services.TryGetValue(record.Name, out service))
                {
                    int txtOffset = record.DataOffset;
                    int txtEnd = record.DataOffset + record.DataLength;
                    while (txtOffset < txtEnd)
                    {
                        int length = message[txtOffset++];
                        if (txtOffset + length > txtEnd)
                        {
                            break;
                        }

                        string item = Encoding.UTF8.GetString(message.Slice(txtOffset, length));
                        int equals = item.IndexOf('=', StringComparison.Ordinal);
                        service.TxtKeys.Add(equals < 0 ? item : item[..equals]);
                        txtOffset += length;
                    }
                }
            }

            foreach (MutableService service in services.Values)
            {
                if (service.HostName is null)
                {
                    continue;
                }

                foreach (ResourceRecord record in records.Where(
                             record => string.Equals(record.Name, service.HostName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (record.Type == 1 && record.DataLength == 4)
                    {
                        service.Addresses.Add(new IPAddress(message.Slice(record.DataOffset, 4)).ToString());
                    }
                    else if (record.Type == 28 && record.DataLength == 16)
                    {
                        service.Addresses.Add(new IPAddress(message.Slice(record.DataOffset, 16)).ToString());
                    }
                }
            }
        }

        private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
        {
            StringBuilder builder = new();
            int cursor = offset;
            bool jumped = false;
            int jumps = 0;
            while (cursor < message.Length)
            {
                byte length = message[cursor++];
                if (length == 0)
                {
                    if (!jumped)
                    {
                        offset = cursor;
                    }

                    return builder.ToString();
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (cursor >= message.Length || ++jumps > 32)
                    {
                        throw new InvalidDataException("Invalid DNS compression pointer.");
                    }

                    int pointer = ((length & 0x3F) << 8) | message[cursor++];
                    if (!jumped)
                    {
                        offset = cursor;
                        jumped = true;
                    }

                    cursor = pointer;
                    continue;
                }

                if (length > 63 || cursor + length > message.Length)
                {
                    throw new InvalidDataException("Invalid DNS name.");
                }

                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(Encoding.UTF8.GetString(message.Slice(cursor, length)));
                cursor += length;
                if (!jumped)
                {
                    offset = cursor;
                }
            }

            throw new InvalidDataException("Truncated DNS name.");
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> message, ref int offset)
        {
            if (offset + 2 > message.Length)
            {
                throw new InvalidDataException("Truncated DNS message.");
            }

            ushort value = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;
            return value;
        }

        private static void WriteName(Stream stream, string name)
        {
            foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length > 63)
                {
                    throw new ArgumentException("DNS label is too long.", nameof(name));
                }

                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }

            stream.WriteByte(0);
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private sealed record ResourceRecord(
            string Name,
            ushort Type,
            int DataOffset,
            int DataLength);
    }
}
