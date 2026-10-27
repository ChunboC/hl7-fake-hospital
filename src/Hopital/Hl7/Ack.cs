using static Hopital.Hl7.Hl7Escape;

namespace Hopital.Hl7;

public enum AckStatus
{
    /// <summary>AA (or CA in enhanced mode).</summary>
    Accepted,

    /// <summary>AE (or CE): the receiver had a problem processing it; the sender may retry.</summary>
    Error,

    /// <summary>AR (or CR): the receiver refused it; retrying the same message will not help.</summary>
    Rejected,

    /// <summary>The reply was not an ACK we can read (no MSH or MSA, or an unknown code).</summary>
    Invalid,
}

/// <param name="Code">MSA-1 as received.</param>
/// <param name="AckedControlId">MSA-2: the MSH-10 this ACK answers.</param>
/// <param name="Text">MSA-3 text message, if any.</param>
/// <param name="ErrorText">From ERR: ERR-8 (v2.5+) or ERR-1 (older), if any.</param>
public sealed record AckResult(AckStatus Status, string Code, string AckedControlId, string Text, string ErrorText)
{
    public static AckResult Parse(string reply)
    {
        var parsed = Hl7Parsed.TryParse(reply);
        if (parsed is null || !parsed.HasSegment("MSA"))
            return new AckResult(AckStatus.Invalid, "", "", "", "");

        var code = parsed.Field("MSA", 1);
        var status = code switch
        {
            "AA" or "CA" => AckStatus.Accepted,
            "AE" or "CE" => AckStatus.Error,
            "AR" or "CR" => AckStatus.Rejected,
            _ => AckStatus.Invalid,
        };
        var errorText = parsed.Field("ERR", 8);
        if (errorText == "")
            errorText = parsed.Field("ERR", 1);
        return new AckResult(status, code, parsed.Field("MSA", 2), parsed.Field("MSA", 3), errorText);
    }
}

/// <summary>
/// Builds the ACK a receiver sends back. Header applications and facilities are swapped
/// from the incoming message and the version is echoed, so a 2.3 sender gets a 2.3 ACK.
/// </summary>
public static class AckBuilder
{
    /// <param name="message">The received message, in wire format.</param>
    /// <param name="code">AA, AE or AR.</param>
    /// <param name="errorText">For AE/AR: goes in MSA-3 and ERR.</param>
    /// <param name="errorCode">HL7 table 0357 code for ERR, e.g. 207 (application internal error) or 200 (unsupported message type).</param>
    public static string For(string message, string code = "AA", string? errorText = null, string errorCode = "207")
    {
        var incoming = Hl7Parsed.TryParse(message);
        var now = DateTime.Now.ToString("yyyyMMddHHmmss");
        var controlId = "ACK" + Guid.NewGuid().ToString("N")[..17].ToUpperInvariant();

        if (incoming is null)
        {
            // Nothing to echo: reject in the default 2.5.1 format.
            var reason = errorText ?? "No MSH segment";
            return $"MSH|^~\\&|HOPITAL||||{now}||ACK|{controlId}|P|2.5.1\r"
                 + $"MSA|AR||{Escape(reason)}\r"
                 + Err("2.5.1", "100", "Segment sequence error", reason);
        }

        string F(int n) => incoming.Field("MSH", n);
        var trigger = incoming.Component("MSH", 9, 2);
        var version = F(12);
        var ack = string.Join('|', "MSH", @"^~\&", F(5), F(6), F(3), F(4), now, "", $"ACK^{trigger}^ACK", controlId, F(11), version) + "\r"
                + $"MSA|{code}|{incoming.ControlId}" + (errorText is null ? "" : "|" + Escape(errorText)) + "\r";
        if (code != "AA" && errorText is not null)
            ack += Err(version, errorCode, ErrorCodeName(errorCode), errorText);
        return ack;
    }

    /// <summary>ERR changed shape in v2.5: before that, everything went in ERR-1.</summary>
    private static string Err(string version, string code, string codeName, string text)
    {
        var isPre25 = version.StartsWith("2.1") || version.StartsWith("2.2") || version.StartsWith("2.3") || version.StartsWith("2.4");
        return isPre25
            ? $"ERR|^^^{code}&{Escape(codeName)}&HL70357\r"
            : $"ERR|||{Components(code, codeName, "HL70357")}|E||||{Escape(text)}\r";
    }

    private static string ErrorCodeName(string code) => code switch
    {
        "100" => "Segment sequence error",
        "101" => "Required field missing",
        "102" => "Data type error",
        "103" => "Table value not found",
        "200" => "Unsupported message type",
        "201" => "Unsupported event code",
        "202" => "Unsupported processing id",
        "203" => "Unsupported version id",
        "204" => "Unknown key identifier",
        "205" => "Duplicate key identifier",
        "206" => "Application record locked",
        _ => "Application internal error",
    };
}
