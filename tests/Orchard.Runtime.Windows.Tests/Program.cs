using Orchard.Protocol;
using Orchard.Runtime.Windows;

namespace Orchard.Runtime.Windows.Tests;

internal static class Program
{
    private static readonly (string Name, Action Body)[] Tests =
    [
        ("classifies every version-1 kind with truthful bootstrap fidelity", ProjectsProtocolKinds),
        ("creates only exposed semantic events", CreatesSemanticEvents),
        ("does not claim or dispatch unwired renderer events", ReportsRendererEventSupport),
        ("serializes edit then press against increasing current revisions", SerializesEditThenPress),
        ("coalesces pending changes without crossing a press", CoalescesPendingChanges),
        ("never interprets Swift-looking action text", DoesNotInterpretActionBodies),
        ("publishes only increasing accepted revisions", PublishesIncreasingRevisions),
        ("rejects malformed replacement revisions", RejectsMalformedRevisions),
        ("ignores stale results without replacing the tree", IgnoresStaleResults),
        ("sanitizes and bounds control and log text", SanitizesText),
        ("classifies future node kinds as unsupported", HandlesUnsupportedKinds),
        ("strictly parses native launch paths", ParsesNativeLaunchPaths),
        ("rejects ambiguous or unsafe launch arguments", RejectsUnsafeLaunchArguments)
    ];

