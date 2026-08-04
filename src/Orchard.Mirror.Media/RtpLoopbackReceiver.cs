using System.Net;
using System.Net.Sockets;

namespace Orchard.Mirror.Media;

/// <summary>
/// Receives RTP datagrams relayed by the Python CoreDevice agent on a private loopback UDP port.
/// The socket is bound before the agent negotiates DisplayService, so the first packet cannot race
/// receiver setup.
/// </summary>
public sealed class RtpLoopbackReceiver : IDisposable
{
    private readonly Socket socket;
    private bool disposed;

    /// <summary>Bind an ephemeral IPv4 loopback port.</summary>
    public RtpLoopbackReceiver()
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 8 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>The bound port to pass to the agent's <c>start-video</c> command.</summary>
    public int Port { get; }

    /// <summary>Receive one complete UDP datagram. The returned array owns its bytes.</summary>
    public async ValueTask<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] buffer = new byte[65_535];
        int length = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>Close the loopback socket and wake a pending receive.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        socket.Dispose();
    }
}
