using Orchard.Mirror.Agent.Windows;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The Home-swipe recogniser decides whether a mouse gesture is replayed as a drag or turned into
/// a hardware Home press. Both mistakes are visible: claiming too much makes ordinary scrolling
/// exit the app, and claiming too little leaves the user unable to exit at all.
/// </summary>
/// <remarks>
/// The gestures below are written as window fractions and converted with
/// <see cref="TouchCoordinates.FromClient"/>, so they describe what a hand actually does on a
/// phone-shaped window rather than restating the thresholds the recogniser is built from.
/// </remarks>
internal static class PhoneGestureTests
{
    /// <summary>A representative phone-shaped client area, in pixels.</summary>
    private const int Width = 415;
    private const int Height = 908;

    internal static void RecognisesAFlickOffTheHomeIndicator()
    {
        // Off the home indicator, straight up, about a fifth of the screen: the gesture a user
        // makes to leave an app.
        Assert(
            IsHome(fromX: 0.50, fromY: 0.97, toX: 0.50, toY: 0.78),
            "an upward flick off the home indicator must press Home");
    }

    internal static void IgnoresAFlickThatStartsAwayFromTheEdge()
    {
        // The same movement in the middle of the screen is content scrolling, not a system gesture.
        Assert(
            !IsHome(fromX: 0.50, fromY: 0.60, toX: 0.50, toY: 0.41),
            "an upward flick in mid-screen must stay an ordinary drag");
    }

    internal static void IgnoresATwitchOnTheHomeIndicator()
    {
        // A press that barely moves is a tap on whatever sits above the indicator.
        Assert(
            !IsHome(fromX: 0.50, fromY: 0.97, toX: 0.50, toY: 0.955),
            "a barely-moving press on the indicator must not press Home");
    }

    internal static void IgnoresASidewaysSwipeAlongTheEdge()
    {
        // Swiping across the bottom edge is the app switcher, and iOS separates it from Home by
        // direction. This one rises far enough to clear the flick threshold, so only the sideways
        // limit can reject it -- which is the point: claiming it would make switching apps
        // impossible.
        Assert(
            !IsHome(fromX: 0.10, fromY: 0.97, toX: 0.90, toY: 0.70),
            "a swipe across the bottom edge must not press Home");
    }

    internal static void IgnoresADownwardSwipeFromTheEdge()
    {
        Assert(
            !IsHome(fromX: 0.50, fromY: 0.90, toX: 0.50, toY: 0.99),
            "a downward drag must never press Home");
    }

    internal static void RecognisesAFlickThatDriftsSlightly()
    {
        // Hands are not straight. A flick that wanders a little must still leave the app.
        Assert(
            IsHome(fromX: 0.50, fromY: 0.97, toX: 0.57, toY: 0.72),
            "a slightly diagonal flick off the indicator must still press Home");
    }

    private static bool IsHome(double fromX, double fromY, double toX, double toY) =>
        PhoneGestures.IsHomeSwipe(Point(fromX, fromY), Point(toX, toY));

    private static TouchCoordinates Point(double x, double y) => TouchCoordinates.FromClient(
        (int)Math.Round(x * (Width - 1)),
        (int)Math.Round(y * (Height - 1)),
        Width,
        Height);

    private static void Assert(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException(because);
        }
    }
}
