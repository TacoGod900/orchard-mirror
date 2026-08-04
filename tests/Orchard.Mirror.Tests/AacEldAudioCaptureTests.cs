using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

/// <summary>
/// The audio leg has to survive the whole way from a socket to a file before it is worth pointing a
/// decoder at it. This drives a real loopback socket rather than calling the parser directly, so a
/// capture that binds the wrong port or never flushes cannot pass.
/// </summary>
internal static class AacEldAudioCaptureTests
{
    internal static void WritesLengthPrefixedAccessUnitsFromTheSocket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"orchard-audio-{Guid.NewGuid():N}.aacel");
        try
        {
            AacEldAudioCapture? capture = AacEldAudioCapture.TryStart(path);
            Assert(capture is not null, "A capture with a valid path must start.");

            byte[][] units = [[0x11, 0x22, 0x33], [0x44], [0x55, 0x66]];
            using (Socket sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                IPEndPoint destination = new(IPAddress.Loopback, capture!.Port);
                for (int index = 0; index < units.Length; index++)
                {
                    sender.SendTo(Datagram((ushort)(index + 1), units[index]), destination);
                }

                // A datagram from the video leg shares nothing with this one and must not be filed
                // as audio.
                sender.SendTo(Datagram(99, [0xFF, 0xFF], payloadType: 96), destination);
                WaitUntil(() => capture.AccessUnitsCaptured == units.Length, "three access units to arrive");
            }

            capture!.DisposeAsync().AsTask().GetAwaiter().GetResult();

            byte[] written = File.ReadAllBytes(path);
            int offset = 0;
            foreach (byte[] expected in units)
            {
                Assert(offset + 4 <= written.Length, "The file ended before a length prefix.");
                int length = BinaryPrimitives.ReadInt32BigEndian(written.AsSpan(offset));
                offset += 4;
                Assert(length == expected.Length, $"Length prefix was {length}, expected {expected.Length}.");
                Assert(
                    written.AsSpan(offset, length).SequenceEqual(expected),
                    "An access unit was not written back byte for byte.");
                offset += length;
            }

            Assert(offset == written.Length, $"{written.Length - offset} unexpected trailing bytes were written.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// No configured path means the audio leg is never requested at all, which is what keeps the
    /// phone from encoding a stream nothing consumes.
    /// </summary>
    internal static void StartsNothingWithoutAConfiguredPath()
    {
        Assert(AacEldAudioCapture.TryStart(null) is null, "A null path must not start a capture.");
        Assert(AacEldAudioCapture.TryStart("   ") is null, "A blank path must not start a capture.");
    }

    private static byte[] Datagram(ushort sequence, byte[] payload, byte payloadType = 101)
    {
        byte[] datagram = new byte[12 + payload.Length];
        datagram[0] = 0x80;
        datagram[1] = payloadType;
        datagram[2] = (byte)(sequence >> 8);
        datagram[3] = (byte)sequence;
        payload.CopyTo(datagram, 12);
        return datagram;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new InvalidOperationException($"Timed out waiting for {what}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
