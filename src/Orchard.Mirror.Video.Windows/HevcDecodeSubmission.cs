namespace Orchard.Mirror.Video.Windows;

/// <summary>
/// Driver-ready HEVC picture data. DirectX Video Acceleration consumes parsed picture parameters,
/// scaling matrices and slice locations alongside the coded bytes; it does not accept Annex-B NAL
/// units alone. The platform-neutral HEVC parser produces this contract.
/// </summary>
public sealed record HevcDecodeSubmission(
    ReadOnlyMemory<byte> PictureParameters,
    ReadOnlyMemory<byte> InverseQuantizationMatrix,
    ReadOnlyMemory<byte> SliceControl,
    ReadOnlyMemory<byte> Bitstream,
    int Width,
    int Height,
    int OutputSurfaceIndex,
    int PictureOrderCount,
    bool VideoFullRange = false,
    int MatrixCoefficients = 2);
