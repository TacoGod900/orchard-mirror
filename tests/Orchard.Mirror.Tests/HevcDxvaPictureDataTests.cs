using System.Buffers.Binary;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

internal static class HevcDxvaPictureDataTests
{
    internal static void BuildsAlignedAnnexBAndShortSliceEntries()
    {
        byte[] firstSlice = [(byte)(1 << 1), 1, 0xAA, 0xBB];
        byte[] secondSlice = [(byte)(1 << 1), 1, 0xCC];
        HevcAccessUnit accessUnit = new(
            10,
            [
                [(byte)(HevcNalType.SequenceParameterSet << 1), 1, 0x11],
                firstSlice,
                secondSlice,
            ],
            false);

        Assert(HevcDxvaPictureData.TryBuild(accessUnit, out HevcDxvaPictureData? result), "VCL slices produced no DXVA picture.");
        HevcDxvaPictureData picture = result
            ?? throw new InvalidOperationException("The builder returned true without picture data.");

        Assert(picture.Bitstream.Length == 128, $"Bitstream length {picture.Bitstream.Length} is not one aligned block.");
        Assert(picture.SliceControl.Length == 24 && picture.SliceCount == 2, "Expected two 12-byte short-slice entries.");
        Assert(picture.Bitstream.AsSpan(0, 3).SequenceEqual<byte>([0, 0, 1]), "First Annex-B start code is missing.");
        Assert(picture.Bitstream.AsSpan(3, firstSlice.Length).SequenceEqual(firstSlice), "First slice bytes changed.");

        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(picture.SliceControl);
        uint firstLength = BinaryPrimitives.ReadUInt32LittleEndian(picture.SliceControl.AsSpan(4));
        uint secondOffset = BinaryPrimitives.ReadUInt32LittleEndian(picture.SliceControl.AsSpan(12));
        uint secondLength = BinaryPrimitives.ReadUInt32LittleEndian(picture.SliceControl.AsSpan(16));
        Assert(firstOffset == 0 && firstLength == 3 + firstSlice.Length, "First short-slice bounds were wrong.");
        Assert(secondOffset == firstLength, "Second slice did not start immediately after the first.");
        Assert(secondOffset + secondLength == picture.Bitstream.Length, "Padding was not assigned to the final slice.");
    }

    internal static void RefusesAnAccessUnitWithoutCodedSlices()
    {
        HevcAccessUnit accessUnit = new(
            10,
            [[(byte)(HevcNalType.SequenceParameterSet << 1), 1, 0x11]],
            false);
        Assert(!HevcDxvaPictureData.TryBuild(accessUnit, out _), "A parameter-set-only access unit became a picture.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
