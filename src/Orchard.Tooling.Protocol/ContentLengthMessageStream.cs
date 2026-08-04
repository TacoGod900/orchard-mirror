using System.Buffers;
using System.Globalization;
using System.Text;

namespace Orchard.Tooling.Protocol;

/// <summary>
/// Reads and writes the content-length envelope shared by LSP and DAP without
/// allowing partial traffic to reset the aggregate operation deadline.
/// </summary>
public sealed class ContentLengthMessageChannel : IAsyncDisposable
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ContentLengthProtocolOptions _options;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Exception? _terminalFault;
    private int _disposed;

    public ContentLengthMessageChannel(
        Stream input,
        Stream output,
        ContentLengthProtocolOptions? options = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        _input = input;
        _output = output;
        _options = (options ?? new ContentLengthProtocolOptions()).Validate();
        _leaveOpen = leaveOpen;
    }

    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        using var deadline = CreateDeadline(cancellationToken);
        var acquired = false;
        var operationStarted = false;
        try
        {
            await _readGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            try
            {
                ThrowIfUnavailable();
                operationStarted = true;
                var header = await ReadHeaderAsync(deadline.Token).ConfigureAwait(false);
                var contentLength = ParseContentLength(header);
                var content = GC.AllocateUninitializedArray<byte>(contentLength);
                await ReadExactlyAsync(content, deadline.Token).ConfigureAwait(false);
                return content;
            }
            finally
            {
                if (acquired)
                {
                    _readGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ContentLengthMessageChannel));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"The framed message was not received within {_options.OperationTimeout}.",
                exception);
            if (operationStarted)
            {
                RecordTerminalFault(timeout);
            }

            throw timeout;
        }
        catch (Exception exception)
        {
            if (operationStarted)
            {
                RecordTerminalFault(exception);
            }

            throw;
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (content.Length is < 2 || content.Length > _options.MaximumContentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                $"Message content must be between 2 and {_options.MaximumContentBytes} bytes.");
        }

        var header = Encoding.ASCII.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Content-Length: {content.Length}\r\n\r\n"));

        using var deadline = CreateDeadline(cancellationToken);
        var acquired = false;
        var operationStarted = false;
        try
        {
            await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            try
            {
                ThrowIfUnavailable();
                operationStarted = true;
                await _output.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await _output.WriteAsync(content, deadline.Token).ConfigureAwait(false);
                await _output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                if (acquired)
                {
                    _writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ContentLengthMessageChannel));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"The framed message was not sent within {_options.OperationTimeout}.",
                exception);
            if (operationStarted)
            {
                RecordTerminalFault(timeout);
            }

            throw timeout;
        }
        catch (Exception exception)
        {
            if (operationStarted)
            {
                RecordTerminalFault(exception);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        if (!_leaveOpen)
        {
            await _input.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_input, _output))
            {
                await _output.DisposeAsync().ConfigureAwait(false);
            }
        }

        _lifetimeCancellation.Dispose();
    }

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        deadline.CancelAfter(_options.OperationTimeout);
        return deadline;
    }

    private async ValueTask<byte[]> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(_options.MaximumHeaderBytes);
        try
        {
            var count = 0;
            var terminatorIndex = 0;
            while (count < _options.MaximumHeaderBytes)
            {
                var singleByte = new Memory<byte>(rented, count, 1);
                var received = await _input.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    throw new EndOfStreamException("The protocol stream ended before a complete header was received.");
                }

                var value = rented[count++];
                if (value == HeaderTerminator[terminatorIndex])
                {
                    terminatorIndex++;
                    if (terminatorIndex == HeaderTerminator.Length)
                    {
                        return rented.AsSpan(0, count - HeaderTerminator.Length).ToArray();
                    }
                }
                else
                {
                    terminatorIndex = value == HeaderTerminator[0] ? 1 : 0;
                }
            }

            throw new ContentLengthProtocolException(
                $"The protocol header exceeded {_options.MaximumHeaderBytes} bytes.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private int ParseContentLength(ReadOnlySpan<byte> header)
    {
        for (var index = 0; index < header.Length; index++)
        {
            var value = header[index];
            if (value == (byte)'\r')
            {
                if (++index >= header.Length || header[index] != (byte)'\n')
                {
                    throw new ContentLengthProtocolException("A protocol header contained a bare carriage return.");
                }

                continue;
            }

            if (value < 0x20 || value > 0x7e)
            {
                throw new ContentLengthProtocolException("A protocol header contained a non-ASCII or control byte.");
            }
        }

        var text = Encoding.ASCII.GetString(header);
        var contentLength = -1;
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
        {
            if (line.Length == 0)
            {
                throw new ContentLengthProtocolException("A protocol header contained an empty line.");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new ContentLengthProtocolException("A protocol header line had no valid field name.");
            }

            var name = line.AsSpan(0, colon);
            if (!IsHeaderName(name))
            {
                throw new ContentLengthProtocolException("A protocol header field name was invalid.");
            }

            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (contentLength >= 0)
            {
                throw new ContentLengthProtocolException("A protocol message contained duplicate Content-Length fields.");
            }

            var value = line.AsSpan(colon + 1).Trim();
            if (value.IsEmpty || value.IndexOfAnyExceptInRange('0', '9') >= 0 ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
            {
                throw new ContentLengthProtocolException("The Content-Length field was not an unsigned decimal integer.");
            }
        }

        if (contentLength < 0)
        {
            throw new ContentLengthProtocolException("A protocol message had no Content-Length field.");
        }

        if (contentLength is < 2 || contentLength > _options.MaximumContentBytes)
        {
            throw new ContentLengthProtocolException(
                $"The protocol content length must be between 2 and {_options.MaximumContentBytes} bytes.");
        }

        return contentLength;
    }

    private static bool IsHeaderName(ReadOnlySpan<char> name)
    {
        foreach (var value in name)
        {
            if (!(char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or
                    '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var received = await _input.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                throw new EndOfStreamException("The protocol stream ended before the declared content was received.");
            }

            offset += received;
        }
    }

    private void RecordTerminalFault(Exception exception) =>
        Interlocked.CompareExchange(ref _terminalFault, exception, null);

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var fault = Volatile.Read(ref _terminalFault);
        if (fault is not null)
        {
            throw new InvalidOperationException(
                "The framed protocol channel is terminally faulted and cannot be reused.",
                fault);
        }
    }
}
