using Orchard.Core;

namespace Orchard.Runtime.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            switch (RuntimeCommandLine.Parse(args))
            {
                case WelcomeLaunchRequest:
                    SimulatorLauncher.Run(CreateWelcomeApplication());
                    break;
                case StaticIrLaunchRequest staticIr:
                    SimulatorLauncher.Run(OrchardApplicationReader.ReadFile(staticIr.IrPath));
                    break;
                case NativeLaunchRequest native:
                    SimulatorLauncher.RunNative(native);
                    break;
                default:
                    throw new InvalidOperationException("The runtime launch mode is unsupported.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                LiveUiText.SanitizeControlText(exception.Message, 2_048),
                "Orchard Simulator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static OrchardApplication CreateWelcomeApplication()
    {
        var root = new ViewNode
        {
            Id = "view-0001",
            Type = "NavigationStack",
            Children =
            [
                new ViewNode
                {
                    Id = "view-0002",
                    Type = "VStack",
                    Modifiers =
                    [
                        new ViewModifier("padding", new Dictionary<string, string> { ["_0"] = "24" }),
                        new ViewModifier("navigationTitle", new Dictionary<string, string> { ["_0"] = "Hello Orchard" })
                    ],
                    Children =
                    [
                        new ViewNode
                        {
                            Id = "view-0003",
                            Type = "Text",
                            Arguments = new Dictionary<string, string> { ["_0"] = "Welcome to Orchard" },
                            Modifiers = [new ViewModifier("font", new Dictionary<string, string> { ["_0"] = ".largeTitle" })]
                        },
                        new ViewNode
                        {
                            Id = "view-0004",
                            Type = "Text",
                            Arguments = new Dictionary<string, string>
                            {
                                ["_0"] = "A clean-room Windows compatibility runtime bootstrap"
                            },
                            Modifiers = [new ViewModifier("foregroundStyle", new Dictionary<string, string> { ["_0"] = ".secondary" })]
                        },
                        new ViewNode
                        {
                            Id = "view-0005",
                            Type = "TextField",
                            Arguments = new Dictionary<string, string> { ["_0"] = "Your name", ["text"] = "$name" }
                        },
                        new ViewNode
                        {
                            Id = "view-0006",
                            Type = "Text",
                            Arguments = new Dictionary<string, string> { ["_0"] = "Hello, \\(name)" },
                            Modifiers =
                            [
                                new ViewModifier(
                                    "accessibilityLabel",
                                    new Dictionary<string, string> { ["_0"] = "Live name preview" })
                            ]
                        },
                        new ViewNode
                        {
                            Id = "view-0007",
                            Type = "Button",
                            Arguments = new Dictionary<string, string> { ["_0"] = "Continue" },
                            Events = [new ViewEvent("action", "print(\"Continue pressed\") name = \"Orchard Developer\"")]
                        }
                    ]
                }
            ]
        };

        return new OrchardApplication
        {
            SchemaVersion = OrchardSchema.CurrentVersion,
            ApplicationId = "dev.orchard.welcome",
            DisplayName = "Hello Orchard",
            SourceFile = "embedded-welcome",
            RootView = root,
            State = [new StateDefinition("name", StateValueKind.Text, string.Empty)],
            Compatibility = new CompatibilityReport
            {
                LocalCompatibilityPercent = 95,
                ApiUsages = [],
                RequiredRemoteCapabilities = [],
                UnsupportedSymbols = []
            },
            CompiledAtUtc = DateTimeOffset.UnixEpoch
        };
    }
}
