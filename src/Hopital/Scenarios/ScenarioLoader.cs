using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hopital.Scenarios;

public sealed class ScenarioException(string message) : Exception(message);

public static class ScenarioLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ScenarioFile Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>Reads YAML. Unknown keys are errors, so a typo is caught instead of ignored.</summary>
    public static ScenarioFile Parse(string yaml, string source = "scenario")
    {
        try
        {
            var file = Deserializer.Deserialize<ScenarioFile>(yaml)
                ?? throw new ScenarioException($"{source} is empty.");
            if (string.IsNullOrWhiteSpace(file.Name))
                file.Name = Path.GetFileNameWithoutExtension(source);
            return file;
        }
        catch (YamlException ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            throw new ScenarioException($"{source} line {ex.Start.Line}, column {ex.Start.Column}: {reason}");
        }
    }
}

public static class Durations
{
    /// <summary>Parses "250ms", "2s", "1.5m", "1h" or "0".</summary>
    public static TimeSpan Parse(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        (string Suffix, Func<double, TimeSpan> Make)[] units =
        [
            ("ms", TimeSpan.FromMilliseconds),
            ("s", TimeSpan.FromSeconds),
            ("m", TimeSpan.FromMinutes),
            ("h", TimeSpan.FromHours),
        ];

        if (value == "0")
            return TimeSpan.Zero;
        foreach (var (suffix, make) in units)
        {
            if (value.EndsWith(suffix)
                && double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && number >= 0)
                return make(number);
        }
        throw new ScenarioException($"'{text}' is not a duration. Use e.g. 250ms, 2s, 1.5m or 1h.");
    }
}
