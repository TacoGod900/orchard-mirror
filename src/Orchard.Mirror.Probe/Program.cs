using System.Globalization;

namespace Orchard.Mirror.Probe;

/// <summary>
/// Orchard Mirror's local diagnostic probe. It observes what this PC can see of nearby Apple
/// devices and nothing more: it opens no session, sends no authentication bytes, and cannot
/// mirror or control a phone.
///
/// <para>Mirror's actual device work runs over CoreDevice through the Python agent (ADR 0014).
/// What remains here is the "why can't Windows see my phone?" toolkit, which is worth keeping
/// for the Wi-Fi transport gate, where discovery is the thing that tends to fail.</para>
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0] switch
            {
                "self-test" => SelfTest.Run(),
                "scan" => await ScanAsync(ParseSeconds(args, 20)),
                "discover" => await DiscoverAsync(ParseSeconds(args, 12)),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"probe error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ScanAsync(int seconds)
    {
        IReadOnlyList<BlePeerObservation> peers =
            await AppleBleScanner.ScanAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        PrintBleSummary(peers);
        return 0;
    }

    private static async Task<int> DiscoverAsync(int seconds)
    {
        IReadOnlyList<MdnsService> services =
            await MdnsDiscovery.DiscoverAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None);
        PrintDiscoverySummary(services);
        return 0;
    }

    private static void PrintBleSummary(IReadOnlyList<BlePeerObservation> peers)
    {
        Console.WriteLine();
        Console.WriteLine($"Apple BLE peers observed: {peers.Count}");
        foreach (BlePeerObservation peer in peers.OrderByDescending(static peer => peer.MaxRssi))
        {
            string types = peer.ContinuityTypes.Count == 0
                ? "none decoded"
                : string.Join(", ", peer.ContinuityTypes.Select(
                    static type => $"0x{type:X2}{AppleBleScanner.DescribeType(type)}"));
            Console.WriteLine(
                $"  {peer.PeerId}: max RSSI {peer.MaxRssi} dBm, packets {peer.PacketCount}, TLVs {types}");
        }
    }

    private static void PrintDiscoverySummary(IReadOnlyList<MdnsService> services)
    {
        Console.WriteLine();
        Console.WriteLine($"Companion-link services discovered: {services.Count}");
        foreach (MdnsService service in services)
        {
            string addresses = service.Addresses.Count == 0
                ? "unresolved"
                : string.Join(", ", service.Addresses);
            Console.WriteLine(
                $"  {service.PeerId}: port {service.Port}, addresses {addresses}, TXT keys [{string.Join(", ", service.TxtKeys)}]");
        }
    }

    private static int ParseSeconds(string[] args, int defaultValue)
    {
        string? value = ReadOption(args, "--seconds");
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
            seconds is < 1 or > 3600)
        {
            throw new ArgumentException("--seconds must be between 1 and 3600.");
        }

        return seconds;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int index = 1; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Orchard Mirror local diagnostic probe

            Commands:
              self-test
                  Verify the probe's own framing and decoding offline.
              scan [--seconds N]
                  Observe Apple BLE manufacturer frames without connecting.
              discover [--seconds N]
                  Query _companion-link._tcp.local over ordinary local interfaces.

            Safety:
              Device addresses and advertisement payloads are not printed.
              Nothing here opens a session, authenticates, mirrors, or controls a phone.
              Mirror's device work runs over CoreDevice through the Python agent; see
              docs/decisions/0014-orchard-mirror-coredevice-architecture.md.
            """);
    }
}
