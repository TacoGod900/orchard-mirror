namespace Orchard.Mirror.Media;

internal sealed class HevcBitstreamException(string? message = null) : Exception(message);

internal ref struct HevcBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bitOffset;

    internal HevcBitReader(ReadOnlySpan<byte> data) => this.data = data;

    internal int Position => bitOffset;

    internal bool ReadFlag() => ReadBits(1) != 0;

    internal void SkipBits(int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, 32);
            _ = ReadBits(chunk);
            count -= chunk;
        }
    }

    internal uint ReadBits(int count)
    {
        if (count is < 0 or > 32 || bitOffset + count > data.Length * 8)
        {
            throw new HevcBitstreamException($"Read {count} bits at {bitOffset}, length {data.Length * 8}.");
        }

        uint value = 0;
        for (int index = 0; index < count; index++)
        {
            value = (value << 1) | (uint)((data[bitOffset >> 3] >> (7 - (bitOffset & 7))) & 1);
            bitOffset++;
        }

        return value;
    }

    internal uint ReadUnsignedExpGolomb()
    {
        int leadingZeroBits = 0;
        while (!ReadFlag())
        {
            leadingZeroBits++;
            if (leadingZeroBits > 31)
            {
                throw new HevcBitstreamException();
            }
        }

        uint suffix = leadingZeroBits == 0 ? 0 : ReadBits(leadingZeroBits);
        return ((1u << leadingZeroBits) - 1) + suffix;
    }

    internal int ReadSignedExpGolomb()
    {
        uint code = ReadUnsignedExpGolomb();
        long magnitude = (code + 1L) >> 1;
        long value = (code & 1) == 0 ? -magnitude : magnitude;
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new HevcBitstreamException();
        }

        return (int)value;
    }
}

internal static class HevcRbsp
{
    internal static byte[] Decode(ReadOnlySpan<byte> nalUnit)
    {
        if (nalUnit.Length < 3)
        {
            throw new HevcBitstreamException();
        }

        List<byte> result = new(nalUnit.Length - 2);
        int zeroCount = 0;
        foreach (byte value in nalUnit[2..])
        {
            if (zeroCount >= 2 && value == 3)
            {
                zeroCount = 0;
                continue;
            }

            result.Add(value);
            zeroCount = value == 0 ? zeroCount + 1 : 0;
        }

        return [.. result];
    }
}
