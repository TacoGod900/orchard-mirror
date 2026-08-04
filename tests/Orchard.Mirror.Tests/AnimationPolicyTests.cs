using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// Whether the setup surface has any reason to repaint. The failure this guards against is not a
/// wrong picture but a stationary one being redrawn sixty times a second forever, which is what a
/// page reporting a failure it had already given up on used to do.
/// </summary>
internal static class AnimationPolicyTests
{
    /// <summary>The state that used to burn a core: a stopped page, waiting for a click.</summary>
    internal static void StopsWhenNothingIsMoving()
    {
        Assert(
            !AnimationPolicy.ShouldAnimate(default),
            "a page where nothing is moving still asked to be repainted");
    }

    /// <summary>Anything genuinely in motion has to keep the surface awake.</summary>
    internal static void RunsWhileAnythingIsMoving()
    {
        (string Name, AnimationNeed Need)[] moving =
        [
            ("sliding between pages", new AnimationNeed(Sliding: true, false, false, false, false)),
            ("revealing the mirror", new AnimationNeed(false, Revealing: true, false, false, false)),
            ("a turning spinner", new AnimationNeed(false, false, Spinning: true, false, false)),
            ("a settling hover", new AnimationNeed(false, false, false, HoverSettling: true, false)),
            ("holding before handing over", new AnimationNeed(false, false, false, false, Holding: true)),
        ];

        foreach ((string name, AnimationNeed need) in moving)
        {
            Assert(AnimationPolicy.ShouldAnimate(need), $"{name} did not keep the surface repainting");
        }
    }

    /// <summary>
    /// Easing has to actually arrive. Exponential easing never mathematically reaches its target,
    /// so without a tolerance the surface would be forever almost-settled and never go idle.
    /// </summary>
    internal static void HoverEasingArrivesAndStops()
    {
        double value = 0;
        int steps = 0;
        while (!HoverEasing.Settled(value, 1.0))
        {
            double next = HoverEasing.Step(value, 1.0);
            Assert(next > value, "easing towards a higher target must move towards it");
            value = next;
            steps++;
            Assert(steps < 500, "easing never converged on its target");
        }

        Assert(value == 1.0, "a settled highlight must sit exactly on its target, not near it");
        Assert(steps < 60, $"a hover took {steps} frames to settle, which is visibly slow");
    }

    /// <summary>Easing down converges too, and never overshoots past the target.</summary>
    internal static void HoverEasingConvergesDownwardWithoutOvershooting()
    {
        double value = 1.0;
        for (int step = 0; step < 200 && !HoverEasing.Settled(value, 0); step++)
        {
            value = HoverEasing.Step(value, 0);
            Assert(value >= 0, "easing overshot below its target");
        }

        Assert(HoverEasing.Settled(value, 0), "easing down never arrived");
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
