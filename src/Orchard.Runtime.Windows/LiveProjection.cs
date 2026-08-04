using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Orchard.Protocol;

namespace Orchard.Runtime.Windows;

public enum LiveNodeKind
{
    Root,
    Text,
    Button,
    TextField,
    SecureField,
    VStack,
    HStack,
    ZStack,
    Group,
    Conditional,
    Spacer,
    Divider,
    Image,
    ScrollView,
    List,
    NavigationStack,
    Toggle,
    Slider,
    ProgressView,
    Unsupported
}

/// <summary>Truthful capability level of the current WinForms feasibility renderer.</summary>
public enum LiveRendererSupport
{
    BootstrapApproximation,
    PlaceholderOnly,
    Unsupported
}

public static class LiveRendererProfile
{
    public static LiveRendererSupport SupportFor(LiveNodeKind kind) => kind switch
    {
        LiveNodeKind.Root or LiveNodeKind.Text or LiveNodeKind.Button or LiveNodeKind.TextField or
            LiveNodeKind.SecureField or LiveNodeKind.VStack or LiveNodeKind.HStack or LiveNodeKind.ZStack or
            LiveNodeKind.Group or LiveNodeKind.Conditional or LiveNodeKind.Spacer or LiveNodeKind.Divider or
            LiveNodeKind.Toggle or LiveNodeKind.Slider => LiveRendererSupport.BootstrapApproximation,
        LiveNodeKind.Image or LiveNodeKind.ScrollView or LiveNodeKind.List or LiveNodeKind.NavigationStack or
            LiveNodeKind.ProgressView => LiveRendererSupport.PlaceholderOnly,
        _ => LiveRendererSupport.Unsupported
    };

    public static bool SupportsEvent(LiveNodeKind kind, string eventName) => kind switch
    {
        LiveNodeKind.Button => string.Equals(eventName, "press", StringComparison.Ordinal),
        LiveNodeKind.TextField or LiveNodeKind.SecureField =>
            eventName is "change" or "submit",
        LiveNodeKind.Toggle or LiveNodeKind.Slider =>
            string.Equals(eventName, "change", StringComparison.Ordinal),
        _ => false
    };

    public static string DescriptionFor(LiveNodeKind kind) => SupportFor(kind) switch
    {
        LiveRendererSupport.BootstrapApproximation =>
            "Rendered by a WinForms bootstrap approximation; this is not UIKit or SwiftUI equivalence.",
        LiveRendererSupport.PlaceholderOnly =>
            "Only a labelled placeholder or flattened child projection is available in this bootstrap renderer.",
        _ => "This node has no local bootstrap renderer."
    };
}

public sealed record LiveNodeViewModel
{
    public required string Id { get; init; }

    public required LiveNodeKind Kind { get; init; }

    public required string ProtocolKind { get; init; }

    public required IReadOnlyDictionary<string, string> Properties { get; init; }

    public required IReadOnlyList<string> Events { get; init; }

    public required IReadOnlyList<LiveNodeViewModel> Children { get; init; }

    public LiveRendererSupport RendererSupport => LiveRendererProfile.SupportFor(Kind);

    public bool RendererSupported => RendererSupport == LiveRendererSupport.BootstrapApproximation;

    public string RendererSupportDescription => LiveRendererProfile.DescriptionFor(Kind);

    public bool SupportsEvent(string eventName) =>
        Events.Contains(eventName, StringComparer.Ordinal);

    public bool RendererSupportsEvent(string eventName) =>
        SupportsEvent(eventName) && LiveRendererProfile.SupportsEvent(Kind, eventName);

    public IReadOnlyList<string> RendererUnsupportedEvents =>
        Events.Where(eventName => !LiveRendererProfile.SupportsEvent(Kind, eventName)).ToArray();

    public string Property(string name, string fallback = "") =>
        Properties.TryGetValue(name, out var value) ? value : fallback;

