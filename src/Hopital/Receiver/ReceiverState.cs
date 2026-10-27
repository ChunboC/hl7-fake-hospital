namespace Hopital.Receiver;

/// <summary>How the fake Epic receiver answers results sent to it.</summary>
public enum ReceiverMode
{
    /// <summary>Reply AA.</summary>
    Accept,

    /// <summary>Reply AE: a temporary problem, so the sender should retry later.</summary>
    Error,

    /// <summary>Reply AR: refused, so retrying the same message will not help.</summary>
    Reject,

    /// <summary>Read the message and never reply, so the sender times out.</summary>
    Hang,

    /// <summary>Read the message and close the connection without replying.</summary>
    Drop,

    /// <summary>Stop listening: connections are refused and open ones are closed.</summary>
    Offline,
}

public sealed record ReceiverStatus(
    ReceiverMode Mode,
    int? RemainingMessages,
    DateTimeOffset? Until,
    string? Text,
    long Received,
    DateTimeOffset? LastReceived);

/// <summary>
/// The current mode, safe to change from the control API or the keyboard while messages arrive.
/// A mode can be temporary: for the next N messages, or for a length of time, after which it goes back to Accept.
/// </summary>
public sealed class ReceiverState(TimeProvider clock)
{
    private readonly Lock _lock = new();
    private ReceiverMode _mode = ReceiverMode.Accept;
    private int? _remaining;
    private DateTimeOffset? _until;
    private string? _text;
    private long _received;
    private DateTimeOffset? _lastReceived;
    private ITimer? _timer;

    // Bumped on every change, so a timer from an earlier mode that fires late does nothing.
    private int _generation;

    /// <summary>Raised after every mode change, including a temporary mode running out.</summary>
    public event Action<ReceiverStatus>? Changed;

    public ReceiverStatus Status
    {
        get
        {
            lock (_lock)
                return Snapshot();
        }
    }

    /// <param name="messages">Go back to Accept after this many messages.</param>
    /// <param name="duration">Go back to Accept after this long.</param>
    /// <param name="text">Text for the AE/AR (MSA-3 and ERR).</param>
    public ReceiverStatus Set(ReceiverMode mode, int? messages = null, TimeSpan? duration = null, string? text = null)
    {
        if (messages is <= 0)
            throw new ArgumentOutOfRangeException(nameof(messages), "Must be 1 or more.");
        if (duration is { } d && d <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Must be longer than zero.");

        ReceiverStatus status;
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _generation++;
            _mode = mode;
            _remaining = mode == ReceiverMode.Accept ? null : messages;
            _text = text;
            _until = mode == ReceiverMode.Accept || duration is null ? null : clock.GetUtcNow() + duration;
            if (_until is not null)
            {
                var generation = _generation;
                _timer = clock.CreateTimer(_ => Expire(generation), null, duration!.Value, Timeout.InfiniteTimeSpan);
            }
            status = Snapshot();
        }
        Changed?.Invoke(status);
        return status;
    }

    /// <summary>
    /// Called once per received message: returns the mode to answer it with and the AE/AR text,
    /// and counts down a temporary mode.
    /// </summary>
    public (ReceiverMode Mode, string? Text) NextMessage()
    {
        ReceiverStatus? reverted = null;
        (ReceiverMode, string?) answer;
        lock (_lock)
        {
            _received++;
            _lastReceived = clock.GetUtcNow();
            answer = (_mode, _text);
            if (_remaining is not null && --_remaining == 0)
            {
                Reset();
                reverted = Snapshot();
            }
        }
        if (reverted is not null)
            Changed?.Invoke(reverted);
        return answer;
    }

    private void Expire(int generation)
    {
        ReceiverStatus status;
        lock (_lock)
        {
            if (generation != _generation)
                return;
            Reset();
            status = Snapshot();
        }
        Changed?.Invoke(status);
    }

    private void Reset()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _mode = ReceiverMode.Accept;
        _remaining = null;
        _until = null;
        _text = null;
    }

    private ReceiverStatus Snapshot() => new(_mode, _remaining, _until, _text, _received, _lastReceived);
}
