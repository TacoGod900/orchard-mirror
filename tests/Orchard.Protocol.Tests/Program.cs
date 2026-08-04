using System.Buffers.Binary;
using System.Text;
using Orchard.Protocol;

namespace Orchard.Protocol.Tests;

internal static class Program
{
    private const string SessionId = "session-0001";

    private static readonly (string Name, Func<Task> Body)[] Tests =
    [
        ("round-trips every version 1 message type", Run(AllMessageTypesRoundTrip)),
        ("serializes a deterministic canonical envelope", Run(SerializesCanonicalEnvelope)),
        ("writes a big-endian length-prefixed frame", Run(WritesBigEndianFrame)),
        ("reads a frame from a fragmented stream", Run(ReadsFragmentedFrame)),
        ("round-trips a frame asynchronously", AsyncFrameRoundTrip),
        ("rejects an empty frame", Run(RejectsEmptyFrame)),
        ("rejects an oversized declared frame before allocation", Run(RejectsOversizedDeclaredFrame)),
        ("rejects an oversized serialized frame", Run(RejectsOversizedSerializedFrame)),
        ("rejects a truncated frame header", Run(RejectsTruncatedHeader)),
        ("rejects a truncated frame payload", Run(RejectsTruncatedPayload)),
        ("rejects invalid UTF-8", Run(RejectsInvalidUtf8)),
        ("rejects malformed JSON", Run(RejectsMalformedJson)),
        ("rejects duplicate envelope properties", Run(RejectsDuplicateEnvelopeProperty)),
        ("rejects duplicate nested properties", Run(RejectsDuplicateNestedProperty)),
        ("rejects unknown envelope properties", Run(RejectsUnknownEnvelopeProperty)),
        ("rejects unknown payload properties", Run(RejectsUnknownPayloadProperty)),
        ("rejects missing envelope properties", Run(RejectsMissingEnvelopeProperty)),
        ("treats JSON property names as case-sensitive", Run(RejectsWrongPropertyCase)),
        ("treats enum values as case-sensitive", Run(RejectsWrongEnumCase)),
        ("rejects an unknown message type", Run(RejectsUnknownMessageType)),
        ("rejects integer enum encodings", Run(RejectsIntegerEnum)),
        ("rejects an unsupported envelope version", Run(RejectsUnsupportedEnvelopeVersion)),
        ("rejects an envelope and payload type mismatch", Run(RejectsTypeMismatch)),
        ("rejects a negative sequence", Run(RejectsNegativeSequence)),
        ("negotiates the highest common protocol version", Run(NegotiatesHighestCommonVersion)),
        ("rejects protocol ranges with no overlap", Run(RejectsNoCommonVersion)),
        ("rejects invalid protocol ranges", Run(RejectsInvalidVersionRange)),
        ("accepts a render tree at the depth limit", Run(AcceptsTreeAtDepthLimit)),
        ("rejects a render tree beyond the depth limit", Run(RejectsTreeBeyondDepthLimit)),
        ("accepts a render tree at the node limit", Run(AcceptsTreeAtNodeLimit)),
        ("rejects a render tree beyond the node limit", Run(RejectsTreeBeyondNodeLimit)),
        ("rejects duplicate node identifiers", Run(RejectsDuplicateNodeIds)),
        ("rejects a non-allowlisted node kind", Run(RejectsUnknownNodeKind)),
        ("rejects a non-allowlisted property", Run(RejectsUnknownProperty)),
        ("rejects a non-allowlisted event", Run(RejectsUnknownEvent)),
        ("rejects duplicate events on a node", Run(RejectsDuplicateEvents)),
        ("bounds strings by UTF-8 bytes", Run(BoundsStringsByUtf8Bytes)),
        ("rejects invalid Unicode in an in-memory payload", Run(RejectsInvalidUnicode)),
        ("bounds authentication tokens", Run(BoundsAuthenticationToken)),
        ("validates simulator configuration ranges", Run(ValidatesConfigurationRanges)),
        ("validates event-result consistency", Run(ValidatesEventResultConsistency)),
        ("validates pong timestamp ordering", Run(ValidatesPongTimestampOrdering)),
        ("rejects null render-node collections", Run(RejectsNullNodeCollections)),
        ("terminal-escapes peer-controlled protocol errors", Run(TerminalEscapesProtocolErrors))
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

    private static void AllMessageTypesRoundTrip()
    {
        IProtocolPayload[] payloads =
        [
            new AuthenticatePayload { Token = "secret-token", ClientNonce = "nonce-1" },
            new HelloPayload
            {
                MinimumVersion = 1,
                MaximumVersion = 1,
                Role = ProtocolPeerRole.Application,
                Capabilities = ["render-v1", "events-v1"]
            },
            new ConfigurePayload
            {
                DeviceProfileId = "iphone-15-pro",
                LogicalWidth = 393,
                LogicalHeight = 852,
                DisplayScale = 3,
                Appearance = SimulatorAppearance.Dark,
                Locale = "en-AU",
                AccessibilityEnabled = true
            },
            new RenderPayload { Revision = 7, Root = SampleTree() },
            new EventPayload
            {
                EventId = "event-9",
                RenderRevision = 7,
                NodeId = "continue",
                Event = "press",
                Value = "clicked"
            },
            new EventResultPayload
            {
                EventId = "event-9",
                Accepted = false,
                RenderRevision = 7,
                ErrorCode = "stale-event",
                Message = "The event targeted an old render."
            },
            new DiagnosticPayload
            {
                Code = "ORP-DEMO-1",
                Severity = ProtocolDiagnosticSeverity.Warning,
                Message = "Preview differs from the validation device.",
                NodeId = "continue",
                RenderRevision = 7
            },
            new PingPayload { Nonce = "ping-11", SentAtUnixMilliseconds = 1_700_000_000_000 },
            new PongPayload
            {
                Nonce = "ping-11",
                SentAtUnixMilliseconds = 1_700_000_000_000,
                RespondedAtUnixMilliseconds = 1_700_000_000_010
            },
            new ShutdownPayload
            {
                Disposition = ShutdownDisposition.Restart,
                Reason = "Replacing the child after a successful build.",
                ExitCode = 0
            }
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var original = ProtocolEnvelope.Create(SessionId, index, payloads[index]);
            var encoded = ProtocolJson.Serialize(original);
            var restored = ProtocolJson.Deserialize(encoded);
            var reencoded = ProtocolJson.Serialize(restored);

            Assert.Equal(original.Type, restored.Type);
            Assert.Equal(original.Payload.GetType(), restored.Payload.GetType());
            Assert.SequenceEqual(encoded, reencoded);
        }
    }

    private static void SerializesCanonicalEnvelope()
    {
        var envelope = ProtocolEnvelope.Create(
            SessionId,
            3,
            new PingPayload { Nonce = "abc", SentAtUnixMilliseconds = 42 });

        var json = Encoding.UTF8.GetString(ProtocolJson.Serialize(envelope));
        Assert.Equal(
            "{\"version\":1,\"type\":\"ping\",\"sessionId\":\"session-0001\",\"sequence\":3,\"payload\":{\"nonce\":\"abc\",\"sentAtUnixMilliseconds\":42}}",
            json);
    }

    private static void WritesBigEndianFrame()
    {
        var envelope = ValidPing();
        var expectedPayload = ProtocolJson.Serialize(envelope);
        using var stream = new MemoryStream();
        ProtocolFrameCodec.Write(stream, envelope);
        var framed = stream.ToArray();

        Assert.Equal(expectedPayload.Length + 4, framed.Length);
        Assert.Equal((uint)expectedPayload.Length, BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(0, 4)));
        Assert.SequenceEqual(expectedPayload, framed.AsSpan(4).ToArray());
    }

