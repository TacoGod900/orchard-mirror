using System.Text.Json;
using Orchard.Mirror.Agent.Windows;
using Orchard.Mirror.Video.Windows;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The mirrored picture is padded past the screen, so the reported display size is what tells the
/// presenter which pixels are real. These fixtures are transcribed from a live iPhone 16e running
/// iOS 27, not from the parser, so a shape the device never sends cannot make them pass.
/// </summary>
internal static class CoreDeviceDisplaySizeTests
{
    /// <summary>The connect payload as CoreDevice actually returns it, trimmed for length.</summary>
    private const string LiveConnectPayload = """
    {
      "udid": "00008140-001569A234EB801C",
      "transport": "wifi",
      "device": { "deviceName": "iPhone", "productVersion": "27.0" },
      "display": {
        "current": true,
        "orientation": { "currentDeviceOrientation": "portrait" },
        "displays": [
          {
            "external": false,
            "primary": true,
            "deviceName": "primary",
            "nativeSize": [1170.0, 2532.0],
            "currentMode": {
              "preferredUIScale": 3,
              "refreshRate": 60.0,
              "size": [1170.0, 2532.0]
            },
            "physicalSize": [2.54347825050354, 5.504347801208496],
            "displayId": 1,
            "bounds": [[0.0, 0.0], [1170.0, 2532.0]],
            "frame": [[0.0, 0.0], [1170.0, 2532.0]],
            "availableModes": [
              { "refreshRate": 60.0, "size": [1170.0, 2532.0] },
              { "refreshRate": 60.0, "size": [586.0, 1266.0] }
            ]
          }
        ]
      }
    }
    """;

    internal static void ReadsTheLiveIosDisplayResolution()
    {
        using JsonDocument document = JsonDocument.Parse(LiveConnectPayload);

        Assert(
            CoreDeviceDisplaySize.TryFind(document.RootElement, out int width, out int height),
            "The live iOS 27 connect payload yielded no display resolution.");
        Assert(width == 1170, $"Display width was {width}, expected the panel's 1170.");
        Assert(height == 2532, $"Display height was {height}, expected the panel's 2532.");
    }

    /// <summary>A phone's own panel must outrank an attached external screen.</summary>
    internal static void PrefersThePrimaryDisplayOverAnExternalOne()
    {
        using JsonDocument document = JsonDocument.Parse("""
        {
          "displays": [
            { "primary": false, "external": true, "currentMode": { "size": [3840.0, 2160.0] } },
            { "primary": true, "external": false, "currentMode": { "size": [1170.0, 2532.0] } }
          ]
        }
        """);

        Assert(
            CoreDeviceDisplaySize.TryFind(document.RootElement, out int width, out int height),
            "A report containing an external display yielded no resolution.");
        Assert(
            width == 1170 && height == 2532,
            $"An external display won the selection: got {width}x{height}.");
    }

    /// <summary>A future build could switch to the object form; both must keep working.</summary>
    internal static void ReadsTheObjectSizeForm()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"displays":[{"primary":true,"currentMode":{"size":{"width":1284,"height":2778}}}]}""");

        Assert(
            CoreDeviceDisplaySize.TryFind(document.RootElement, out int width, out int height),
            "The object size form yielded no resolution.");
        Assert(width == 1284 && height == 2778, $"Object size parsed as {width}x{height}.");
    }

    /// <summary>
    /// Without a usable size the presenter must show the whole decoded frame. Reporting a wrong
    /// guess would crop live screen pixels away, which is worse than the black padding.
    /// </summary>
    internal static void ReportsNoResolutionRatherThanGuessing()
    {
        foreach (string payload in new[]
        {
            """{"display":{"displays":[]}}""",
            """{"display":{"displays":[{"primary":true,"physicalSize":[2.54,5.50]}]}}""",
            """{"display":{"displays":[{"primary":true,"currentMode":{"size":[1170.0]}}]}}""",
            """{"display":{"displays":[{"primary":true,"currentMode":{"size":["wide","tall"]}}]}}""",
            """{"icon":{"size":{"width":64,"height":64}}}""",
            "null",
        })
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            Assert(
                !CoreDeviceDisplaySize.TryFind(document.RootElement, out int width, out int height),
                $"An unusable report produced {width}x{height}: {payload}");
            Assert(width == 0 && height == 0, $"A failed lookup left {width}x{height} behind.");
        }
    }

    /// <summary>
    /// Cropping is only ever meant to remove block-alignment padding. Anything larger means the
    /// display report does not describe this stream, and honouring it would zoom the picture.
    /// </summary>
    internal static void OnlyCropsAwayEncoderPadding()
    {
        Assert(
            DxgiFlipPresenter.IsPlausibleCrop(2532, 2576),
            "The real iPhone 16e padding of 44 rows was rejected.");
        Assert(
            DxgiFlipPresenter.IsPlausibleCrop(1170, 1184),
            "The real iPhone 16e padding of 14 columns was rejected.");
        Assert(
            DxgiFlipPresenter.IsPlausibleCrop(1184, 1184),
            "An exact match should be honoured as a no-op crop.");
        Assert(
            !DxgiFlipPresenter.IsPlausibleCrop(1170, 2576),
            "A crop trimming far more than padding was accepted and would zoom the picture.");
        Assert(
            !DxgiFlipPresenter.IsPlausibleCrop(2576, 1184),
            "A crop larger than the decoded frame was accepted.");
        Assert(!DxgiFlipPresenter.IsPlausibleCrop(0, 1184), "A zero crop was accepted.");
        Assert(!DxgiFlipPresenter.IsPlausibleCrop(-8, 1184), "A negative crop was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
