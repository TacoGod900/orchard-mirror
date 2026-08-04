using System.Text;

namespace Orchard.Mirror.Probe;

/// <summary>
/// Offline checks for the probe's own wire encoding. Nothing here touches a network, a radio, or a
/// device.
///
/// <para>The expectations below are derived from RFC 1035 §4.1 and the service name itself, not
/// from reading <see cref="MdnsDiscovery.DnsWire"/>. A test written from the implementation it
/// checks would pass just as happily against a broken encoder.</para>
/// </summary>
internal static class SelfTest
{
    private const string CompanionLink = "_companion-link._tcp.local";

    public static int Run()
    {
        CheckPtrQueryMatchesRfc1035();
        CheckUnicastResponseBitIsTheOnlyDifference();

        Console.WriteLine("self-test passed:");
        Console.WriteLine("  RFC 1035 PTR query header, QNAME, QTYPE and QCLASS");
        Console.WriteLine("  mDNS unicast-response bit encoding");
        return 0;
    }

    /// <summary>
    /// A PTR query for <c>_companion-link._tcp.local</c> is fully determined by RFC 1035, so the
    /// whole datagram can be predicted byte for byte:
    /// a 12-byte header carrying one question and no records, then QNAME as length-prefixed labels
    /// terminated by a zero byte, then QTYPE 12 (PTR) and QCLASS 1 (IN) with mDNS's unicast-response
    /// bit set in the top bit.
    /// </summary>
    private static void CheckPtrQueryMatchesRfc1035()
    {
        byte[] query = MdnsDiscovery.DnsWire.CreatePtrQuery(CompanionLink, requestUnicastResponse: true);

        List<byte> expected =
        [
            0x00, 0x00,             // ID: 0
            0x00, 0x00,             // flags: standard query, no recursion
            0x00, 0x01,             // QDCOUNT: one question
            0x00, 0x00,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00,             // ARCOUNT
        ];

        foreach (string label in CompanionLink.Split('.'))
        {
            expected.Add(checked((byte)label.Length));
            expected.AddRange(Encoding.ASCII.GetBytes(label));
        }

        expected.Add(0x00);         // root label terminates QNAME
        expected.AddRange([0x00, 0x0C]);   // QTYPE 12 = PTR
        expected.AddRange([0x80, 0x01]);   // QCLASS 1 = IN, top bit = unicast response requested

        // 12 header + 28 QNAME (1+15 + 1+4 + 1+5 + 1) + 2 QTYPE + 2 QCLASS.
        Require(expected.Count == 44, $"The expectation itself is malformed: {expected.Count} bytes, not 44.");
        Require(
            query.Length == expected.Count,
            $"PTR query was {query.Length} bytes; RFC 1035 gives {expected.Count} for '{CompanionLink}'.");
        Require(
            query.AsSpan().SequenceEqual(CollectionsMarshalAsSpan(expected)),
            $"PTR query bytes differ from RFC 1035.{Environment.NewLine}"
                + $"  expected {Convert.ToHexString(expected.ToArray())}{Environment.NewLine}"
                + $"  actual   {Convert.ToHexString(query)}");
    }

    /// <summary>
    /// mDNS (RFC 6762 §5.4) reuses the top bit of QCLASS to ask for a unicast reply. Setting it must
    /// change that bit and nothing else — a query that differed elsewhere would be a different
    /// question.
    /// </summary>
    private static void CheckUnicastResponseBitIsTheOnlyDifference()
    {
        byte[] unicast = MdnsDiscovery.DnsWire.CreatePtrQuery(CompanionLink, requestUnicastResponse: true);
        byte[] multicast = MdnsDiscovery.DnsWire.CreatePtrQuery(CompanionLink, requestUnicastResponse: false);

        Require(
            unicast.Length == multicast.Length,
            "The unicast-response bit changed the query length.");
        Require(
            multicast[^2] == 0x00 && multicast[^1] == 0x01,
            $"Multicast QCLASS should be IN (0x0001) but was 0x{multicast[^2]:X2}{multicast[^1]:X2}.");
        Require(
            unicast[^2] == 0x80 && unicast[^1] == 0x01,
            $"Unicast QCLASS should be 0x8001 but was 0x{unicast[^2]:X2}{unicast[^1]:X2}.");
        Require(
            unicast.AsSpan(0, unicast.Length - 2).SequenceEqual(multicast.AsSpan(0, multicast.Length - 2)),
            "The unicast-response bit changed bytes outside QCLASS.");
    }

    private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> values) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