    private static int Main()
    {
        var failures = new List<string>();
        foreach (var (name, body) in Tests)
        {
            try
            {
                body();
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

    private static void ProjectsProtocolKinds()
    {
        var placeholders = new HashSet<string>(StringComparer.Ordinal)
        {
            "image",
            "scrollView",
            "list",
            "navigationStack",
            "progressView"
        };
        var kinds = ProtocolAllowlists.NodeKinds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var children = kinds.Select((kind, index) => Node(
            $"node-{index}",
            kind,
            properties: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = $"{kind}\u202E\nlabel",
                ["enabled"] = "true",
                ["padding"] = "12"
            })).ToArray();
        var projected = LiveTreeProjector.Project(Render(1, Node("root", "root", children: children)));
        var projectedChildren = projected.Root.Children;
        Assert(projectedChildren.Count == kinds.Length, "Projection changed the child count.");
        for (var index = 0; index < kinds.Length; index++)
        {
            Assert(projectedChildren[index].ProtocolKind == kinds[index], "Projection changed protocol kind order.");
            var expectedSupport = placeholders.Contains(kinds[index])
                ? LiveRendererSupport.PlaceholderOnly
                : LiveRendererSupport.BootstrapApproximation;
            Assert(projectedChildren[index].RendererSupport == expectedSupport,
                $"Version-1 kind {kinds[index]} received an inaccurate renderer-fidelity claim.");
            Assert(projectedChildren[index].RendererSupported ==
                (expectedSupport == LiveRendererSupport.BootstrapApproximation),
                $"Version-1 kind {kinds[index]} exposed an inconsistent supported flag.");
            Assert(!projectedChildren[index].Property("text").Contains('\u202E'), "Bidi formatting reached control text.");
            Assert(!projectedChildren[index].Property("text").Contains('\n'), "Control text retained a control character.");
            Assert(projectedChildren[index].BooleanProperty("enabled", false), "Boolean property was not projected.");
            Assert(projectedChildren[index].IntegerProperty("padding", 0, 0, 128) == 12,
                "Integer property was not projected.");
        }
    }

    private static void ReportsRendererEventSupport()
    {
        var projected = LiveTreeProjector.Project(Render(
            1,
            Node(
                "root",
                "root",
                children:
                [
                    Node("button", "button", events: ["press", "focus"]),
                    Node("field", "textField", events: ["change", "submit", "focus", "blur"]),
                    Node("image", "image", events: ["press"])
                ])));

        var button = projected.FindNode("button")!;
        Assert(button.RendererSupportsEvent("press"), "The wired button press was not reported.");
        Assert(button.SupportsEvent("focus") && !button.RendererSupportsEvent("focus"),
            "An exposed but unwired button focus event was claimed as dispatchable.");
        var field = projected.FindNode("field")!;
        Assert(field.RendererSupportsEvent("change") && field.RendererSupportsEvent("submit"),
            "Wired text-field events were not reported.");
        Assert(field.RendererUnsupportedEvents.SequenceEqual(["focus", "blur"]),
            "Unwired text-field events were not disclosed in protocol order.");
        var image = projected.FindNode("image")!;
        Assert(image.RendererSupport == LiveRendererSupport.PlaceholderOnly &&
            !image.RendererSupportsEvent("press"),
            "The image placeholder claimed interactive support.");

        AssertThrows<ArgumentException>(
            () => LiveEventFactory.Create(projected, "event-focus", "button", "focus"),
            "An exposed event with no renderer hookup was dispatched.");
    }

    private static void SerializesEditThenPress() =>
        SerializesEditThenPressAsync().GetAwaiter().GetResult();

    private static async Task SerializesEditThenPressAsync()
    {
        var current = InteractiveRender(1);
        var observed = new List<(string Event, long Revision, string? Value)>();
        var eventSequence = 0;
        await using var queue = new LiveSemanticIntentScheduler(
            (intent, _) =>
            {
                var payload = LiveEventFactory.Create(
                    current,
                    $"queue-{++eventSequence}",
                    intent.NodeId,
                    intent.Event,
                    intent.Value);
                observed.Add((payload.Event, payload.RenderRevision, payload.Value));
                current = InteractiveRender(current.Revision + 1);
                return Task.FromResult(new LiveRenderUpdate(
                    LiveRenderUpdateKind.Applied,
                    current,
                    "ORU0000",
                    "fake update"));
            },
            (_, _) => Task.CompletedTask,
            (_, exception) => Task.FromException(exception));

        Assert(queue.Enqueue(new LiveSemanticIntent("field", "change", "Ada")) != LiveIntentEnqueueResult.Rejected,
            "The edit intent was rejected.");
        Assert(queue.Enqueue(new LiveSemanticIntent("button", "press")) != LiveIntentEnqueueResult.Rejected,
            "The press intent was rejected.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WhenIdleAsync(deadline.Token);

        Assert(observed.Count == 2, "The edit or following press was dropped.");
        Assert(observed[0] == ("change", 1, "Ada") && observed[1] == ("press", 2, null),
            "Queued intents were not resolved in order against the latest render revision.");
    }

    private static void CoalescesPendingChanges() =>
        CoalescesPendingChangesAsync().GetAwaiter().GetResult();

    private static async Task CoalescesPendingChangesAsync()
    {
        var firstDispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<LiveSemanticIntent>();
        await using var queue = new LiveSemanticIntentScheduler(
            async (intent, cancellationToken) =>
            {
                observed.Add(intent);
                if (observed.Count == 1)
                {
                    firstDispatchStarted.SetResult();
                    await releaseFirstDispatch.Task.WaitAsync(cancellationToken);
                }

                return new LiveRenderUpdate(LiveRenderUpdateKind.Applied, InteractiveRender(observed.Count + 1),
                    "ORU0000", "fake update");
            },
            (_, _) => Task.CompletedTask,
            (_, exception) => Task.FromException(exception));

        queue.Enqueue(new LiveSemanticIntent("button", "press"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await firstDispatchStarted.Task.WaitAsync(deadline.Token);
        Assert(queue.Enqueue(new LiveSemanticIntent("slider", "change", "0.1")) == LiveIntentEnqueueResult.Enqueued,
            "The first pending slider value was not queued.");
        Assert(queue.Enqueue(new LiveSemanticIntent("slider", "change", "0.8")) == LiveIntentEnqueueResult.Coalesced,
            "The pending slider value was not coalesced.");
        queue.Enqueue(new LiveSemanticIntent("button", "press"));
        releaseFirstDispatch.SetResult();
        await queue.WhenIdleAsync(deadline.Token);

        Assert(observed.Count == 3, "The queue did not retain the non-coalescible presses.");
        Assert(observed[0].Event == "press" && observed[1].Value == "0.8" && observed[2].Event == "press",
            "Change coalescing crossed or discarded an ordered press boundary.");
    }

    private static void CreatesSemanticEvents()
    {
        var textField = Node("field", "textField", events: ["change", "submit"]);
        var render = LiveTreeProjector.Project(Render(1, Node("root", "root", children: [textField])));
        var change = LiveEventFactory.Create(render, "event-1", "field", "change", "Ada\0Lovelace");
        Assert(change.Event == "change" && change.RenderRevision == 1 && change.NodeId == "field",
            "The semantic change event was projected incorrectly.");
        Assert(change.Value == "Ada�Lovelace", "Unsafe event text was not sanitized before dispatch.");

        AssertThrows<ArgumentException>(
            () => LiveEventFactory.Create(render, "event-2", "field", "press"),
            "An event absent from the node was dispatched.");
        AssertThrows<ArgumentException>(
            () => LiveEventFactory.Create(render, "event-3", "missing", "change", "x"),
            "An event was dispatched to a node absent from the render.");
    }

    private static void DoesNotInterpretActionBodies()
    {
        const string swiftLookingText = "print(\"owned\") state = \"changed\"";
        var projected = LiveTreeProjector.Project(Render(
            1,
            Node(
                "root",
                "root",
                children:
                [
                    Node(
                        "button",
                        "button",
                        properties: new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = swiftLookingText },
                        events: ["press"])
                ])));
        var button = projected.FindNode("button")!;
        Assert(button.Property("text") == swiftLookingText, "Display text was unexpectedly parsed or rewritten.");
        Assert(button.Properties.Keys.All(name => name is not "body" and not "action"),
            "An executable action body entered the projected model.");
        var payload = LiveEventFactory.Create(projected, "event-press", "button", "press");
        Assert(payload.Event == "press" && payload.Value is null,
            "Button dispatch carried interpreted action content.");
    }

    private static void PublishesIncreasingRevisions()
    {
        var state = new LiveRenderState();
        var first = Render(1, Node("root", "root", children: [Node("button", "button", events: ["press"])]));
        Assert(state.AcceptInitial(first).Kind == LiveRenderUpdateKind.Applied, "Initial revision was not applied.");
        var request = new EventPayload
        {
            EventId = "event-1",
            RenderRevision = 1,
            NodeId = "button",
            Event = "press"
        };
        var result = new EventResultPayload
        {
            EventId = "event-1",
            Accepted = true,
            RenderRevision = 2
        };
        var replacement = Render(
            2,
            Node(
                "root",
                "root",
                children:
                [
                    Node("text", "text", properties: Text("new"), events: ["appear"]),
                    Node("button", "button", events: ["press"])
                ]));
        var update = state.ApplyEventResult(request, result, replacement);
        Assert(update.Kind == LiveRenderUpdateKind.Applied && state.Current?.Revision == 2,
            "An accepted increasing revision was not published.");

        var rejectedRequest = request with { EventId = "event-2", RenderRevision = 2, NodeId = "text", Event = "appear" };
        var rejected = new EventResultPayload
        {
            EventId = "event-2",
            Accepted = false,
            RenderRevision = 2,
            ErrorCode = "ORL1004",
            Message = "No appear handler."
        };
        update = state.ApplyEventResult(rejectedRequest, rejected, null);
        Assert(update.Kind == LiveRenderUpdateKind.Rejected && state.Current?.Revision == 2,
            "A rejected event changed the published revision.");
    }

    private static void RejectsMalformedRevisions()
    {
        var state = new LiveRenderState();
        Assert(state.AcceptInitial(Render(2, Node("root", "root"))).Kind == LiveRenderUpdateKind.Invalid,
            "A non-one initial revision was accepted.");
        Assert(state.Current is null, "An invalid initial revision was published.");
        Assert(state.AcceptInitial(Render(1, Node("root", "root", children: [Node("button", "button", events: ["press"])]))).Kind ==
            LiveRenderUpdateKind.Applied, "Valid initial revision was rejected after invalid input.");

        var request = new EventPayload
        {
            EventId = "event-1",
            RenderRevision = 1,
            NodeId = "button",
            Event = "press"
        };
        var sameRevision = new EventResultPayload
        {
            EventId = "event-1",
            Accepted = true,
            RenderRevision = 1
        };
        var update = state.ApplyEventResult(request, sameRevision, Render(1, Node("root", "root")));
        Assert(update.Kind == LiveRenderUpdateKind.Invalid && state.Current?.Revision == 1,
            "An accepted event replayed the current revision.");

        var mismatchedResult = sameRevision with { RenderRevision = 3 };
        update = state.ApplyEventResult(request, mismatchedResult, Render(2, Node("root", "root")));
        Assert(update.Kind == LiveRenderUpdateKind.Invalid && state.Current?.Revision == 1,
            "A mismatched event result and render were published.");
    }

    private static void IgnoresStaleResults()
    {
        var state = StateAtRevisionTwo();
        var staleRequest = new EventPayload
        {
            EventId = "old-event",
            RenderRevision = 1,
            NodeId = "button",
            Event = "press"
        };
        var staleResult = new EventResultPayload
        {
            EventId = "old-event",
            Accepted = true,
            RenderRevision = 1
        };
        var staleRender = Render(1, Node("root", "root", children: [Node("text-old", "text", properties: Text("old"))]));
        var update = state.ApplyEventResult(staleRequest, staleResult, staleRender);
        Assert(update.Kind == LiveRenderUpdateKind.IgnoredStale && state.Current?.Revision == 2,
            "A stale result replaced a newer tree.");
        Assert(state.Current?.FindNode("text-new") is not null, "The newer tree was not retained.");
    }

    private static void SanitizesText()
    {
        var safe = LiveUiText.SanitizeControlText("alpha\n\u202Eomega", 64);
        Assert(!safe.Contains('\n') && !safe.Contains('\u202E'), "Unsafe formatting remained in control text.");
        Assert(safe.Contains('�'), "Unsafe characters were not visibly replaced.");
        var bounded = LiveUiText.SanitizeControlText(new string('x', 100), 10);
        Assert(bounded.Length == 10 && bounded.EndsWith('…'), "Control text was not deterministically bounded.");
        var boundedSurrogate = LiveUiText.SanitizeControlText("😀x", 2);
        Assert(IsWellFormedUtf16(boundedSurrogate), "Control-text truncation split a UTF-16 surrogate pair.");

        var log = new LiveLogBuffer(256);
        for (var index = 0; index < 30; index++)
        {
            log.Append("child\nstdout", $"line-{index}-\u202E-{new string('x', 40)}");
        }
        Assert(log.Text.Length <= 256, "The UI log exceeded its configured bound.");
        Assert(!log.Text.Contains('\u202E'), "Bidi formatting remained in a log.");
        Assert(!log.Text.Contains("child\nstdout", StringComparison.Ordinal), "A log category injected a line break.");

        var surrogateBoundary = new LiveLogBuffer(256);
        surrogateBoundary.Append("x", "😀" + new string('b', 253));
        Assert(IsWellFormedUtf16(surrogateBoundary.Text), "Log tail trimming split a UTF-16 surrogate pair.");
    }

    private static void HandlesUnsupportedKinds()
    {
        Assert(LiveTreeProjector.ClassifyKind("futureProtocolNode") == LiveNodeKind.Unsupported,
            "A future kind was treated as a supported renderer kind.");
        AssertThrows<ProtocolValidationException>(
            () => LiveTreeProjector.Project(Render(1, Node("root", "futureProtocolNode"))),
            "An unallowlisted protocol node reached the view model.");
    }

    private static void ParsesNativeLaunchPaths()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process has no executable path.");
        var runtimeDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var request = RuntimeCommandLine.Parse(
            ["--native", executable, "--runtime-path", runtimeDirectory]);
        Assert(request is NativeLaunchRequest, "Native arguments did not select native mode.");
        var native = (NativeLaunchRequest)request;
        Assert(native.ExecutablePath == Path.GetFullPath(executable), "Executable path was not normalized.");
        Assert(native.RuntimeSearchPaths.SequenceEqual([Path.TrimEndingDirectorySeparator(runtimeDirectory)]),
            "Runtime path was not normalized.");

        request = RuntimeCommandLine.Parse([executable]);
        Assert(request is StaticIrLaunchRequest, "One file path no longer selects static IR mode.");
        var relativeStaticPath = Path.GetRelativePath(Environment.CurrentDirectory, executable);
        request = RuntimeCommandLine.Parse([relativeStaticPath]);
        Assert(request is StaticIrLaunchRequest relativeStatic &&
            relativeStatic.IrPath == Path.GetFullPath(relativeStaticPath),
            "Static IR mode no longer resolves an existing relative path.");
        Assert(RuntimeCommandLine.Parse([]) is WelcomeLaunchRequest, "Empty arguments no longer select welcome mode.");
    }

    private static void RejectsUnsafeLaunchArguments()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process has no executable path.");
        var runtimeDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        AssertThrows<ArgumentException>(
            () => RuntimeCommandLine.Parse(["--native", "relative.exe"]),
            "A relative native executable was accepted.");
        AssertThrows<ArgumentException>(
            () => RuntimeCommandLine.Parse(["--native", executable, "--child-argument", "payload"]),
            "An arbitrary child argument was accepted.");
        AssertThrows<ArgumentException>(
            () => RuntimeCommandLine.Parse(
                ["--native", executable, "--runtime-path", runtimeDirectory, "--runtime-path", runtimeDirectory]),
            "A duplicate runtime path was accepted.");
        AssertThrows<ArgumentException>(
            () => RuntimeCommandLine.Parse([executable, "ignored"]),
            "Static mode silently ignored an extra argument.");

        var separatorDirectory = Path.Combine(
            Path.GetTempPath(),
            $"orchard-runtime-path-{Guid.NewGuid():N};injected");
        Directory.CreateDirectory(separatorDirectory);
        try
        {
            AssertThrows<ArgumentException>(
                () => RuntimeCommandLine.Parse(
                    ["--native", executable, "--runtime-path", separatorDirectory]),
                "A runtime directory containing the PATH separator was accepted.");
        }
        finally
        {
            Directory.Delete(separatorDirectory);
        }
    }

    private static LiveRenderState StateAtRevisionTwo()
    {
        var state = new LiveRenderState();
        state.AcceptInitial(Render(1, Node("root", "root", children: [Node("button", "button", events: ["press"])])));
        var request = new EventPayload
        {
            EventId = "event-1",
            RenderRevision = 1,
            NodeId = "button",
            Event = "press"
        };
        var result = new EventResultPayload
        {
            EventId = "event-1",
            Accepted = true,
            RenderRevision = 2
        };
        state.ApplyEventResult(
            request,
            result,
            Render(2, Node("root", "root", children: [Node("text-new", "text", properties: Text("new"))])));
        return state;
    }

    private static Dictionary<string, string> Text(string value) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = value };

    private static LiveRenderViewModel InteractiveRender(long revision) =>
        LiveTreeProjector.Project(Render(
            revision,
            Node(
                "root",
                "root",
                children:
                [
                    Node("field", "textField", events: ["change", "submit"]),
                    Node("button", "button", events: ["press"]),
                    Node("slider", "slider", events: ["change"])
                ])));

    private static RenderPayload Render(long revision, ProtocolViewNode root) => new()
    {
        Revision = revision,
        Root = root
    };

    private static ProtocolViewNode Node(
        string id,
        string kind,
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

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
