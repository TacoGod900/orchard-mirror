using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Orchard.Tooling.Protocol;

namespace Orchard.Tooling.Protocol.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Body)[] Tests =
    [
        ("reads fragmented frames and case-insensitive fields", ReadsFragmentedFrames),
        ("writes concurrent messages as indivisible frames", WritesConcurrentFrames),
        ("rejects missing and duplicate content lengths", RejectsContentLengthAmbiguity),
        ("rejects malformed header bytes and field names", RejectsMalformedHeaders),
        ("enforces header and content bounds before allocation", EnforcesBounds),
        ("reports premature header and content EOF", ReportsPrematureEndOfStream),
        ("uses one aggregate read deadline across partial traffic", UsesAggregateReadDeadline),
        ("uses one aggregate write deadline", UsesAggregateWriteDeadline),
        ("terminally faults after ambiguous partial traffic", FaultsAfterAmbiguousTraffic),
        ("preserves caller cancellation identity", PreservesCallerCancellation),
        ("parses unambiguous tooling JSON", ParsesStrictToolingJson),
        ("rejects duplicate and non-strict tooling JSON", RejectsAmbiguousToolingJson),
        ("enforces tooling JSON structural budgets", EnforcesToolingJsonBudgets),
        ("round-trips strict JSON-RPC message kinds", RoundTripsJsonRpcMessages),
        ("rejects ambiguous JSON-RPC envelopes", RejectsAmbiguousJsonRpcMessages),
        ("rejects unsafe JSON-RPC output fields", RejectsUnsafeJsonRpcOutput),
        ("validates policy bounds", ValidatesPolicyBounds)
    ];

    private static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (var (name, body) in Tests)
        {
            try
            {
                await body().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Executed {Tests.Length} tests; {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task ReadsFragmentedFrames()
    {
        var expected = "{\"jsonrpc\":\"2.0\",\"id\":1}"u8.ToArray();
        var bytes = Envelope(expected, "content-length", "Content-Type: application/vscode-jsonrpc; charset=utf-8");
        await using var input = new FragmentingReadStream(bytes, maximumChunk: 1);
        await using var channel = new ContentLengthMessageChannel(input, Stream.Null, leaveOpen: true);
        var actual = await channel.ReadAsync().ConfigureAwait(false);
        Assert(actual.SequenceEqual(expected), "Fragmentation or optional headers changed the content bytes.");
    }

    private static async Task WritesConcurrentFrames()
    {
        await using var output = new DelayingCaptureStream();
        await using (var channel = new ContentLengthMessageChannel(Stream.Null, output, leaveOpen: true))
        {
            var first = "{\"id\":1}"u8.ToArray();
            var second = "{\"id\":2}"u8.ToArray();
            await Task.WhenAll(
                channel.WriteAsync(first).AsTask(),
                channel.WriteAsync(second).AsTask()).ConfigureAwait(false);
        }

        var captured = output.ToArray();
        await using var input = new MemoryStream(captured, writable: false);
        await using var reader = new ContentLengthMessageChannel(input, Stream.Null, leaveOpen: true);
        var messages = new[]
        {
            Encoding.UTF8.GetString(await reader.ReadAsync().ConfigureAwait(false)),
            Encoding.UTF8.GetString(await reader.ReadAsync().ConfigureAwait(false))
        };
        Assert(messages.Order(StringComparer.Ordinal).SequenceEqual(["{\"id\":1}", "{\"id\":2}"]),
            "Concurrent writes interleaved their headers or content.");
    }

    private static async Task RejectsContentLengthAmbiguity()
    {
        await AssertReadThrows<ContentLengthProtocolException>("X-Test: 2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>(
            "Content-Length: 2\r\ncontent-length: 2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content-Length: +2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content-Length: -1\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content-Length: 0\r\n\r\n"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>(
            "Content-Length: 999999999999999999\r\n\r\n{}"u8.ToArray());
    }

    private static async Task RejectsMalformedHeaders()
    {
        await AssertReadThrows<ContentLengthProtocolException>(" Content-Length: 2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content Length: 2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content-Length 2\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>("Content-Length: 2\n\r\n\r\n{}"u8.ToArray());
        await AssertReadThrows<ContentLengthProtocolException>(
            [.. "X-Test: "u8.ToArray(), 0x1b, .. "\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray()]);
    }

    private static async Task EnforcesBounds()
    {
        var options = new ContentLengthProtocolOptions
        {
            MaximumHeaderBytes = 64,
            MaximumContentBytes = 8,
            OperationTimeout = TimeSpan.FromSeconds(1)
        };
        await AssertReadThrows<ContentLengthProtocolException>(
            Encoding.ASCII.GetBytes($"X-Fill: {new string('x', 80)}\r\nContent-Length: 2\r\n\r\n{{}}"),
            options);
        await AssertReadThrows<ContentLengthProtocolException>(
            "Content-Length: 9\r\n\r\n123456789"u8.ToArray(),
            options);

        await using var channel = new ContentLengthMessageChannel(Stream.Null, Stream.Null, options, leaveOpen: true);
        await AssertThrowsAsync<ArgumentOutOfRangeException>(
            () => channel.WriteAsync("123456789"u8.ToArray()).AsTask()).ConfigureAwait(false);
    }

    private static async Task ReportsPrematureEndOfStream()
    {
        await AssertReadThrows<EndOfStreamException>("Content-Length: 2\r\n"u8.ToArray());
        await AssertReadThrows<EndOfStreamException>("Content-Length: 5\r\n\r\n{}"u8.ToArray());
    }

    private static async Task UsesAggregateReadDeadline()
    {
        var content = "{\"method\":\"slow\"}"u8.ToArray();
        await using var input = new DribbleReadStream(Envelope(content), TimeSpan.FromMilliseconds(30));
        var options = new ContentLengthProtocolOptions { OperationTimeout = TimeSpan.FromMilliseconds(100) };
        await using var channel = new ContentLengthMessageChannel(input, Stream.Null, options, leaveOpen: true);
        var timer = Stopwatch.StartNew();
        await AssertThrowsAsync<TimeoutException>(() => channel.ReadAsync().AsTask()).ConfigureAwait(false);
        Assert(timer.Elapsed < TimeSpan.FromSeconds(2), "Partial traffic reset the aggregate read deadline.");
    }

    private static async Task UsesAggregateWriteDeadline()
    {
        await using var output = new BlockingWriteStream();
        var options = new ContentLengthProtocolOptions { OperationTimeout = TimeSpan.FromMilliseconds(100) };
        await using var channel = new ContentLengthMessageChannel(Stream.Null, output, options, leaveOpen: true);
        var timer = Stopwatch.StartNew();
        await AssertThrowsAsync<TimeoutException>(
            () => channel.WriteAsync("{}"u8.ToArray()).AsTask()).ConfigureAwait(false);
        Assert(timer.Elapsed < TimeSpan.FromSeconds(2), "A blocked write exceeded its aggregate deadline.");
    }

    private static async Task FaultsAfterAmbiguousTraffic()
    {
        byte[] invalidThenValid = [
            .. "Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray(),
            .. Envelope("{}"u8)
        ];
        await using var input = new MemoryStream(invalidThenValid, writable: false);
        await using var reader = new ContentLengthMessageChannel(input, Stream.Null, leaveOpen: true);
        await AssertThrowsAsync<ContentLengthProtocolException>(
            () => reader.ReadAsync().AsTask()).ConfigureAwait(false);
        await AssertThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync().AsTask()).ConfigureAwait(false);

        await using var output = new BlockingWriteStream();
        var options = new ContentLengthProtocolOptions { OperationTimeout = TimeSpan.FromMilliseconds(50) };
        await using var writer = new ContentLengthMessageChannel(Stream.Null, output, options, leaveOpen: true);
        await AssertThrowsAsync<TimeoutException>(
            () => writer.WriteAsync("{}"u8.ToArray()).AsTask()).ConfigureAwait(false);
        await AssertThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync("{}"u8.ToArray()).AsTask()).ConfigureAwait(false);
    }

    private static async Task PreservesCallerCancellation()
    {
        await using var input = new NeverReadStream();
        var options = new ContentLengthProtocolOptions { OperationTimeout = TimeSpan.FromSeconds(2) };
        await using var channel = new ContentLengthMessageChannel(input, Stream.Null, options, leaveOpen: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await AssertThrowsAsync<OperationCanceledException>(
            () => channel.ReadAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
    }

    private static Task ParsesStrictToolingJson()
    {
        using var document = StrictToolingJson.ParseObject(
            "{\"jsonrpc\":\"2.0\",\"result\":{\"name\":\"Swift\",\"Name\":\"Orchard\"}}"u8.ToArray());
        Assert(document.RootElement.GetProperty("result").GetProperty("name").GetString() == "Swift",
            "Strict JSON parsing changed a property value.");
        return Task.CompletedTask;
    }

    private static Task RejectsAmbiguousToolingJson()
    {
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject("{\"result\":{\"x\":1,\"x\":2}}"u8.ToArray()).Dispose());
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject("{\"x\":1,}"u8.ToArray()).Dispose());
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject("/*x*/{\"x\":1}"u8.ToArray()).Dispose());
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject("[1,2]"u8.ToArray()).Dispose());
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject(
                new byte[] { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d }).Dispose());
        return Task.CompletedTask;
    }

    private static Task EnforcesToolingJsonBudgets()
    {
        var objectOptions = new ToolingJsonOptions { MaximumMembersPerObject = 4 };
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject(
                "{\"a\":1,\"b\":2,\"c\":3,\"d\":4,\"e\":5}"u8.ToArray(),
                objectOptions).Dispose());

        var arrayOptions = new ToolingJsonOptions { MaximumItemsPerArray = 4 };
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject("{\"a\":[1,2,3,4,5]}"u8.ToArray(), arrayOptions).Dispose());

        var elementOptions = new ToolingJsonOptions { MaximumElementCount = 16 };
        AssertThrows<ToolingJsonException>(
            () => StrictToolingJson.ParseObject(
                "{\"a\":[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15]}"u8.ToArray(),
                elementOptions).Dispose());
        return Task.CompletedTask;
    }

    private static Task RoundTripsJsonRpcMessages()
    {
        var parameters = JsonSerializer.SerializeToElement(new { processId = 42, rootUri = "file:///orchard" });
        var request = JsonRpcCodec.Decode(JsonRpcCodec.EncodeRequest(7, "initialize", parameters));
        Assert(request.Kind == JsonRpcMessageKind.Request && request.Id?.Number == 7 &&
            request.Method == "initialize" && request.Parameters?.GetProperty("processId").GetInt32() == 42,
            "A JSON-RPC request did not round-trip.");

        var notification = JsonRpcCodec.Decode(JsonRpcCodec.EncodeNotification("initialized", parameters));
        Assert(notification.Kind == JsonRpcMessageKind.Notification && notification.Id is null,
            "A JSON-RPC notification gained an id.");

        var result = JsonSerializer.SerializeToElement(new { capabilities = new { hoverProvider = true } });
        var success = JsonRpcCodec.Decode(JsonRpcCodec.EncodeSuccess(JsonRpcId.FromString("server-1"), result));
        Assert(success.Kind == JsonRpcMessageKind.SuccessResponse && success.Id?.Text == "server-1" &&
            success.Result?.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean() == true,
            "A JSON-RPC success response did not round-trip.");

        var nullSuccess = JsonRpcCodec.Decode(JsonRpcCodec.EncodeSuccess(JsonRpcId.FromNumber(8)));
        Assert(nullSuccess.Result?.ValueKind == JsonValueKind.Null,
            "An absent success value was not encoded as an explicit JSON null result.");

        var error = new JsonRpcError
        {
            Code = -32601,
            Message = "Method not found",
            Data = JsonSerializer.SerializeToElement(new { method = "orchard/missing" })
        };
        var failure = JsonRpcCodec.Decode(JsonRpcCodec.EncodeError(JsonRpcId.FromNumber(9), error));
        Assert(failure.Kind == JsonRpcMessageKind.ErrorResponse && failure.Error?.Code == -32601 &&
            failure.Error.Data?.GetProperty("method").GetString() == "orchard/missing",
            "A JSON-RPC error response did not round-trip.");
        return Task.CompletedTask;
    }

    private static Task RejectsAmbiguousJsonRpcMessages()
    {
        string[] rejected =
        [
            "{\"jsonrpc\":\"2.1\",\"id\":1,\"result\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"x\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":1.5,\"result\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"params\":1}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null,\"error\":{\"code\":1,\"message\":\"x\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"result\":null}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"x\\nowned\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":1,\"message\":\"x\\u202e\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"result\":null}"
        ];
        foreach (var value in rejected)
        {
            AssertThrows<ToolingJsonException>(() => JsonRpcCodec.Decode(Encoding.UTF8.GetBytes(value)));
        }

        return Task.CompletedTask;
    }

    private static Task RejectsUnsafeJsonRpcOutput()
    {
        AssertThrows<ArgumentException>(
            () => JsonRpcCodec.EncodeNotification("window/logMessage\nContent-Length: 0"));
        AssertThrows<ArgumentException>(
            () => JsonRpcCodec.EncodeRequest(1, "initialize", JsonSerializer.SerializeToElement(42)));
        AssertThrows<ArgumentException>(
            () => JsonRpcCodec.EncodeError(
                JsonRpcId.FromString("unsafe\u202e"),
                new JsonRpcError { Code = -1, Message = "safe" }));
        AssertThrows<ArgumentException>(
            () => JsonRpcCodec.EncodeError(
                JsonRpcId.FromNumber(1),
                new JsonRpcError { Code = -1, Message = "unsafe\nmessage" }));
        return Task.CompletedTask;
    }

    private static Task ValidatesPolicyBounds()
    {
        AssertThrows<ArgumentOutOfRangeException>(
            () => GC.KeepAlive(new ContentLengthMessageChannel(
                    Stream.Null,
                    Stream.Null,
                    new ContentLengthProtocolOptions { MaximumHeaderBytes = 63 },
                    leaveOpen: true)));
        AssertThrows<ArgumentOutOfRangeException>(
            () => GC.KeepAlive(new ContentLengthMessageChannel(
                    Stream.Null,
                    Stream.Null,
                    new ContentLengthProtocolOptions { MaximumContentBytes = 65 * 1024 * 1024 },
                    leaveOpen: true)));
        AssertThrows<ArgumentOutOfRangeException>(
            () => GC.KeepAlive(new ContentLengthMessageChannel(
                    Stream.Null,
                    Stream.Null,
                    new ContentLengthProtocolOptions { OperationTimeout = TimeSpan.Zero },
                    leaveOpen: true)));
        return Task.CompletedTask;
    }

    private static byte[] Envelope(
        ReadOnlySpan<byte> content,
        string contentLengthName = "Content-Length",
        string? additionalHeader = null)
    {
        var header = additionalHeader is null
            ? $"{contentLengthName}: {content.Length}\r\n\r\n"
            : $"{additionalHeader}\r\n{contentLengthName}: {content.Length}\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(header), .. content];
    }

    private static async Task AssertReadThrows<TException>(
        byte[] bytes,
        ContentLengthProtocolOptions? options = null)
        where TException : Exception
    {
        await using var input = new MemoryStream(bytes, writable: false);
        await using var channel = new ContentLengthMessageChannel(input, Stream.Null, options, leaveOpen: true);
        await AssertThrowsAsync<TException>(() => channel.ReadAsync().AsTask()).ConfigureAwait(false);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> body)
        where TException : Exception
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void AssertThrows<TException>(Action body)
        where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FragmentingReadStream(byte[] bytes, int maximumChunk) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var length = Math.Min(Math.Min(buffer.Length, maximumChunk), bytes.Length - _offset);
            if (length == 0)
            {
                return 0;
            }

            bytes.AsSpan(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DribbleReadStream(byte[] bytes, TimeSpan delay) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (_offset == bytes.Length)
            {
                return 0;
            }

            buffer.Span[0] = bytes[_offset++];
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private sealed class DelayingCaptureStream : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
