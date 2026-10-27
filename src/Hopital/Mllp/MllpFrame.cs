namespace Hopital.Mllp;

/// <summary>
/// MLLP framing: every message is sent as 0x0B, payload, 0x1C, 0x0D.
/// </summary>
public static class MllpFrame
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[payload.Length + 3];
        framed[0] = StartBlock;
        payload.CopyTo(framed.AsSpan(1));
        framed[^2] = EndBlock;
        framed[^1] = CarriageReturn;
        return framed;
    }

    /// <summary>Frames a payload correctly or, for testing receivers, in one of the common broken ways.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> payload, MllpFraming framing) => framing switch
    {
        MllpFraming.Normal => Wrap(payload),
        MllpFraming.NoTrailingCr => [StartBlock, .. payload, EndBlock],
        MllpFraming.NoEndBlock => [StartBlock, .. payload],
        MllpFraming.JunkBefore => [.. "garbage\r\n"u8, .. Wrap(payload)],
        MllpFraming.None => payload.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(framing)),
    };
}

public enum MllpFraming
{
    /// <summary>0x0B payload 0x1C 0x0D.</summary>
    Normal,

    /// <summary>0x0B payload 0x1C: the final 0x0D is missing.</summary>
    NoTrailingCr,

    /// <summary>0x0B payload: never ends, so the receiver keeps waiting.</summary>
    NoEndBlock,

    /// <summary>Some stray bytes, then a correct frame.</summary>
    JunkBefore,

    /// <summary>The payload alone, with no MLLP framing at all.</summary>
    None,
}

/// <summary>
/// One message read off the wire, plus what the reader had to tolerate to get it.
/// </summary>
/// <param name="Payload">The bytes between the start and end block, without framing.</param>
/// <param name="SkippedBytes">Bytes thrown away before the start block, or from an earlier message cut off by a new start block.</param>
/// <param name="MissingTrailingCr">
/// True when the byte after the end block had already arrived and was not 0x0D. When the sender
/// pauses right after 0x1C the next byte is not known yet, so this stays false.
/// </param>
public sealed record MllpMessage(byte[] Payload, int SkippedBytes, bool MissingTrailingCr);

public sealed class MllpProtocolException(string message) : Exception(message);
