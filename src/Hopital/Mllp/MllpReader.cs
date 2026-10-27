namespace Hopital.Mllp;

/// <summary>
/// Reads MLLP-framed messages from a stream. Handles messages split across reads
/// and several messages arriving in one read. Lenient on the receiving side:
/// junk before a start block is skipped and a missing trailing 0x0D is accepted,
/// but both are reported on the returned <see cref="MllpMessage"/>.
/// </summary>
public sealed class MllpReader(Stream stream, int maxMessageBytes = MllpReader.DefaultMaxMessageBytes)
{
    // Results can carry embedded images, so allow large messages.
    public const int DefaultMaxMessageBytes = 64 * 1024 * 1024;

    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _position;
    private int _length;
    private int _pushedBack = -1;
    private bool _skipTrailingCr;

    /// <summary>
    /// Bytes that arrived after the last message but never formed one (no start block) before
    /// the stream ended, e.g. a sender that does not use MLLP framing at all.
    /// </summary>
    public int UnframedBytesAtClose { get; private set; }

    /// <summary>
    /// Returns the next message, or null if the stream ended cleanly between messages.
    /// Throws <see cref="MllpProtocolException"/> if it ends partway through a message
    /// or a message is larger than the limit.
    /// </summary>
    public async Task<MllpMessage?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        var skipped = 0;

        // The last message ended with 0x1C before its 0x0D had arrived: drop that 0x0D now.
        if (_skipTrailingCr)
        {
            _skipTrailingCr = false;
            var b = await ReadByteAsync(cancellationToken);
            if (b < 0)
                return null;
            if (b != MllpFrame.CarriageReturn)
                _pushedBack = b;
        }

        // Find the start block.
        while (true)
        {
            var b = await ReadByteAsync(cancellationToken);
            if (b < 0)
            {
                UnframedBytesAtClose = skipped;
                return null;
            }
            if (b == MllpFrame.StartBlock)
                break;
            skipped++;
        }

        var payload = new MemoryStream();
        while (true)
        {
            var b = await ReadByteAsync(cancellationToken);
            if (b < 0)
                throw new MllpProtocolException($"Connection closed partway through a message ({payload.Length} bytes received, no end block).");

            if (b == MllpFrame.EndBlock)
            {
                // The message is complete at 0x1C. Do not wait for the 0x0D: a sender that leaves
                // it out would otherwise never get its ACK. Check it only if it is already here.
                if (_position == _length && _pushedBack < 0)
                {
                    _skipTrailingCr = true;
                    return new MllpMessage(payload.ToArray(), skipped, MissingTrailingCr: false);
                }

                var next = await ReadByteAsync(cancellationToken);
                if (next == MllpFrame.CarriageReturn)
                    return new MllpMessage(payload.ToArray(), skipped, MissingTrailingCr: false);
                if (next >= 0)
                    _pushedBack = next;
                return new MllpMessage(payload.ToArray(), skipped, MissingTrailingCr: true);
            }

            if (b == MllpFrame.StartBlock)
            {
                // A new message started before the last one ended: drop the partial one.
                skipped += (int)payload.Length + 1;
                payload.SetLength(0);
                continue;
            }

            if (payload.Length >= maxMessageBytes)
                throw new MllpProtocolException($"Message is larger than the {maxMessageBytes:N0} byte limit.");
            payload.WriteByte((byte)b);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_pushedBack >= 0)
        {
            var b = _pushedBack;
            _pushedBack = -1;
            return b;
        }

        if (_position == _length)
        {
            _length = await stream.ReadAsync(_buffer, cancellationToken);
            _position = 0;
            if (_length == 0)
                return -1;
        }

        return _buffer[_position++];
    }
}
