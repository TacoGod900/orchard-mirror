using System.Collections.Concurrent;
using System.Security.Cryptography;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Orchard.Mirror.Probe;

internal sealed record BlePeerObservation(
    string PeerId,
    short MaxRssi,
    int PacketCount,
    IReadOnlyList<byte> ContinuityTypes);

internal sealed class AppleBleScanner
{
    private const ushort AppleCompanyId = 0x004C;

    public static async Task<IReadOnlyList<BlePeerObservation>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<ulong, MutableObservation> observations = new();
        BluetoothLEAdvertisementWatcher watcher = new()
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, eventArgs) =>
        {
            BluetoothLEManufacturerData[] appleData =
                eventArgs.Advertisement.ManufacturerData
                    .Where(static data => data.CompanyId == AppleCompanyId)
                    .ToArray();
            if (appleData.Length == 0)
            {
                return;
            }

            MutableObservation observation = observations.GetOrAdd(
                eventArgs.BluetoothAddress,
                static address => new MutableObservation(PeerId(address)));
            lock (observation)
            {
                observation.PacketCount++;
                observation.MaxRssi = Math.Max(observation.MaxRssi, eventArgs.RawSignalStrengthInDBm);
                foreach (BluetoothLEManufacturerData manufacturerData in appleData)
                {
                    byte[] payload = ReadBuffer(manufacturerData.Data);
                    foreach (byte type in ParseContinuityTypes(payload))
                    {
                        observation.Types.Add(type);
                    }
                }
            }
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            watcher.Stop();
        }

        return observations.Values.Select(
            static observation => new BlePeerObservation(
                observation.PeerId,
                observation.MaxRssi,
                observation.PacketCount,
                observation.Types.Order().ToArray())).ToArray();
    }

    public static string DescribeType(byte type) => type switch
    {
        0x07 => " (AirDrop)",
        0x0C => " (Handoff)",
        0x0F => " (Nearby Action)",
        0x10 => " (Nearby Info)",
        _ => string.Empty
    };

    private static IEnumerable<byte> ParseContinuityTypes(byte[] payload)
    {
        int offset = 0;
        while (offset + 2 <= payload.Length)
        {
            byte type = payload[offset++];
            int length = payload[offset++];
            if (offset + length > payload.Length)
            {
                yield break;
            }

            yield return type;
            offset += length;
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        using DataReader reader = DataReader.FromBuffer(buffer);
        byte[] bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string PeerId(ulong bluetoothAddress)
    {
        Span<byte> address = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(address, bluetoothAddress);
        string fingerprint = Convert.ToHexString(SHA256.HashData(address))[..10].ToLowerInvariant();
        return $"ble-{fingerprint}";
    }

    private sealed class MutableObservation(string peerId)
    {
        public string PeerId { get; } = peerId;

        public short MaxRssi { get; set; } = short.MinValue;

        public int PacketCount { get; set; }

        public HashSet<byte> Types { get; } = [];
    }
}
