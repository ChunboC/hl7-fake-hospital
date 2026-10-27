using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hopital.Hl7;

namespace Hopital.Receiver;

/// <summary>An image or document found in an OBX segment.</summary>
/// <param name="Obx">OBX-1 set ID (or its position if OBX-1 is empty).</param>
/// <param name="ValueType">OBX-2, e.g. ED (embedded data) or RP (reference pointer).</param>
/// <param name="File">Saved file name, for embedded data that decoded.</param>
/// <param name="Reference">Pointer or URL, for referenced data.</param>
/// <param name="Problem">Why embedded data could not be saved.</param>
public sealed record Attachment(int Obx, string ValueType, string? MediaType, string? File, long? Bytes, string? Reference, string? Problem);

/// <summary>What was received, written next to the message as JSON for quick inspection.</summary>
/// <param name="File">The saved message, relative to the store's root, e.g. 2026-10-26/095815-253-0001_ORU-R01_X.hl7.</param>
public sealed record ReceivedSummary(
    DateTimeOffset ReceivedAt,
    string? From,
    int Bytes,
    string MessageType,
    string ControlId,
    string Version,
    string PatientMrn,
    string PatientName,
    string PlacerOrderNumber,
    string FillerOrderNumber,
    string AccessionNumber,
    string DocumentNumber,
    IReadOnlyList<Attachment> Attachments,
    string Mode,
    string? Ack,
    IReadOnlyList<string> Warnings,
    string File);