    public bool BooleanProperty(string name, bool fallback)
    {
        if (!Properties.TryGetValue(name, out var value))
        {
            return fallback;
        }

        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public int IntegerProperty(string name, int fallback, int minimum, int maximum)
    {
        if (!Properties.TryGetValue(name, out var value) ||
            !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }

    public decimal DecimalProperty(string name, decimal fallback, decimal minimum, decimal maximum)
    {
        if (!Properties.TryGetValue(name, out var value) ||
            !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, minimum, maximum);
    }
}

public sealed record LiveRenderViewModel(long Revision, LiveNodeViewModel Root)
{
    public LiveNodeViewModel? FindNode(string nodeId)
    {
        var pending = new Stack<LiveNodeViewModel>();
        pending.Push(Root);
        while (pending.TryPop(out var node))
        {
            if (string.Equals(node.Id, nodeId, StringComparison.Ordinal))
            {
                return node;
            }

            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }

        return null;
    }
}

/// <summary>Projects a validated wire tree into a bounded, display-safe bootstrap view model.</summary>
public static class LiveTreeProjector
{
    private const int MaximumDisplayedPropertyCharacters = 4_096;

    public static LiveRenderViewModel Project(RenderPayload render)
    {
        ArgumentNullException.ThrowIfNull(render);
        ProtocolValidator.Validate(
            ProtocolEnvelope.Create("runtime-projection", 0, render));
        return new LiveRenderViewModel(render.Revision, ProjectNode(render.Root));
    }

    public static LiveNodeKind ClassifyKind(string protocolKind) => protocolKind switch
    {
        "root" => LiveNodeKind.Root,
        "text" => LiveNodeKind.Text,
        "button" => LiveNodeKind.Button,
        "textField" => LiveNodeKind.TextField,
        "secureField" => LiveNodeKind.SecureField,
        "vStack" => LiveNodeKind.VStack,
        "hStack" => LiveNodeKind.HStack,
        "zStack" => LiveNodeKind.ZStack,
        "group" => LiveNodeKind.Group,
        "conditional" => LiveNodeKind.Conditional,
        "spacer" => LiveNodeKind.Spacer,
        "divider" => LiveNodeKind.Divider,
        "image" => LiveNodeKind.Image,
        "scrollView" => LiveNodeKind.ScrollView,
        "list" => LiveNodeKind.List,
        "navigationStack" => LiveNodeKind.NavigationStack,
        "toggle" => LiveNodeKind.Toggle,
        "slider" => LiveNodeKind.Slider,
        "progressView" => LiveNodeKind.ProgressView,
        _ => LiveNodeKind.Unsupported
    };

    private static LiveNodeViewModel ProjectNode(ProtocolViewNode node)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in node.Properties.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            properties.Add(
                name,
                LiveUiText.SanitizeControlText(value, MaximumDisplayedPropertyCharacters));
        }

        return new LiveNodeViewModel
        {
            Id = node.Id,
            Kind = ClassifyKind(node.Kind),
            ProtocolKind = LiveUiText.SanitizeControlText(node.Kind, 64),
            Properties = new ReadOnlyDictionary<string, string>(properties),
            Events = Array.AsReadOnly(node.Events.ToArray()),
            Children = Array.AsReadOnly(node.Children.Select(ProjectNode).ToArray())
        };
    }
}

public enum LiveRenderUpdateKind
{
    Applied,
    Rejected,
    IgnoredStale,
    Invalid
}

public sealed record LiveRenderUpdate(
    LiveRenderUpdateKind Kind,
    LiveRenderViewModel? Current,
    string Code,
    string Message);

/// <summary>Deterministic state machine that publishes only trusted, increasing revisions.</summary>
public sealed class LiveRenderState
{
    private readonly object _gate = new();

    public LiveRenderViewModel? Current { get; private set; }

    public LiveRenderUpdate AcceptInitial(RenderPayload render)
    {
        ArgumentNullException.ThrowIfNull(render);
        lock (_gate)
        {
            if (Current is not null)
            {
                return Invalid("ORU2001", "The initial render was already published.");
            }

            if (render.Revision != 1)
            {
                return Invalid("ORU2002", "The first live render must use revision 1.");
            }

            try
            {
                Current = LiveTreeProjector.Project(render);
                return Applied("Initial native render accepted.");
            }
            catch (ProtocolValidationException)
            {
                return Invalid("ORU2003", "The native render tree failed protocol validation.");
            }
        }
    }

    public LiveRenderUpdate ApplyEventResult(
        EventPayload request,
        EventResultPayload result,
        RenderPayload? replacement)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (Current is null)
            {
                return Invalid("ORU2004", "No native render is available for an event result.");
            }

            try
            {
                ProtocolValidator.Validate(ProtocolEnvelope.Create("runtime-event-request", 0, request));
                ProtocolValidator.Validate(ProtocolEnvelope.Create("runtime-event-result", 0, result));
            }
            catch (ProtocolValidationException)
            {
                return Invalid("ORU2011", "The native event exchange failed protocol validation.");
            }

            if (!string.Equals(request.EventId, result.EventId, StringComparison.Ordinal))
            {
                return Invalid("ORU2005", "The native result belongs to a different event.");
            }

            if (request.RenderRevision < Current.Revision || result.RenderRevision < Current.Revision)
            {
                return new LiveRenderUpdate(
                    LiveRenderUpdateKind.IgnoredStale,
                    Current,
                    "ORU2006",
                    "A stale native event result was ignored.");
            }

            if (request.RenderRevision != Current.Revision)
            {
                return Invalid("ORU2007", "The event was not dispatched against the current render.");
            }

            var target = Current.FindNode(request.NodeId);
            if (target is null || !target.SupportsEvent(request.Event))
            {
                return Invalid("ORU2012", "The event target is absent or does not expose that event.");
            }

