using System.Text;

namespace Hopital.Hl7;

public static class Hl7Text
{
    /// <summary>All fake data is plain ASCII, so UTF-8 (no BOM) is safe on the wire.</summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Turns a message typed in a text file (CRLF or LF line endings) into wire format:
    /// segments separated by a single 0x0D, no blank lines.
    /// </summary>
    public static string ToWire(string text)
    {
        var segments = text.ReplaceLineEndings("\r")
            .Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\r', segments) + '\r';
    }

    /// <summary>Puts each segment on its own line for display or saving.</summary>
    public static string ToDisplay(string wire) =>
        string.Join(Environment.NewLine, wire.Split('\r', StringSplitOptions.RemoveEmptyEntries));
}
