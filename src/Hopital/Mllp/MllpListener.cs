using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Hopital.Mllp;

/// <summary>What to do after a message: send a reply, send nothing, or close the connection.</summary>
public sealed record MllpReply(byte[]? Payload, bool CloseConnection)
{
    public static MllpReply Send(byte[] payload) => new(payload, false);

    public static readonly MllpReply None = new(null, false);

    public static readonly MllpReply Close = new(null, true);
}

/// <summary>Handles one received message and says how to answer it.</summary>
public delegate Task<MllpReply> MllpMessageHandler(MllpMessage message, EndPoint? remote, CancellationToken cancellationToken);

/// <summary>
/// Accepts MLLP connections and passes each message to a handler. Each connection can carry
/// any number of messages; they are handled one after another, in order.
/// </summary>
public sealed class MllpListener(IPAddress address, int port, MllpMessageHandler handler, ILogger logger)
{
    private readonly TcpListener _tcp = new(address, port);

    /// <summary>The port actually listened on (useful when started with port 0).</summary>
    public int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;

    public void Start()
    {
        // Lets a receiver that went offline listen on the same port again right away.
        // Not on Windows, where this option would let another program share the port.
        if (!OperatingSystem.IsWindows())
            _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcp.Start();
    }

    /// <summary>
    /// Accepts connections until cancelled. Call <see cref="Start"/> first. Cancelling also
    /// closes every open connection.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(HandleConnectionAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _tcp.Stop();
            await Task.WhenAll(connections);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var remote = client.Client.RemoteEndPoint;
        logger.LogInformation("Connection from {Remote}", remote);
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new MllpReader(stream);
                while (await reader.ReadMessageAsync(cancellationToken) is { } message)
                {
                    if (message.SkippedBytes > 0)
                        logger.LogWarning("Skipped {Count} bytes outside MLLP framing from {Remote}", message.SkippedBytes, remote);
                    if (message.MissingTrailingCr)
                        logger.LogWarning("End block from {Remote} was not followed by 0x0D", remote);

                    var reply = await handler(message, remote, cancellationToken);
                    if (reply.Payload is not null)
                    {
                        await stream.WriteAsync(MllpFrame.Wrap(reply.Payload), cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                    if (reply.CloseConnection)
                    {
                        logger.LogInformation("Closing connection from {Remote} on purpose", remote);
                        return;
                    }
                }
                if (reader.UnframedBytesAtClose > 0)
                    logger.LogWarning("{Remote} sent {Count} bytes with no MLLP start block before closing; they were discarded",
                        remote, reader.UnframedBytesAtClose);
            }
            logger.LogInformation("Connection from {Remote} closed", remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MllpProtocolException ex)
        {
            logger.LogWarning("Dropped connection from {Remote}: {Reason}", remote, ex.Message);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Connection from {Remote} failed: {Reason}", remote, ex.Message);
        }
    }
}
