using System.Text;

namespace Orchard.NativeHost.Windows;

internal sealed class BoundedOutputCapture
{
    private readonly byte[] _captured;
    private readonly object _gate = new();
    private int _length;
    private bool _truncated;

    public BoundedOutputCapture(int maximumBytes)
    {
        _captured = new byte[maximumBytes];
    }

    public async Task PumpAsync(Stream stream)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            lock (_gate)
            {
                var retained = Math.Min(read, _captured.Length - _length);
                if (retained > 0)
                {
                    buffer.AsSpan(0, retained).CopyTo(_captured.AsSpan(_length));
                    _length += retained;
                }

                _truncated |= retained != read;
            }
        }
    }

    public CapturedText Snapshot()
    {
        lock (_gate)
        {
            return new CapturedText(
                new UTF8Encoding(false, false).GetString(_captured, 0, _length),
                _truncated);
        }
    }
}

public sealed record CapturedText(string Text, bool Truncated);

public sealed record NativeChildOutput(CapturedText StandardOutput, CapturedText StandardError);
