namespace Orchard.Mirror.Agent.Windows;

/// <summary>
/// Recognises the iPhone system gestures that a mouse has to stand in for.
/// </summary>
/// <remarks>
/// This lives beside <see cref="TouchCoordinates"/> rather than in the window, because it is a
/// decision about normalized touchscreen coordinates and nothing about it depends on Windows.
/// Keeping it here is also what makes it testable: the window is a <c>WinExe</c>.
/// </remarks>
public static class PhoneGestures
{
    /// <summary>Lowest row counted as the home indicator, in normalized touchscreen units.</summary>
    /// <remarks>
    /// The bottom eighth of the screen. The recogniser originally wanted the bottom seventh and a
    /// swipe most of the way up the display, which meant an ordinary flick fell through to a plain
    /// drag and never left the app.
    /// </remarks>
    private const int HomeIndicatorTop = 58000;

    /// <summary>Shortest upward travel that still reads as a deliberate flick.</summary>
    private const int MinimumRise = 5000;

    /// <summary>Widest sideways drift allowed before the gesture is a swipe, not Home.</summary>
    /// <remarks>
    /// iOS puts the app switcher and Home on the same edge and separates them by direction, so a
    /// diagonal must not be claimed here.
    /// </remarks>
    private const int MaximumDrift = 26000;

    /// <summary>Recognise the upward flick off the home indicator that leaves the foreground app.</summary>
    /// <param name="start">Where the gesture first made contact.</param>
    /// <param name="end">Where contact was released.</param>
    /// <returns><see langword="true"/> when the gesture should press Home instead of replaying as a drag.</returns>
    /// <remarks>
    /// Injected touchscreen events reach the foreground app but never iOS's own edge gesture
    /// recogniser: replaying this swipe as a drag was tried at four speeds and lengths against a
    /// device and every one either scrolled the app or registered as a long press. The Indigo HID
    /// Home button is the path that actually leaves the app, so the swipe is mapped onto it.
    /// </remarks>
    public static bool IsHomeSwipe(TouchCoordinates start, TouchCoordinates end) =>
        start.Y >= HomeIndicatorTop
        && start.Y - end.Y >= MinimumRise
        && Math.Abs(start.X - end.X) <= MaximumDrift;
}
