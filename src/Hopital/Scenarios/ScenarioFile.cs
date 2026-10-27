namespace Hopital.Scenarios;

/// <summary>
/// A scenario as written in YAML. Property names are camelCase in the file.
/// See scenarios/smoke-test.yaml for a commented example of every field.
/// </summary>
public sealed class ScenarioFile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Seed for the fake data. Same seed, same patients and orders.</summary>
    public int Seed { get; set; } = 1;

    /// <summary>How many patients to make up. They are numbered 1 to N, and patient N has order N.</summary>
    public int Patients { get; set; }

    /// <summary>
    /// Extra patients that are the same person as an existing one, registered again under a new MRN
    /// and account, e.g. { 3: 1 } makes patient 3 a duplicate of patient 1. Used to set up merges.
    /// </summary>
    public Dictionary<int, int> Duplicates { get; set; } = [];

    /// <summary>Time of the first appointment today, e.g. "08:00".</summary>
    public string FirstAppointment { get; set; } = "08:00";

    /// <summary>Time between appointments, e.g. "30m".</summary>
    public string AppointmentSpacing { get; set; } = "30m";

    /// <summary>Default wait between messages, e.g. "2s". A step's own delay replaces it.</summary>
    public string Spacing { get; set; } = "0s";

    /// <summary>How long to wait for each ACK.</summary>
    public string AckTimeout { get; set; } = "30s";

    public HeaderFile Header { get; set; } = new();

    public List<StepFile> Steps { get; set; } = [];
}

/// <summary>Optional MSH overrides; anything left out uses the defaults in Hl7Settings.</summary>
public sealed class HeaderFile
{
    public string? SendingApplication { get; set; }
    public string? SendingFacility { get; set; }
    public string? ReceivingApplication { get; set; }
    public string? ReceivingFacility { get; set; }
    public string? Version { get; set; }
    public string? ProcessingId { get; set; }
}

/// <summary>
/// One step: either send a message, wait, or repeat some steps for every patient.
/// </summary>
public sealed class StepFile
{
    /// <summary>
    /// Message to send: adt-a04, adt-a08, adt-a40, orm-new, orm-cancel, orm-status, siu-s12,
    /// resend (the previous message again, byte for byte) or raw (the <see cref="Text"/> as written).
    /// </summary>
    public string? Send { get; set; }

    /// <summary>
    /// raw only: the message text, one segment per line. {controlId} is replaced with a new
    /// control ID and {now} with the current time, so a raw message can still be matched to its ACK.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>MLLP framing: normal (default), no-trailing-cr, no-end-block, junk-before or none.</summary>
    public string? Framing { get; set; }

    /// <summary>Short description shown in the output instead of the generated one.</summary>
    public string? Label { get; set; }

    /// <summary>Patient number. Inside forEachPatient it defaults to the current patient.</summary>
    public int? Patient { get; set; }

    /// <summary>adt-a40 only: the patient number merged into <see cref="Patient"/>.</summary>
    public int? Merged { get; set; }

    /// <summary>orm-status only: the new ORC-5 status, e.g. IP (in progress) or CM (completed).</summary>
    public string? Status { get; set; }

    /// <summary>siu-s12 only: appointment length in minutes.</summary>
    public int? Minutes { get; set; }

    /// <summary>Use this MSH-10 instead of a new one, e.g. to send the same control ID twice.</summary>
    public string? ControlId { get; set; }

    /// <summary>Wait before this message, replacing the scenario's spacing, e.g. "500ms".</summary>
    public string? Delay { get; set; }

    /// <summary>A step on its own that just pauses, e.g. "10s".</summary>
    public string? Wait { get; set; }

    /// <summary>
    /// The ACK this message should get: AA (default), AE, AR, any (some ACK) or none
    /// (no ACK: a timeout or closed connection is the right result).
    /// </summary>
    public string? Expect { get; set; }

    /// <summary>Steps repeated for patients 1 to N, in order.</summary>
    public List<StepFile>? ForEachPatient { get; set; }
}
