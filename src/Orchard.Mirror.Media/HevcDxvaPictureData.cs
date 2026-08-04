using System.Buffers.Binary;

namespace Orchard.Mirror.Media;

/// <summary>
/// The coded buffers consumed by a DXVA HEVC decoder: Annex-B-prefixed VCL NAL units, padded to the
/// driver's 128-byte boundary, and one 12-byte <c>DXVA_Slice_HEVC_Short</c> entry per slice.
/// </summary>
public sealed record HevcDxvaPictureData(byte[] Bitstream, byte[] SliceControl, int SliceCount)
{
    public static bool TryBuild(HevcAccessUnit accessUnit, out HevcDxvaPictureData? picture)
    {
        ArgumentNullException.ThrowIfNull(accessUnit);
        picture = null;

        List<byte> bitstream = [];
        List<(int Offset, int Length)> slices = [];
        foreach (byte[] nalUnit in accessUnit.NalUnits)
        {
            int type = HevcNalType.Of(nalUnit);
            if (type is < 0 or > 31)
            {
                continue;
            }

            int offset = bitstream.Count;
            bitstream.Add(0);
            bitstream.Add(0);
            bitstream.Add(1);
            bitstream.AddRange(nalUnit);
            slices.Add((offset, bitstream.Count - offset));
        }

        if (slices.Count == 0)
        {
            return false;
        }

        int padding = (128 - (bitstream.Count & 127)) & 127;
        if (padding != 0)
        {
            bitstream.AddRange(new byte[padding]);
            (int offset, int length) = slices[^1];
            slices[^1] = (offset, checked(length + padding));
        }

        byte[] sliceControl = new byte[checked(slices.Count * 12)];
        for (int index = 0; index < slices.Count; index++)
        {
            int offset = index * 12;
            BinaryPrimitives.WriteUInt32LittleEndian(sliceControl.AsSpan(offset), checked((uint)slices[index].Offset));
            BinaryPrimitives.WriteUInt32LittleEndian(sliceControl.AsSpan(offset + 4), checked((uint)slices[index].Length));
            BinaryPrimitives.WriteUInt16LittleEndian(sliceControl.AsSpan(offset + 8), 0);
        }

        picture = new HevcDxvaPictureData([.. bitstream], sliceControl, slices.Count);
        return true;
    }
}
