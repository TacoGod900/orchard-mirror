using Orchard.Mirror.Agent.Windows;

namespace Orchard.Mirror.Windows;

/// <summary>Coalesces high-rate Windows pointer motion into ordered CoreDevice touchscreen samples.</summary>
internal sealed class CoreDeviceInputController : IAsyncDisposable
{
    private readonly CoreDeviceAgentClient agent;
    private readonly Action<string> reportError;
    private readonly object sync = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private TouchCoordinates? pendingMove;
    private TouchCoordinates touchStart;
    private TouchCoordinates scrollOrigin;
    private int pendingScrollDelta;
    private bool movePumpRunning;
    private bool scrollPumpRunning;
    private bool contact;
    private TouchCoordinates lastPoint;
    private bool disposed;

    internal CoreDeviceInputController(CoreDeviceAgentClient agent, Action<string> reportError)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
    }

    internal async Task TouchDownAsync(Point location, Size clientSize)
    {
        if (disposed)
        {
            return;
        }

        TouchCoordinates point = Map(location, clientSize);
        lock (sync)
        {
            contact = true;
            touchStart = point;
            lastPoint = point;
            pendingMove = null;
        }

        await SendAsync(async () =>
            _ = await agent.SendTouchAsync(true, point.X, point.Y).ConfigureAwait(false)).ConfigureAwait(false);
    }

    internal void TouchMove(Point location, Size clientSize)
    {
        if (disposed)
        {
            return;
        }

        bool startPump = false;
        lock (sync)
        {
            if (!contact)
            {
                return;
            }

            pendingMove = Map(location, clientSize);
            if (!movePumpRunning)
            {
                movePumpRunning = true;
                startPump = true;
            }
        }

        if (startPump)
        {
            _ = PumpMovesAsync();
        }
    }

    internal async Task TouchUpAsync(Point location, Size clientSize)
    {
        if (disposed)
        {
            return;
        }

        TouchCoordinates point = Map(location, clientSize);
        TouchCoordinates start;
        lock (sync)
        {
            start = touchStart;
            contact = false;
            lastPoint = point;
            pendingMove = null;
        }

        await SendAsync(async () =>
        {
            if (PhoneGestures.IsHomeSwipe(start, point))
            {
                _ = await agent.SendTouchAsync(false, point.X, point.Y).ConfigureAwait(false);
                _ = await agent.PressButtonAsync("home").ConfigureAwait(false);
                return;
            }

            // Always send the release point as a contact first. The move pump is
            // intentionally lossy, but the end of a gesture must never be dropped.
            _ = await agent.SendTouchAsync(true, point.X, point.Y).ConfigureAwait(false);
            _ = await agent.SendTouchAsync(false, point.X, point.Y).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    internal Task ScrollAsync(Point location, Size clientSize, int delta)
    {
        if (disposed || delta == 0)
        {
            return Task.CompletedTask;
        }

        lock (sync)
        {
            scrollOrigin = Map(location, clientSize);
            // Two notches of backlog at most. A deeper queue kept replaying drags after the wheel
            // had already stopped, which looked like the phone carrying on by itself.
            pendingScrollDelta = Math.Clamp(pendingScrollDelta + delta, -240, 240);
            if (scrollPumpRunning)
            {
                return Task.CompletedTask;
            }

            scrollPumpRunning = true;
        }

        return PumpScrollAsync();
    }

    internal Task TypeAsync(string text) => SendAsync(async () =>
        _ = await agent.TypeTextAsync(text).ConfigureAwait(false));

    internal Task KeyAsync(string name) => SendAsync(async () =>
        _ = await agent.SendKeyAsync(name).ConfigureAwait(false));

    internal Task ButtonAsync(string name) => SendAsync(async () =>
        _ = await agent.PressButtonAsync(name).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        TouchCoordinates point;
        bool release;
        lock (sync)
        {
            release = contact;
            contact = false;
            point = lastPoint;
            pendingMove = null;
            disposed = true;
        }

        if (release)
        {
            // A dead CoreDevice connection must never hold reconnect teardown
            // behind an in-flight HID command. Release is strictly best effort.
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));
            try
            {
                _ = await agent.SendTouchAsync(
                    false,
                    point.X,
                    point.Y,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or AgentCommandException
                    or AgentProtocolException
                    or IOException)
            {
            }
        }

        // Disposed last, after the release above has finished using it. A new controller is built
        // on every reconnect, so leaking one of these per attempt is a real leak on a phone that
        // keeps dropping out.
        sendGate.Dispose();
    }

    private async Task PumpMovesAsync()
    {
        while (true)
        {
            TouchCoordinates point;
            lock (sync)
            {
                if (!contact || pendingMove is null)
                {
                    movePumpRunning = false;
                    return;
                }

                point = pendingMove.Value;
                pendingMove = null;
                lastPoint = point;
            }

            await SendAsync(async () =>
            {
                lock (sync)
                {
                    if (!contact)
                    {
                        return;
                    }
                }

                _ = await agent.SendTouchAsync(true, point.X, point.Y).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    private async Task PumpScrollAsync()
    {
        while (true)
        {
            TouchCoordinates origin;
            int delta;
            lock (sync)
            {
                delta = pendingScrollDelta;
                pendingScrollDelta = 0;
                origin = scrollOrigin;
                if (delta == 0 || disposed)
                {
                    scrollPumpRunning = false;
                    return;
                }
            }

            int notches = Math.Max(1, Math.Abs(delta) / 120);
            int travel = Math.Clamp(notches * 3600, 3000, 12000);
            int destinationY = Math.Clamp(origin.Y + (delta > 0 ? travel : -travel), 0, 65535);

            // Each notch finishes stationary. Releasing straight out of the movement carried its
            // velocity into iOS, which flung the list, so content drifted on after the wheel had
            // stopped instead of behaving like a desktop scroll.
            const double settleSeconds = 0.07;
            await SendAsync(async () =>
                _ = await agent.SendDragAsync(
                    origin.X,
                    origin.Y,
                    origin.X,
                    destinationY,
                    steps: 5,
                    duration: 0.055,
                    settle: settleSeconds).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(Func<Task> action)
    {
        await sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AgentCommandException or AgentProtocolException or IOException)
        {
            reportError(exception.Message);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static TouchCoordinates Map(Point location, Size size) =>
        TouchCoordinates.FromClient(location.X, location.Y, Math.Max(1, size.Width), Math.Max(1, size.Height));
}