/// <summary>
/// Saves every received message exactly as it arrived (.hl7), a JSON summary next to it,
/// and any embedded images or documents as their own files, in one folder per day.
/// </summary>
public sealed partial class MessageStore(string root, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, (string Extension, string MediaType)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPEG"] = (".jpg", "image/jpeg"),
        ["JPG"] = (".jpg", "image/jpeg"),
        ["PNG"] = (".png", "image/png"),
        ["GIF"] = (".gif", "image/gif"),
        ["BMP"] = (".bmp", "image/bmp"),
        ["TIFF"] = (".tif", "image/tiff"),
        ["TIF"] = (".tif", "image/tiff"),
        ["DICOM"] = (".dcm", "application/dicom"),
        ["PDF"] = (".pdf", "application/pdf"),
        ["RTF"] = (".rtf", "application/rtf"),
        ["HTML"] = (".html", "text/html"),
        ["XML"] = (".xml", "application/xml"),
        ["TEXT"] = (".txt", "text/plain"),
    };

    private int _sequence;

    public string Root => Path.GetFullPath(root);

    public ReceivedSummary Save(byte[] payload, string? from, string mode, string? ack, IReadOnlyList<string> warnings)
    {
        var now = clock.GetLocalNow();
        var text = Hl7Text.Encoding.GetString(payload);
        var hl7 = Hl7Parsed.TryParse(text);

        var folder = Path.Combine(Root, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var type = hl7?.Component("MSH", 9, 1) is { Length: > 0 } code ? code + "-" + hl7.Component("MSH", 9, 2) : "unparsed";
        var controlId = hl7?.ControlId ?? "";
        // A per-run sequence keeps names unique when several messages arrive in the same millisecond.
        var baseName = $"{now:HHmmss-fff}-{Interlocked.Increment(ref _sequence):D4}_{Safe(type)}" + (controlId == "" ? "" : "_" + Safe(controlId));
        File.WriteAllBytes(Path.Combine(folder, baseName + ".hl7"), payload);

        var attachments = hl7 is null ? [] : SaveAttachments(hl7, folder, baseName);
        string F(string segment, int field) => hl7?.Field(segment, field) ?? "";
        string C(string segment, int field, int component) => hl7?.Component(segment, field, component) ?? "";

        var summary = new ReceivedSummary(
            ReceivedAt: now,
            From: from,
            Bytes: payload.Length,
            MessageType: F("MSH", 9),
            ControlId: controlId,
            Version: F("MSH", 12),
            PatientMrn: C("PID", 3, 1),
            PatientName: string.Join(", ", new[] { C("PID", 5, 1), C("PID", 5, 2) }.Where(s => s != "")),
            PlacerOrderNumber: FirstOf(C("ORC", 2, 1), C("OBR", 2, 1), C("TXA", 14, 1)),
            FillerOrderNumber: FirstOf(C("ORC", 3, 1), C("OBR", 3, 1), C("TXA", 15, 1)),
            AccessionNumber: F("OBR", 18),
            DocumentNumber: C("TXA", 12, 1),
            Attachments: attachments,
            Mode: mode,
            Ack: ack is null ? null : Hl7Text.ToDisplay(ack),
            Warnings: hl7 is null ? [.. warnings, "No MSH segment: saved as is."] : warnings,
            File: $"{now:yyyy-MM-dd}/{baseName}.hl7");

        File.WriteAllText(Path.Combine(folder, baseName + ".json"), JsonSerializer.Serialize(summary, Json));
        return summary;
    }

    /// <summary>The newest saved messages first, read from their JSON summaries.</summary>
    public IReadOnlyList<ReceivedSummary> Recent(int limit)
    {
        var recent = new List<ReceivedSummary>();
        if (limit <= 0 || !Directory.Exists(Root))
            return recent;

        var days = Directory.GetDirectories(Root)
            .Select(Path.GetFileName)
            .Where(d => d is not null && DayFolder().IsMatch(d))
            .OrderDescending(StringComparer.Ordinal);
        foreach (var day in days)
        {
            // Names start with the time and a sequence number, so name order is arrival order.
            var summaries = Directory.GetFiles(Path.Combine(Root, day!), "*.json")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
            foreach (var path in summaries)
            {
                if (ReadSummary(path) is { } summary)
                    recent.Add(summary);
                if (recent.Count == limit)
                    return recent;
            }
        }
        return recent;
    }

    /// <summary>One message's summary, by day folder and name without extension; null if not found.</summary>
    public ReceivedSummary? Read(string day, string name) =>
        PathOf(day, name + ".json") is { } path ? ReadSummary(path) : null;

    /// <summary>
    /// Full path of a saved file (message, summary or attachment), or null if it does not exist.
    /// Only plain names inside a day folder are allowed, so a request can never reach outside the store.
    /// </summary>
    public string? PathOf(string day, string file)
    {
        if (!DayFolder().IsMatch(day) || !SafeFileName().IsMatch(file))
            return null;
        var path = Path.Combine(Root, day, file);
        return File.Exists(path) ? path : null;
    }

    private static ReceivedSummary? ReadSummary(string path)
    {
        try
        {
            var summary = JsonSerializer.Deserialize<ReceivedSummary>(File.ReadAllText(path), Json);
            // Summaries saved on Windows by older versions used backslashes.
            return summary is null ? null : summary with { File = summary.File.Replace('\\', '/') };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static List<Attachment> SaveAttachments(Hl7Parsed hl7, string folder, string baseName)
    {
        var attachments = new List<Attachment>();
        var position = 0;
        foreach (var fields in hl7.AllSegments("OBX"))
        {
            position++;
            string Field(int n) => n < fields.Length ? fields[n] : "";
            var setId = int.TryParse(Field(1), out var id) ? id : position;
            var valueType = Field(2).ToUpperInvariant();
            var value = Field(5);
            var parts = value.Split(hl7.ComponentSeparator);
            string Part(int n) => n < parts.Length ? parts[n] : "";

            if (valueType == "ED")
            {
                // ED: source application ^ type of data ^ data subtype ^ encoding ^ data
                var (extension, mediaType) = Types.GetValueOrDefault(Part(2), Types.GetValueOrDefault(Part(1), (".bin", "application/octet-stream")));
                var data = Decode(Part(3), Part(4), out var problem);
                if (data is null)
                {
                    attachments.Add(new Attachment(setId, valueType, mediaType, null, null, null, problem));
                    continue;
                }
                var file = $"{baseName}_obx{setId}{extension}";
                File.WriteAllBytes(Path.Combine(folder, file), data);
                attachments.Add(new Attachment(setId, valueType, mediaType, file, data.Length, null, null));
            }
            else if (valueType == "RP")
            {
                // RP: pointer ^ application ID ^ type of data ^ subtype
                var mediaType = Types.TryGetValue(Part(3), out var t) || Types.TryGetValue(Part(2), out t) ? t.MediaType : null;
                attachments.Add(new Attachment(setId, valueType, mediaType, null, null, Part(0), null));
            }
            else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                attachments.Add(new Attachment(setId, valueType, null, null, null, value, null));
            }
        }
        return attachments;
    }

    private static byte[]? Decode(string encoding, string data, out string? problem)
    {
        problem = null;
        if (data == "")
        {
            problem = "ED has no data (OBX-5.5 is empty).";
            return null;
        }
        try
        {
            switch (encoding.ToUpperInvariant())
            {
                case "BASE64":
                case "B64":
                    // Some senders wrap long Base64 with HL7 line-break escapes or whitespace.
                    return Convert.FromBase64String(Whitespace().Replace(data.Replace(@"\.br\", ""), ""));
                case "HEX":
                    return Convert.FromHexString(data);
                case "A":
                    return Hl7Text.Encoding.GetBytes(data);
                default:
                    problem = $"Unknown ED encoding '{encoding}' (expected Base64, Hex or A).";
                    return null;
            }
        }
        catch (FormatException ex)
        {
            problem = $"{encoding} data does not decode: {ex.Message}";
            return null;
        }
    }

    private static string FirstOf(params string[] values) => values.FirstOrDefault(v => v != "") ?? "";

    private static string Safe(string value) => UnsafeCharacters().Replace(value, "-");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[^A-Za-z0-9-]")]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayFolder();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex SafeFileName();
}
