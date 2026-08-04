namespace Orchard.Mirror.Shell;

/// <summary>Everything that could currently be moving on the setup surface.</summary>
/// <param name="Sliding">Content is sliding in from a page change.</param>
/// <param name="Revealing">The surface is fading away to uncover the mirror.</param>
/// <param name="Spinning">A ring is turning because Orchard is genuinely working.</param>
/// <param name="HoverSettling">A hover highlight has not finished easing to its target.</param>
/// <param name="Holding">A presentation is counting down to hand the screen back.</param>
public readonly record struct AnimationNeed(
    bool Sliding,
    bool Revealing,
    bool Spinning,
    bool HoverSettling,
    bool Holding);

/// <summary>
/// Whether there is any reason to repaint.
/// </summary>
/// <remarks>
/// <para>The surface ran a sixteen-millisecond timer whose every tick invalidated the whole
/// control, and it was stopped in exactly one place — after a reveal completed. So the opening
/// page repainted sixty times a second until someone clicked it, and a page reporting a failure
/// that had already given up did the same, indefinitely, redrawing an unchanging picture.</para>
/// <para>Asking one function makes "nothing is moving" a state the surface can be in, rather than
/// something that has to be noticed at each of the places motion happens to stop.</para>
/// </remarks>
public static class AnimationPolicy
{
    /// <summary>Whether the surface has any reason to keep repainting.</summary>
    public static bool ShouldAnimate(AnimationNeed need) =>
        need.Sliding || need.Revealing || need.Spinning || need.HoverSettling || need.Holding;
}

/// <summary>
/// Eases a hover highlight towards its target and says when it has arrived.
/// </summary>
/// <remarks>
/// Exponential easing never mathematically reaches its target, so something has to decide when it
/// is close enough to stop. Without that the surface can never go idle: the highlight would be
/// forever almost-settled and the timer would run forever with nothing visibly changing.
/// </remarks>
public static class HoverEasing
{
    /// <summary>How close counts as arrived. Well below one step of visible alpha.</summary>
    public const double Tolerance = 0.004;

    /// <summary>How much of the remaining distance is covered each step.</summary>
    private const double Rate = 0.2;

    /// <summary>Move one step towards the target.</summary>
    public static double Step(double current, double target)
    {
        double next = current + ((target - current) * Rate);
        return Settled(next, target) ? target : next;
    }

    /// <summary>Whether easing has arrived and can stop.</summary>
    public static bool Settled(double current, double target) => Math.Abs(target - current) <= Tolerance;
}