            if (!result.Accepted)
            {
                if (replacement is not null || result.RenderRevision != Current.Revision)
                {
                    return Invalid("ORU2008", "A rejected event attempted to change the render.");
                }

                return new LiveRenderUpdate(
                    LiveRenderUpdateKind.Rejected,
                    Current,
                    result.ErrorCode ?? "ORU2009",
                    LiveUiText.SanitizeControlText(result.Message ?? "The native application rejected the event.", 512));
            }

            if (replacement is null ||
                replacement.Revision != result.RenderRevision ||
                replacement.Revision <= Current.Revision)
            {
                return Invalid("ORU2010", "An accepted event did not supply one newer matching render.");
            }

            try
            {
                Current = LiveTreeProjector.Project(replacement);
                return Applied("Newer native render accepted.");
            }
            catch (ProtocolValidationException)
            {
                return Invalid("ORU2003", "The native render tree failed protocol validation.");
            }
        }
    }

    private LiveRenderUpdate Applied(string message) =>
        new(LiveRenderUpdateKind.Applied, Current, "ORU0000", message);

    private LiveRenderUpdate Invalid(string code, string message) =>
        new(LiveRenderUpdateKind.Invalid, Current, code, message);
}

public static class LiveEventFactory
{
    public static EventPayload Create(
        LiveRenderViewModel render,
        string eventId,
        string nodeId,
        string eventName,
        string? value = null)
    {
        ArgumentNullException.ThrowIfNull(render);
        var node = render.FindNode(nodeId)
            ?? throw new ArgumentException("The target node is not present in the current render.", nameof(nodeId));
        if (!node.SupportsEvent(eventName))
        {
            throw new ArgumentException("The target node does not expose the requested semantic event.", nameof(eventName));
        }
        if (!node.RendererSupportsEvent(eventName))
        {
            throw new ArgumentException(
                "The target exposes the event, but the bootstrap renderer does not implement it.",
                nameof(eventName));
        }

        var payload = new EventPayload
        {
            EventId = eventId,
            RenderRevision = render.Revision,
            NodeId = node.Id,
            Event = eventName,
            Value = value is null ? null : LiveUiText.SanitizeEventValue(value)
        };
        ProtocolValidator.Validate(ProtocolEnvelope.Create("runtime-event", 0, payload));
        return payload;
    }
}

/// <summary>Safety boundary for text displayed in native Windows controls and logs.</summary>
public static class LiveUiText
{
    public const int MaximumEventValueCharacters = 4_096;

    public static string SanitizeControlText(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);

        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        var truncated = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var unsafeCharacter = category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.Surrogate;
            var representation = unsafeCharacter ? "�" : rune.ToString();
            if (builder.Length + representation.Length > maximumCharacters)
            {
                truncated = true;
                break;
            }

            builder.Append(representation);
        }

        if (truncated)
        {
            var targetLength = maximumCharacters - 1;
            while (builder.Length > targetLength)
            {
                var remove = builder.Length >= 2 &&
                    char.IsLowSurrogate(builder[^1]) &&
                    char.IsHighSurrogate(builder[^2])
                    ? 2
                    : 1;
                builder.Length -= remove;
            }

            builder.Append('…');
        }

        return builder.ToString();
    }

    public static string SanitizeLogText(string value, int maximumCharacters = 4_096) =>
        SanitizeControlText(value, maximumCharacters)
            .Replace('�', ' ')
            .Trim();

    public static string SanitizeEventValue(string value) =>
        SanitizeControlText(value, MaximumEventValueCharacters);
}

/// <summary>A deterministic second bound around child output and UI diagnostics.</summary>
public sealed class LiveLogBuffer
{
    private readonly object _gate = new();
    private readonly int _maximumCharacters;
    private string _text = string.Empty;

    public LiveLogBuffer(int maximumCharacters = 64 * 1024)
    {
        if (maximumCharacters < 256 || maximumCharacters > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        _maximumCharacters = maximumCharacters;
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    public void Append(string category, string message)
    {
        var safeCategory = LiveUiText.SanitizeLogText(category, 64);
        var safeMessage = LiveUiText.SanitizeLogText(message);
        var entry = $"[{safeCategory}] {safeMessage}{Environment.NewLine}";
        lock (_gate)
        {
            _text += entry;
            if (_text.Length > _maximumCharacters)
            {
                var start = _text.Length - _maximumCharacters;
                if (start > 0 &&
                    start < _text.Length &&
                    char.IsLowSurrogate(_text[start]) &&
                    char.IsHighSurrogate(_text[start - 1]))
                {
                    start++;
                }

                _text = _text[start..];
                var firstLineBreak = _text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                if (firstLineBreak >= 0 && firstLineBreak + Environment.NewLine.Length < _text.Length)
                {
                    _text = _text[(firstLineBreak + Environment.NewLine.Length)..];
                }
            }
        }
    }
}