    private static void ReadsFragmentedFrame()
    {
        using var source = new MemoryStream();
        ProtocolFrameCodec.Write(source, ValidPing());
        using var fragmented = new FragmentedReadStream(source.ToArray(), 2);
        var restored = ProtocolFrameCodec.Read(fragmented);
        Assert.Equal(ProtocolMessageTypes.Ping, restored.Type);
    }

    private static async Task AsyncFrameRoundTrip()
    {
        await using var stream = new MemoryStream();
        await ProtocolFrameCodec.WriteAsync(stream, ValidPing()).ConfigureAwait(false);
        stream.Position = 0;
        var restored = await ProtocolFrameCodec.ReadAsync(stream).ConfigureAwait(false);
        Assert.Equal(ProtocolMessageTypes.Ping, restored.Type);
        Assert.Equal(4L, restored.Sequence);
    }

    private static void RejectsEmptyFrame()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidFrame,
            () => ProtocolFrameCodec.Read(new MemoryStream([0, 0, 0, 0])));
    }

    private static void RejectsOversizedDeclaredFrame()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, ProtocolConstants.MaximumFrameBytes + 1U);
        AssertProtocolError(
            ProtocolErrorCodes.FrameTooLarge,
            () => ProtocolFrameCodec.Read(new MemoryStream(header)));
    }

    private static void RejectsOversizedSerializedFrame()
    {
        var largeText = new string('x', ProtocolConstants.MaximumPropertyValueBytes);
        var children = Enumerable.Range(0, 66)
            .Select(index => Node($"node-{index}", properties: new Dictionary<string, string> { ["text"] = largeText }))
            .ToArray();
        var root = Node("root", "root", children: children);
        AssertProtocolError(
            ProtocolErrorCodes.FrameTooLarge,
            () => ProtocolJson.Serialize(RenderEnvelope(root)));
    }

    private static void RejectsTruncatedHeader()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidFrame,
            () => ProtocolFrameCodec.Read(new MemoryStream([0, 0, 1])));
    }

    private static void RejectsTruncatedPayload()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidFrame,
            () => ProtocolFrameCodec.Read(new MemoryStream([0, 0, 0, 2, (byte)'{'])));
    }

    private static void RejectsInvalidUtf8()
    {
        AssertProtocolError(ProtocolErrorCodes.InvalidUtf8OrJson, () => ProtocolJson.Deserialize([0xff]));
    }

    private static void RejectsMalformedJson()
    {
        AssertProtocolError(ProtocolErrorCodes.InvalidUtf8OrJson, () => Decode("{\"version\":"));
    }

    private static void RejectsDuplicateEnvelopeProperty()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidEnvelope,
            () => Decode("{\"version\":1,\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"nonce\":\"n\",\"sentAtUnixMilliseconds\":0}}"));
    }

    private static void RejectsDuplicateNestedProperty()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidEnvelope,
            () => Decode("{\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"nonce\":\"n\",\"nonce\":\"n2\",\"sentAtUnixMilliseconds\":0}}"));
    }

    private static void RejectsUnknownEnvelopeProperty()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidEnvelope,
            () => Decode("{\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"nonce\":\"n\",\"sentAtUnixMilliseconds\":0},\"extra\":true}"));
    }

    private static void RejectsUnknownPayloadProperty()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidUtf8OrJson,
            () => Decode("{\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"nonce\":\"n\",\"sentAtUnixMilliseconds\":0,\"extra\":true}}"));
    }

    private static void RejectsMissingEnvelopeProperty()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidEnvelope,
            () => Decode("{\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"payload\":{\"nonce\":\"n\",\"sentAtUnixMilliseconds\":0}}"));
    }

    private static void RejectsWrongPropertyCase()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidUtf8OrJson,
            () => Decode("{\"version\":1,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"Nonce\":\"n\",\"sentAtUnixMilliseconds\":0}}"));
    }

    private static void RejectsWrongEnumCase()
    {
        var invalidEnvelopes = new[]
        {
            "{\"version\":1,\"type\":\"hello\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"minimumVersion\":1,\"maximumVersion\":1,\"role\":\"Host\",\"capabilities\":[]}}",
            "{\"version\":1,\"type\":\"configure\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"deviceProfileId\":\"phone\",\"logicalWidth\":1,\"logicalHeight\":1,\"displayScale\":1,\"appearance\":\"Dark\",\"locale\":\"en\",\"accessibilityEnabled\":false}}",
            "{\"version\":1,\"type\":\"diagnostic\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"code\":\"ORP1001\",\"severity\":\"Warning\",\"message\":\"invalid\"}}",
            "{\"version\":1,\"type\":\"shutdown\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"disposition\":\"Normal\",\"reason\":\"done\"}}"
        };

        foreach (var json in invalidEnvelopes)
        {
            AssertProtocolError(ProtocolErrorCodes.InvalidUtf8OrJson, () => Decode(json));
        }
    }

    private static void RejectsUnknownMessageType()
    {
        AssertProtocolError(
            ProtocolErrorCodes.UnknownMessageType,
            () => Decode("{\"version\":1,\"type\":\"execute\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{}}"));
    }

    private static void RejectsIntegerEnum()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidUtf8OrJson,
            () => Decode("{\"version\":1,\"type\":\"shutdown\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"disposition\":0,\"reason\":\"done\"}}"));
    }

    private static void RejectsUnsupportedEnvelopeVersion()
    {
        AssertProtocolError(
            ProtocolErrorCodes.UnsupportedVersion,
            () => Decode("{\"version\":2,\"type\":\"ping\",\"sessionId\":\"s\",\"sequence\":0,\"payload\":{\"nonce\":\"n\",\"sentAtUnixMilliseconds\":0}}"));
    }

    private static void RejectsTypeMismatch()
    {
        var envelope = new ProtocolEnvelope
        {
            Type = ProtocolMessageTypes.Pong,
            SessionId = SessionId,
            Sequence = 0,
            Payload = new PingPayload { Nonce = "n", SentAtUnixMilliseconds = 0 }
        };
        AssertProtocolError(ProtocolErrorCodes.InvalidPayload, () => ProtocolValidator.Validate(envelope));
    }

    private static void RejectsNegativeSequence()
    {
        var envelope = ProtocolEnvelope.Create(
            SessionId,
            -1,
            new PingPayload { Nonce = "n", SentAtUnixMilliseconds = 0 });
        AssertProtocolError(ProtocolErrorCodes.InvalidPayload, () => ProtocolValidator.Validate(envelope));
    }

    private static void NegotiatesHighestCommonVersion()
    {
        Assert.Equal(3, ProtocolNegotiator.Negotiate(1, 4, 2, 3));
        Assert.Equal(
            1,
            ProtocolNegotiator.Negotiate(new HelloPayload
            {
                MinimumVersion = 1,
                MaximumVersion = 2,
                Role = ProtocolPeerRole.Application
            }));
    }

    private static void RejectsNoCommonVersion()
    {
        AssertProtocolError(ProtocolErrorCodes.NoCommonVersion, () => ProtocolNegotiator.Negotiate(1, 1, 2, 3));
    }

    private static void RejectsInvalidVersionRange()
    {
        AssertProtocolError(ProtocolErrorCodes.InvalidPayload, () => ProtocolNegotiator.Negotiate(2, 1, 1, 1));
        AssertProtocolError(ProtocolErrorCodes.InvalidPayload, () => ProtocolNegotiator.Negotiate(1, 1, 0, 1));
    }

    private static void AcceptsTreeAtDepthLimit()
    {
        var tree = Chain(ProtocolConstants.MaximumTreeDepth);
        ProtocolValidator.Validate(RenderEnvelope(tree));
        var restored = ProtocolJson.Deserialize(ProtocolJson.Serialize(RenderEnvelope(tree)));
        Assert.Equal(ProtocolMessageTypes.Render, restored.Type);
    }

    private static void RejectsTreeBeyondDepthLimit()
    {
        AssertProtocolError(
            ProtocolErrorCodes.LimitExceeded,
            () => ProtocolValidator.Validate(RenderEnvelope(Chain(ProtocolConstants.MaximumTreeDepth + 1))));
    }

    private static void AcceptsTreeAtNodeLimit()
    {
        var children = Enumerable.Range(1, ProtocolConstants.MaximumNodeCount - 1)
            .Select(index => Node($"node-{index}"))
            .ToArray();
        ProtocolValidator.Validate(RenderEnvelope(Node("root", "root", children: children)));
    }

    private static void RejectsTreeBeyondNodeLimit()
    {
        var children = Enumerable.Range(1, ProtocolConstants.MaximumNodeCount)
            .Select(index => Node($"node-{index}"))
            .ToArray();
        AssertProtocolError(
            ProtocolErrorCodes.LimitExceeded,
            () => ProtocolValidator.Validate(RenderEnvelope(Node("root", "root", children: children))));
    }

    private static void RejectsDuplicateNodeIds()
    {
        var root = Node("same", "root", children: [Node("same")]);
        AssertProtocolError(
            ProtocolErrorCodes.InvalidTree,
            () => ProtocolValidator.Validate(RenderEnvelope(root)));
    }

    private static void RejectsUnknownNodeKind()
    {
        AssertProtocolError(
            ProtocolErrorCodes.InvalidTree,
            () => ProtocolValidator.Validate(RenderEnvelope(Node("root", "webView"))));
    }

    private static void RejectsUnknownProperty()
    {
        var root = Node("root", properties: new Dictionary<string, string> { ["launchExecutable"] = "calc.exe" });
        AssertProtocolError(ProtocolErrorCodes.InvalidTree, () => ProtocolValidator.Validate(RenderEnvelope(root)));
    }

    private static void RejectsUnknownEvent()
    {
        var root = Node("root", events: ["execute"]);
        AssertProtocolError(ProtocolErrorCodes.InvalidTree, () => ProtocolValidator.Validate(RenderEnvelope(root)));
    }

    private static void RejectsDuplicateEvents()
    {
        var root = Node("root", events: ["press", "press"]);
        AssertProtocolError(ProtocolErrorCodes.InvalidTree, () => ProtocolValidator.Validate(RenderEnvelope(root)));
    }

    private static void BoundsStringsByUtf8Bytes()
    {
        var exact = new string('é', ProtocolConstants.MaximumPropertyValueBytes / 2);
        ProtocolValidator.Validate(RenderEnvelope(Node(
            "root",
            properties: new Dictionary<string, string> { ["text"] = exact })));

        var tooLong = exact + "é";
        AssertProtocolError(
            ProtocolErrorCodes.LimitExceeded,
            () => ProtocolValidator.Validate(RenderEnvelope(Node(
                "root",
                properties: new Dictionary<string, string> { ["text"] = tooLong }))));
    }

    private static void RejectsInvalidUnicode()
    {
        var unpairedSurrogate = new string(['\ud800']);
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(RenderEnvelope(Node(
                "root",
                properties: new Dictionary<string, string> { ["text"] = unpairedSurrogate }))));
    }

    private static void BoundsAuthenticationToken()
    {
        var valid = ProtocolEnvelope.Create(
            SessionId,
            0,
            new AuthenticatePayload
            {
                Token = new string('t', ProtocolConstants.MaximumAuthenticationTokenBytes),
                ClientNonce = "nonce"
            });
        ProtocolValidator.Validate(valid);

        var invalid = valid with
        {
            Payload = new AuthenticatePayload
            {
                Token = new string('t', ProtocolConstants.MaximumAuthenticationTokenBytes + 1),
                ClientNonce = "nonce"
            }
        };
        AssertProtocolError(ProtocolErrorCodes.LimitExceeded, () => ProtocolValidator.Validate(invalid));
    }

    private static void ValidatesConfigurationRanges()
    {
        var payload = new ConfigurePayload
        {
            DeviceProfileId = "phone",
            LogicalWidth = 0,
            LogicalHeight = 800,
            DisplayScale = 2,
            Appearance = SimulatorAppearance.Light,
            Locale = "en-AU",
            AccessibilityEnabled = false
        };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(ProtocolEnvelope.Create(SessionId, 0, payload)));

        payload = payload with { LogicalWidth = 400, DisplayScale = double.NaN };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(ProtocolEnvelope.Create(SessionId, 0, payload)));
    }

    private static void ValidatesEventResultConsistency()
    {
        var acceptedWithError = new EventResultPayload
        {
            EventId = "event-1",
            Accepted = true,
            RenderRevision = 1,
            ErrorCode = "unexpected"
        };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(ProtocolEnvelope.Create(SessionId, 0, acceptedWithError)));

        var rejectedWithoutError = acceptedWithError with { Accepted = false, ErrorCode = null };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(ProtocolEnvelope.Create(SessionId, 0, rejectedWithoutError)));
    }

    private static void ValidatesPongTimestampOrdering()
    {
        var payload = new PongPayload
        {
            Nonce = "nonce",
            SentAtUnixMilliseconds = 20,
            RespondedAtUnixMilliseconds = 19
        };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidPayload,
            () => ProtocolValidator.Validate(ProtocolEnvelope.Create(SessionId, 0, payload)));
    }

    private static void RejectsNullNodeCollections()
    {
        var root = new ProtocolViewNode
        {
            Id = "root",
            Kind = "root",
            Properties = null!,
            Events = [],
            Children = []
        };
        AssertProtocolError(
            ProtocolErrorCodes.InvalidTree,
            () => ProtocolValidator.Validate(RenderEnvelope(root)));
    }

    private static void TerminalEscapesProtocolErrors()
    {
        var wireException = Assert.Throws<ProtocolException>(() => Decode(
            "{\"version\":1,\"type\":\"evil\\u001B[31m\\n\",\"sessionId\":\"session-0001\"," +
            "\"sequence\":0,\"payload\":{}}"));
        AssertTerminalSafe(wireException.Message);
        if (!wireException.Message.Contains("\\u001B", StringComparison.Ordinal) ||
            !wireException.Message.Contains("\\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Peer-controlled terminal controls were not visibly escaped.");
        }

        var validationException = new ProtocolValidationException(
            "ORP1004",
            "$.peer\u202E\n",
            "bad\u001B\n\uD800");
        AssertTerminalSafe(validationException.Message);
        AssertTerminalSafe(validationException.Path);
        if (!validationException.Message.Contains("\\u202E", StringComparison.Ordinal) ||
            !validationException.Message.Contains("\\uD800", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Format markers or lone surrogates were not visibly escaped.");
        }

        var withUnsafeInner = new ProtocolException(
            "ORP1003",
            "Outer message.",
            new InvalidDataException("inner\u001B\n"));
        AssertTerminalSafe(withUnsafeInner.ToString());
        if (!withUnsafeInner.ToString().Contains("\\u001B", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ProtocolException.ToString exposed an unsafe inner exception.");
        }
    }

    private static void AssertTerminalSafe(string value)
    {
        if (value.Any(char.IsControl) || value.Contains('\u202E'))
        {
            throw new InvalidOperationException("A protocol exception exposed terminal control characters.");
        }
    }

    private static ProtocolEnvelope ValidPing() => ProtocolEnvelope.Create(
        SessionId,
        4,
        new PingPayload { Nonce = "ping-4", SentAtUnixMilliseconds = 100 });

    private static ProtocolEnvelope RenderEnvelope(ProtocolViewNode root) => ProtocolEnvelope.Create(
        SessionId,
        1,
        new RenderPayload { Revision = 1, Root = root });

    private static ProtocolViewNode SampleTree() => Node(
        "root",
        "vStack",
        children:
        [
            Node("title", properties: new Dictionary<string, string> { ["text"] = "Welcome" }),
            Node(
                "continue",
                "button",
                properties: new Dictionary<string, string> { ["text"] = "Continue" },
                events: ["press"])
        ]);

    private static ProtocolViewNode Chain(int depth)
    {
        var node = Node($"node-{depth}");
        for (var current = depth - 1; current >= 1; current--)
        {
            node = Node($"node-{current}", children: [node]);
        }

        return node;
    }

    private static ProtocolViewNode Node(
        string id,
        string kind = "text",
        IReadOnlyDictionary<string, string>? properties = null,
        IReadOnlyList<string>? events = null,
        IReadOnlyList<ProtocolViewNode>? children = null) => new()
        {
            Id = id,
            Kind = kind,
            Properties = properties ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Events = events ?? [],
            Children = children ?? []
        };

    private static ProtocolEnvelope Decode(string json) =>
        ProtocolJson.Deserialize(Encoding.UTF8.GetBytes(json));

    private static void AssertProtocolError(string expectedCode, Action action)
    {
        var exception = Assert.Throws<ProtocolException>(action);
        Assert.Equal(expectedCode, exception.Code);
    }

    private static Func<Task> Run(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
        }
    }

    public static void SequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Expected byte sequences to be equal.");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}

internal sealed class FragmentedReadStream(byte[] contents, int maximumReadSize) : MemoryStream(contents)
{
    public override int Read(Span<byte> buffer) =>
        base.Read(buffer[..Math.Min(buffer.Length, maximumReadSize)]);

    public override int Read(byte[] buffer, int offset, int count) =>
        base.Read(buffer, offset, Math.Min(count, maximumReadSize));
}
