using System.Runtime.InteropServices;

namespace Orchard.Mirror.Windows;

/// <summary>
/// The Win32 surface Orchard Mirror actually uses: window hit-testing and sizing, the drag handoff,
/// and the DWM attributes that give the window its borderless rounded frame.
/// </summary>
/// <remarks>
/// This used to carry a second set of imports — <c>EnumWindows</c>, <c>GetWindowThreadProcessId</c>,
/// <c>SetParent</c>, <c>GetWindowLongPtr</c>/<c>SetWindowLongPtr</c>, <c>MoveWindow</c> and the
/// <c>WS_*</c> style bits — which together are one recipe: find another process's window by
/// enumerating them all, strip its caption and frame, and reparent it into a panel. That was how the
/// prototype hosted a UxPlay receiver, before the phone's own HEVC stream was decoded in-process.
/// None of it had been called in a long time, and none of it describes anything this app does now.
/// </remarks>
internal static partial class NativeMethods
{
    internal const int WmNcHitTest = 0x0084;
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int WmSizing = 0x0214;
    internal const int WmszLeft = 1;
    internal const int WmszRight = 2;
    internal const int WmszTop = 3;
    internal const int WmszTopLeft = 4;
    internal const int WmszTopRight = 5;
    internal const int WmszBottom = 6;
    internal const int WmszBottomLeft = 7;
    internal const int WmszBottomRight = 8;
    internal const int GwlExStyle = -20;
    internal const int WsExTopMost = 0x00000008;
    internal const nint HwndTopMost = -1;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const int HtCaption = 0x0002;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;

    internal const int DwmWindowCornerPreference = 33;
    internal const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    internal const int DwmWindowCornerPreferenceDoNotRound = 1;

    /// <summary>DWMWA_BORDER_COLOR. Windows 11 paints a light frame line here by default.</summary>
    internal const int DwmWindowAttributeBorderColor = 34;

    /// <summary>DWMWA_COLOR_NONE: suppress the frame line entirely.</summary>
    internal const uint DwmColorNone = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLongW(nint window, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    /// <summary>Whether the window is currently in the always-on-top band, read from Windows.</summary>
    internal static bool IsTopMost(nint window) => (GetWindowLongW(window, GwlExStyle) & WsExTopMost) != 0;

    /// <summary>Put the window back in the always-on-top band without moving, sizing or focusing it.</summary>
    internal static void ReassertTopMost(nint window) =>
        _ = SetWindowPos(window, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    [LibraryImport("user32.dll")]
    internal static partial nint SendMessageW(
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
