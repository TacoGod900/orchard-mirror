using System.Buffers.Binary;

namespace Orchard.Protocol;

/// <summary>
/// Reads and writes protocol JSON using a four-byte unsigned big-endian byte-count prefix.
/// </summary>
public static class ProtocolFrameCodec
{
    public static void Write(Stream stream, ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = ProtocolJson.Serialize(envelope);
        Span<byte> header = stackalloc byte[ProtocolConstants.FrameHeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = ProtocolJson.Serialize(envelope);
        var header = new byte[ProtocolConstants.FrameHeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static ProtocolEnvelope Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[ProtocolConstants.FrameHeaderBytes];
        ReadExactly(stream, header, "frame header");
        var length = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(header));
        var payload = new byte[length];
        ReadExactly(stream, payload, "frame payload");
        return ProtocolJson.Deserialize(payload);
    }

    public static async ValueTask<ProtocolEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[ProtocolConstants.FrameHeaderBytes];
        await ReadExactlyAsync(stream, header, "frame header", cancellationToken).ConfigureAwait(false);
        var length = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(header));
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, "frame payload", cancellationToken).ConfigureAwait(false);
        return ProtocolJson.Deserialize(payload);
    }

    private static int ValidateLength(uint length)
    {
        if (length == 0)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidFrame, "A frame cannot contain an empty payload.");
        }

        if (length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new ProtocolException(
                ProtocolErrorCodes.FrameTooLarge,
                $"Frame declares {length} bytes; the maximum is {ProtocolConstants.MaximumFrameBytes} bytes.");
        }

        return checked((int)length);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string component)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new ProtocolException(
                    ProtocolErrorCodes.InvalidFrame,
                    $"The stream ended in the middle of the {component}.");
            }

            offset += read;
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        string component,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ProtocolException(
                    ProtocolErrorCodes.InvalidFrame,
                    $"The stream ended in the middle of the {component}.");
            }

            offset += read;
        }
    }
}
