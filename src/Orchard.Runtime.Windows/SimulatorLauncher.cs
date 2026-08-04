using System.Globalization;
using Orchard.Core;
using Orchard.NativeHost.Windows;
using Orchard.Protocol;

namespace Orchard.Runtime.Windows;

public static class SimulatorLauncher
{
    public static void Run(OrchardApplication application, DeviceProfile? device = null)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SimulatorForm(application, device ?? DeviceProfile.Find("iphone-15-pro")));
    }

    public static void RunNative(NativeLaunchRequest request, DeviceProfile? device = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectedDevice = device ?? DeviceProfile.Find("iphone-15-pro");
        var locale = CultureInfo.CurrentCulture.Name;
        if (string.IsNullOrWhiteSpace(locale))
        {
            locale = "en-AU";
        }

        var options = new NativeHostLaunchOptions
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = ["--orchard-live"],
            RuntimeSearchPaths = request.RuntimeSearchPaths,
            Configuration = new ConfigurePayload
            {
                DeviceProfileId = selectedDevice.Id,
                LogicalWidth = selectedDevice.LogicalWidth,
                LogicalHeight = selectedDevice.LogicalHeight,
                DisplayScale = selectedDevice.Scale,
                Appearance = SimulatorAppearance.Light,
                Locale = locale,
                AccessibilityEnabled = false
            }
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new LiveSimulatorForm(options, selectedDevice));
    }
}
