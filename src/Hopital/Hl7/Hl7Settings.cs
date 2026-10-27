namespace Hopital.Hl7;

/// <summary>Header values for outgoing messages. A scenario can override any of them.</summary>
public sealed record Hl7Settings
{
    public string SendingApplication { get; init; } = "EPIC";
    public string SendingFacility { get; init; } = "HOPITAL";
    public string ReceivingApplication { get; init; } = "OIE";
    public string ReceivingFacility { get; init; } = "IMAGING";

    /// <summary>MSH-12. Epic interfaces are commonly 2.3 to 2.5.1.</summary>
    public string Version { get; init; } = "2.5.1";

    /// <summary>MSH-11: P (production), T (training) or D (debugging).</summary>
    public string ProcessingId { get; init; } = "P";
}
