using Orchard.Mirror.Shell;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The window is borderless, always-on-top and has no title bar, so these controls are the only
/// visible way to disconnect, unpin, minimise or close it. Both mistakes are bad in different ways:
/// controls that will not appear leave the owner with no way out but Alt+F4, and controls that will
/// not go away sit on top of the phone they are meant to be showing.
/// </summary>
/// <remarks>
/// Positions are window fractions, following <see cref="PhoneGestureTests"/>, so these describe
/// where a hand actually goes rather than restating the threshold the policy is built from.
/// </remarks>
internal static class ControlStripPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The bug this guards: the fade chose its direction with <c>target &gt; opacity</c>, so once the
    /// strip was fully shown it stepped *down* — 1.0, 0.86, 1.0, 0.86 — twenty times a second, for as
    /// long as the strip was on screen. Reaching for the controls made them strobe.
    /// </summary>
    internal static void FullyShownControlsStopMovingInsteadOfStrobing()
    {
        double opacity = StripFade.Step(1, target: 1);
        Assert(opacity == 1, $"a fully shown strip stepped to {opacity} instead of staying put");

        // A limit cycle needs more than one step to show itself; a single step can look settled
        // while the pair of them oscillates.
        for (int tick = 0; tick < 100; tick++)
        {
            opacity = StripFade.Step(opacity, target: 1);
            Assert(opacity == 1, $"a shown strip drifted to {opacity} on tick {tick}");
        }

        Assert(StripFade.Settled(1, target: 1), "a strip already at its target was not called settled");
    }

    /// <summary>
    /// The shown level must stay below full opacity. WinForms strips WS_EX_LAYERED from a form whose
    /// Opacity is exactly 1, so rounding this up to a tidy 1.0 would put a window-style change at the
    /// end of every fade — and the fade would stop being just an alpha.
    /// </summary>
    internal static void ShownControlsKeepASliverOfTranslucency()
    {
        Assert(StripFade.Shown < 1, $"the shown level is {StripFade.Shown}; at 1 the window stops being layered");
        Assert(StripFade.Shown > 0.9, $"the shown level is {StripFade.Shown}, too faint to read against a bright phone");

        double opacity = 0;
        for (int tick = 0; tick < 100; tick++)
        {
            opacity = StripFade.Step(opacity, StripFade.Shown);
        }

        Assert(opacity == StripFade.Shown, $"fading in settled at {opacity} rather than the shown level");
    }

    internal static void HiddenControlsStopMovingToo()
    {
        double opacity = 0;
        for (int tick = 0; tick < 100; tick++)
        {
            opacity = StripFade.Step(opacity, target: 0);
            Assert(opacity == 0, $"a hidden strip drifted to {opacity} on tick {tick}");
        }
    }

    /// <summary>
    /// Easing that overshoots its target reads as a bounce. Both directions must arrive exactly and
    /// stay, from any starting point rather than only from the ends.
    /// </summary>
    internal static void FadingArrivesExactlyAndNeverOvershoots()
    {
        foreach (double start in new[] { 0.0, 0.07, 0.5, 0.93, 1.0 })
        {
            double rising = start;
            for (int tick = 0; tick < 100; tick++)
            {
                rising = StripFade.Step(rising, target: 1);
                Assert(rising <= 1, $"fading in from {start} overshot to {rising}");
            }

            Assert(rising == 1, $"fading in from {start} stalled at {rising}");

            double falling = start;
            for (int tick = 0; tick < 100; tick++)
            {
                falling = StripFade.Step(falling, target: 0);
                Assert(falling >= 0, $"fading out from {start} undershot to {falling}");
            }

            Assert(falling == 0, $"fading out from {start} stalled at {falling}");
        }
    }

    /// <summary>
    /// Controls should meet a hand that reaches for them and leave unhurriedly, so the two
    /// directions are deliberately not the same speed.
    /// </summary>
    internal static void ControlsArriveQuickerThanTheyLeave()
    {
        int In = StepsBetween(0, 1);
        int Out = StepsBetween(1, 0);
        Assert(In < Out, $"appearing took {In} steps and leaving took {Out}; it should be the other way round");
    }

    private static int StepsBetween(double from, double target)
    {
        double value = from;
        int steps = 0;
        while (!StripFade.Settled(value, target) && steps < 1000)
        {
            value = StripFade.Step(value, target);
            steps++;
        }

        return steps;
    }

    /// <summary>
    /// The reveal band has to stay near the top of the window. Reported as "sometimes the bar pops
    /// up when i dont want": at a fifth of the picture it covered the whole of an iPhone's status
    /// bar and most of a pulled-down notification shade, so reaching into the phone's own top edge
    /// summoned Orchard's controls over it.
    /// </summary>
    internal static void KeepsTheRevealBandOutOfThePhonesOwnReach()
    {
        Assert(
            ControlStripPolicy.RevealBand <= 0.12,
            $"the reveal band is {ControlStripPolicy.RevealBand:P0} of the window, far enough down to "
                + "cover what the owner is reaching for on the phone");

        // Still has to be reachable: the strip sits about a fourteenth of the way down, and a band
        // tighter than the strip itself would mean travelling to a control that vanishes en route.
        Assert(
            ControlStripPolicy.RevealBand >= 0.07,
            $"the reveal band is {ControlStripPolicy.RevealBand:P0}, tighter than the strip it reveals");

        ControlStripPolicy policy = new();
        Assert(
            !policy.Update(At(0.15, Start)),
            "a pointer a sixth of the way down the phone is using the phone, not reaching for Orchard");
        Assert(
            policy.Update(At(0.05, Start)),
            "a pointer at the very top must still bring the controls up");
    }

    internal static void ShowsWhenThePointerReachesForTheTop()
    {
        ControlStripPolicy policy = new();
        Assert(
            policy.Update(At(0.03, Start)),
            "reaching for the top of the window must bring up its controls");
    }

    internal static void StaysAwayWhileThePhoneIsBeingUsed()
    {
        ControlStripPolicy policy = new();
        _ = policy.Update(At(0.03, Start));

        // Pointer moves down into the phone's own content and stays there.
        Assert(
            !policy.Update(At(0.60, Start + TimeSpan.FromSeconds(1.5))),
            "the controls must get out of the way while the phone is being used");
    }

    /// <summary>Controls that vanish while you are travelling towards them cannot be clicked.</summary>
    internal static void LingersLongEnoughToBeReached()
    {
        ControlStripPolicy policy = new();
        _ = policy.Update(At(0.03, Start));

        Assert(
            policy.Update(At(0.40, Start + TimeSpan.FromMilliseconds(200))),
            "the controls disappeared before the pointer could travel to them");
    }

    /// <summary>Someone using a control has no pointer left in the band to hold it open with.</summary>
    internal static void NeverHidesWhileAControlIsBeingUsed()
    {
        ControlStripPolicy policy = new();
        _ = policy.Update(At(0.03, Start));

        Assert(
            policy.Update(new StripInput(PointerInWindow: false, 0, HoldOpen: true, Start + TimeSpan.FromMinutes(5))),
            "the controls faded out from under someone using them");
    }

    internal static void HidesWhenThePointerLeavesTheWindow()
    {
        ControlStripPolicy policy = new();
        _ = policy.Update(At(0.03, Start));

        Assert(
            !policy.Update(new StripInput(PointerInWindow: false, 0, HoldOpen: false, Start + TimeSpan.FromSeconds(3))),
            "the controls stayed up after the pointer left the window entirely");
    }

    /// <summary>
    /// The band is a fraction, so it means the same gesture in a window of any size. Stated as two
    /// window heights rather than as the constant, which is what makes it a claim about behaviour.
    /// </summary>
    internal static void MeansTheSameGestureAtAnyWindowSize()
    {
        foreach (int height in (int[])[600, 1400])
        {
            ControlStripPolicy policy = new();
            const int NearTopPixels = 20;
            double fraction = NearTopPixels / (double)height;

            Assert(
                policy.Update(At(fraction, Start)) == (fraction <= ControlStripPolicy.RevealBand),
                $"a hand {NearTopPixels}px from the top behaved differently in a {height}px window");
        }
    }

    private static StripInput At(double yFraction, DateTimeOffset now) =>
        new(PointerInWindow: true, yFraction, HoldOpen: false, now);

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
