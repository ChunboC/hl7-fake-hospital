using System.Net.Sockets;

namespace Hopital.Mllp;

/// <summary>
/// One MLLP connection to a remote listener (for example Open Integration Engine).
/// </summary>
public sealed class MllpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly MllpReader _reader;

    private MllpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new MllpReader(_stream);
    }

    public static async Task<MllpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken);
            return new MllpClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Sends one message wrapped in MLLP framing.</summary>
    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(MllpFrame.Wrap(payload), cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>Sends bytes exactly as given, with no framing. Used to send broken frames on purpose.</summary>
    public async Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the next message (normally the ACK). Returns null if the other side closed the connection.
    /// Throws <see cref="TimeoutException"/> if nothing complete arrives in time.
    /// </summary>
    public async Task<MllpMessage?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _reader.ReadMessageAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No reply within {timeout.TotalSeconds:0.#} s.");
        }
    }

    public async Task<MllpMessage?> SendAndReceiveAsync(byte[] payload, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await SendAsync(payload, cancellationToken);
        return await ReceiveAsync(timeout, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
