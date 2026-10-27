namespace Hopital.Hl7;

/// <summary>
/// Just enough HL7 parsing to read headers, ACKs and received results: segments,
/// fields and components, using the separators declared in MSH. No validation.
/// </summary>
public sealed class Hl7Parsed
{
    private Hl7Parsed(IReadOnlyList<string> segments, char fieldSeparator, char componentSeparator)
    {
        Segments = segments;
        FieldSeparator = fieldSeparator;
        ComponentSeparator = componentSeparator;
    }

    public IReadOnlyList<string> Segments { get; }
    public char FieldSeparator { get; }
    public char ComponentSeparator { get; }

    public string MessageType => Field("MSH", 9);
    public string ControlId => Field("MSH", 10);
    public string Version => Field("MSH", 12);

    /// <summary>Returns null when there is no usable MSH segment.</summary>
    public static Hl7Parsed? TryParse(string text)
    {
        var segments = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var msh = segments.FirstOrDefault(s => s.StartsWith("MSH", StringComparison.Ordinal) && s.Length >= 8);
        if (msh is null)
            return null;
        return new Hl7Parsed(segments, msh[3], msh[4]);
    }

    /// <summary>
    /// Field <paramref name="number"/> of the first segment with this ID, or "" if missing.
    /// Follows HL7 numbering, so for MSH, field 1 is the field separator itself.
    /// </summary>
    public string Field(string segmentId, int number)
    {
        var segment = Segments.FirstOrDefault(s => s.StartsWith(segmentId + FieldSeparator, StringComparison.Ordinal));
        if (segment is null)
            return "";

        var fields = segment.Split(FieldSeparator);
        if (segmentId == "MSH")
        {
            if (number == 1)
                return FieldSeparator.ToString();
            number--;
        }
        return number < fields.Length ? fields[number] : "";
    }

    /// <summary>Component <paramref name="component"/> (1-based) of a field, or "" if missing.</summary>
    public string Component(string segmentId, int field, int component)
    {
        var parts = Field(segmentId, field).Split(ComponentSeparator);
        return component - 1 < parts.Length ? parts[component - 1] : "";
    }

    /// <summary>
    /// Every segment with this ID (not MSH), split into fields so that index n is field n.
    /// </summary>
    public IEnumerable<string[]> AllSegments(string segmentId) =>
        Segments.Where(s => s.StartsWith(segmentId + FieldSeparator, StringComparison.Ordinal))
            .Select(s => s.Split(FieldSeparator));

    public bool HasSegment(string segmentId) =>
        Segments.Any(s => s.StartsWith(segmentId + FieldSeparator, StringComparison.Ordinal));
}
