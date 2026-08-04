using System.Buffers;
using System.Text;

namespace Orchard.Core;

public static class BoundedUtf8File
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string ReadAllText(string path, int maximumBytes, string documentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"{documentName} exceeds the {maximumBytes:N0}-byte limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maximumBytes + 1);
        try
        {
            var total = 0;
            while (total <= maximumBytes)
            {
                var read = stream.Read(buffer, total, maximumBytes + 1 - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maximumBytes)
            {
                throw new InvalidDataException($"{documentName} exceeds the {maximumBytes:N0}-byte limit.");
            }

            var offset = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            try
            {
                return StrictUtf8.GetString(buffer, offset, total - offset);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{documentName} is not valid UTF-8.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
