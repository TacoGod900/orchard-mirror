using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Orchard.Mirror.Media;
using Orchard.Mirror.Video.Windows;

bool presentationDiagnostic = args.Length == 5 && args[2] == "--present-frame";
if (args.Length != 2 && !presentationDiagnostic)
{
    Console.Error.WriteLine(
        "usage: Orchard.Mirror.Replay <capture.rtp> <output-directory> [--present-frame <number> <output.bgra>]");
    return 2;
}

string capturePath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);

HevcAccessUnitAssembler assembler = new();
HevcIdrSubmissionBuilder builder = new(surfaceCount: 16);
D3D11HevcDecoder? decoder = null;
DxgiFlipPresenter? presenter = null;
nint diagnosticWindow = 0;
int diagnosticFrame = presentationDiagnostic ? int.Parse(args[3], CultureInfo.InvariantCulture) : -1;
string? diagnosticPath = presentationDiagnostic ? Path.GetFullPath(args[4]) : null;
using StreamWriter frameHashes = new(Path.Combine(outputDirectory, "orchard.framemd5"), append: false);
int nextSurface = 0;
int accessUnits = 0;
int decoded = 0;
int rejected = 0;
HashSet<int> snapshots = [0, 100, 200, 300, 400, 500, 600, 800, 1000, 1200, 1600, 2000];

try
{
    using FileStream capture = File.OpenRead(capturePath);
    Span<byte> sizeBytes = stackalloc byte[4];
    while (capture.Read(sizeBytes) == sizeBytes.Length)
    {
        int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(sizeBytes));
        byte[] datagram = new byte[size];
        capture.ReadExactly(datagram);
        if (!RtpPacket.TryParse(datagram, out RtpPacket packet))
        {
            continue;
        }

        HevcAccessUnit? accessUnit = assembler.Push(in packet);
        if (accessUnit is null)
        {
            continue;
        }

        accessUnits++;
        if (!builder.TryBuild(accessUnit, nextSurface, out HevcDecodeSubmission? submission) || submission is null)
        {
            rejected++;
            byte[]? firstSlice = accessUnit.NalUnits.FirstOrDefault(nal => HevcNalType.IsCodedSlice(HevcNalType.Of(nal)));
            string prefix = firstSlice is null
                ? "none"
                : Convert.ToHexString(firstSlice.AsSpan(0, Math.Min(firstSlice.Length, 24)));
            Console.WriteLine($"reject au={accessUnits - 1} reason={builder.LastFailureReason} slice={prefix}");
            continue;
        }

        decoder ??= D3D11HevcDecoder.TryCreate(submission.Width, submission.Height, surfaceCount: 16)
            ?? throw new InvalidOperationException("No D3D11 HEVC Main decoder is available.");
        decoder.Decode(submission, submission.OutputSurfaceIndex);
        if (presentationDiagnostic)
        {
            if (presenter is null)
            {
                diagnosticWindow = NativeWindow.Create();
                presenter = DxgiFlipPresenter.TryCreate(diagnosticWindow, decoder)
                    ?? throw new InvalidOperationException("No D3D11 video presentation path is available.");
            }

            if (!presenter.TryPresent(
                    submission.OutputSurfaceIndex,
                    timeoutMilliseconds: 1000,
                    diagnosticBgraPath: decoded == diagnosticFrame ? diagnosticPath : null))
            {
                throw new InvalidOperationException($"Presentation timed out at decoded frame {decoded}.");
            }
        }
        else
        {
            byte[] decodedNv12 = decoder.ReadOutputNv12(submission.OutputSurfaceIndex);
            frameHashes.WriteLine($"{decoded:D4} {Convert.ToHexString(SHA256.HashData(decodedNv12)).ToLowerInvariant()}");
            if (snapshots.Contains(decoded))
            {
                string path = Path.Combine(outputDirectory, $"frame-{decoded:D4}.nv12");
                File.WriteAllBytes(path, decodedNv12);
                Console.WriteLine(
                    $"snapshot frame={decoded} poc={submission.PictureOrderCount} surface={submission.OutputSurfaceIndex} " +
                    $"sha256={Convert.ToHexString(SHA256.HashData(decodedNv12))}");
            }
        }

        decoded++;
        nextSurface = (submission.OutputSurfaceIndex + 1) % 16;
    }

    Console.WriteLine(
        $"accessUnits={accessUnits} decoded={decoded} rejected={rejected} " +
        $"droppedAccessUnits={assembler.DroppedAccessUnits} droppedFragments={assembler.DroppedFragments}");
    return rejected == 0 ? 0 : 1;
}
finally
{
    presenter?.Dispose();
    decoder?.Dispose();
    NativeWindow.Destroy(diagnosticWindow);
}

internal static class NativeWindow
{
    internal static nint Create()
    {
        nint window = CreateWindowExW(0, "STATIC", "Orchard Mirror replay", 0, 0, 0, 390, 844, 0, 0, 0, 0);
        return window != 0 ? window : throw new InvalidOperationException("Could not create a replay HWND.");
    }

    internal static void Destroy(nint window)
    {
        if (window != 0)
        {
            _ = DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
