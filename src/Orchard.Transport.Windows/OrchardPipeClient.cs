using System.IO.Pipes;
using Orchard.Protocol;

namespace Orchard.Transport.Windows;

/// <summary>Connects to an Orchard pipe on the local Windows host.</summary>
public static class OrchardPipeClient
{
    public static async ValueTask<OrchardPipeSession> ConnectAsync(
        OrchardPipeEndpoint endpoint,
        ProtocolPeerRole localRole = ProtocolPeerRole.Application,
        OrchardTransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var snapshot = (options ?? new OrchardTransportOptions()).ValidateAndSnapshot(localRole);
        var stream = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(snapshot.ConnectionTimeout);
                try
                {
                    await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(
                            "The pipe connection was canceled by the caller.",
                            exception,
                            cancellationToken);
                    }

                    throw new OrchardTransportException(
                        OrchardTransportErrorCodes.ConnectionTimeout,
                        "The local pipe server did not accept the connection before the deadline.",
                        exception);
                }
            }

            var session = await TransportHandshake.ConnectAsync(
                stream,
                endpoint,
                snapshot,
                cancellationToken).ConfigureAwait(false);
            stream = null!;
            return session;
        }
        catch (OrchardTransportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.IoFailure,
                "Windows denied access to the current-user-only named pipe.",
                exception);
        }
        catch (IOException exception)
        {
            throw new OrchardTransportException(
                OrchardTransportErrorCodes.ConnectionClosed,
                "The local named-pipe connection failed.",
                exception);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
