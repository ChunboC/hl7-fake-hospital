namespace Hopital.Hl7;

/// <summary>HL7 escape sequences for the standard separators |^~\&amp;.</summary>
public static class Hl7Escape
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value
            .Replace(@"\", @"\E\")
            .Replace("|", @"\F\")
            .Replace("^", @"\S\")
            .Replace("~", @"\R\")
            .Replace("&", @"\T\");
    }

    /// <summary>Escapes each part and joins them as components, dropping trailing empty ones.</summary>
    public static string Components(params string?[] parts)
    {
        var count = parts.Length;
        while (count > 0 && string.IsNullOrEmpty(parts[count - 1]))
            count--;
        return string.Join('^', parts.Take(count).Select(Escape));
    }
}
