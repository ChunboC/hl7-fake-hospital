using System.Globalization;
using Hopital.Generation;
using Hopital.Hl7;
using Hopital.Mllp;

namespace Hopital.Scenarios;

/// <summary>
/// One message to send. It is built just before sending, so MSH-7 is the actual send time
/// and control IDs are handed out in send order.
/// </summary>
/// <param name="Expect">AA, AE, AR, ANY or NONE.</param>
public sealed record PlannedSend(int Number, string Label, TimeSpan DelayBefore, string Expect, MllpFraming Framing, Func<BuiltMessage> Build);

public sealed record ScenarioPlan(string Name, string? Description, TimeSpan AckTimeout, IReadOnlyList<PlannedSend> Sends);

/// <summary>
/// Turns a scenario file into a flat list of sends: makes up the patients and orders,
/// expands forEachPatient, works out delays and checks every step. All problems are
/// reported together, each with its step number.
/// </summary>
public static class ScenarioPlanner
{
    private static readonly string[] Expectations = ["AA", "AE", "AR", "ANY", "NONE"];

    private static readonly Dictionary<string, MllpFraming> Framings = new()
    {
        ["normal"] = MllpFraming.Normal,
        ["no-trailing-cr"] = MllpFraming.NoTrailingCr,
        ["no-end-block"] = MllpFraming.NoEndBlock,
        ["junk-before"] = MllpFraming.JunkBefore,
        ["none"] = MllpFraming.None,
    };

    public static ScenarioPlan Build(ScenarioFile file, TimeProvider clock)
    {
        var errors = new List<string>();
        TimeSpan Duration(string text, string what)
        {
            try
            {
                return Durations.Parse(text);
            }
            catch (ScenarioException ex)
            {
                errors.Add($"{what}: {ex.Message}");
                return TimeSpan.Zero;
            }
        }

        var spacing = Duration(file.Spacing, "spacing");
        var ackTimeout = Duration(file.AckTimeout, "ackTimeout");
        var appointmentSpacing = Duration(file.AppointmentSpacing, "appointmentSpacing");
        if (!TimeOnly.TryParseExact(file.FirstAppointment, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstAppointment))
            errors.Add($"firstAppointment: '{file.FirstAppointment}' is not a time like 08:00.");
        if (file.Patients < 0)
            errors.Add("patients: must be 0 or more.");

        // Make up everyone first, so the data does not depend on which steps use it.
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var generator = new FakeDataGenerator(file.Seed, today);
        var firstSlot = today.ToDateTime(firstAppointment);
        var patients = new Dictionary<int, FakePatient>();
        var orders = new Dictionary<int, FakeOrder>();
        for (var n = 1; n <= file.Patients; n++)
        {
            patients[n] = generator.NextPatient();
            orders[n] = generator.NextOrder(firstSlot + appointmentSpacing * (n - 1));
        }
        foreach (var (number, original) in file.Duplicates.OrderBy(d => d.Key))
        {
            if (number <= file.Patients)
                errors.Add($"duplicates: {number} is already a patient; duplicates must be numbered after {file.Patients}.");
            else if (!patients.ContainsKey(original) || original > file.Patients)
                errors.Add($"duplicates: {number} is a duplicate of {original}, which is not a patient (1 to {file.Patients}).");
            else
            {
                var newIds = generator.NextPatient();
                patients[number] = patients[original] with { Mrn = newIds.Mrn, AccountNumber = newIds.AccountNumber };
                orders[number] = generator.NextOrder(firstSlot + appointmentSpacing * (number - 1));
            }
        }

        var header = file.Header;
        var defaults = new Hl7Settings();
        var settings = new Hl7Settings
        {
            SendingApplication = header.SendingApplication ?? defaults.SendingApplication,
            SendingFacility = header.SendingFacility ?? defaults.SendingFacility,
            ReceivingApplication = header.ReceivingApplication ?? defaults.ReceivingApplication,
            ReceivingFacility = header.ReceivingFacility ?? defaults.ReceivingFacility,
            Version = header.Version ?? defaults.Version,
            ProcessingId = header.ProcessingId ?? defaults.ProcessingId,
        };
        var controlIds = new ControlIdSource(clock);
        var factory = new Hl7MessageFactory(settings, controlIds, clock);

        var sends = new List<PlannedSend>();
        var pendingWait = TimeSpan.Zero;
        BuiltMessage? lastBuilt = null;

        void Expand(List<StepFile> steps, string prefix, int? loopPatient)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var where = $"step {prefix}{i + 1}";
                var kinds = (step.Send is null ? 0 : 1) + (step.Wait is null ? 0 : 1) + (step.ForEachPatient is null ? 0 : 1);
                if (kinds != 1)
                {
                    errors.Add($"{where}: needs exactly one of send, wait or forEachPatient.");
                    continue;
                }

                if (step.Wait is not null)
                {
                    pendingWait += Duration(step.Wait, where);
                    continue;
                }

                if (step.ForEachPatient is not null)
                {
                    if (loopPatient is not null)
                        errors.Add($"{where}: forEachPatient cannot be nested.");
                    else if (file.Patients == 0)
                        errors.Add($"{where}: forEachPatient needs patients set above 0.");
                    else
                    {
                        for (var p = 1; p <= file.Patients; p++)
                        {
                            var errorsBefore = errors.Count;
                            Expand(step.ForEachPatient, $"{prefix}{i + 1}.", p);
                            if (errors.Count > errorsBefore)
                                break; // Same problem would repeat for every patient.
                        }
                    }
                    continue;
                }

                var send = PlanSend(step, where, loopPatient);
                if (send is null)
                    continue;
                var delay = step.Delay is not null ? Duration(step.Delay, where) : sends.Count == 0 ? TimeSpan.Zero : spacing;
                var build = send.Value.Build;
                sends.Add(new PlannedSend(sends.Count + 1, send.Value.Label, delay + pendingWait, send.Value.Expect, send.Value.Framing,
                    () => lastBuilt = build()));
                pendingWait = TimeSpan.Zero;
            }
        }

        (string Label, string Expect, MllpFraming Framing, Func<BuiltMessage> Build)? PlanSend(StepFile step, string where, int? loopPatient)
        {
            var errorsBefore = errors.Count;
            var kind = step.Send!.Trim().ToLowerInvariant();
            var expect = (step.Expect ?? "AA").Trim().ToUpperInvariant();
            if (!Expectations.Contains(expect))
                errors.Add($"{where}: expect '{step.Expect}' must be AA, AE, AR, any or none.");

            var framing = MllpFraming.Normal;
            if (step.Framing is not null && !Framings.TryGetValue(step.Framing.Trim().ToLowerInvariant(), out framing))
                errors.Add($"{where}: framing '{step.Framing}' must be one of {string.Join(", ", Framings.Keys)}.");

            // resend and raw are not about a patient; everything else is.
            var p = step.Patient ?? loopPatient ?? 0;
            if (kind is "resend" or "raw")
            {
                if (step.Patient is not null)
                    errors.Add($"{where}: {kind} does not use patient.");
            }
            else if (step.Patient is null && loopPatient is null)
                errors.Add($"{where}: {kind} needs a patient number (or put it inside forEachPatient).");
            else if (!patients.ContainsKey(p))
                errors.Add($"{where}: patient {p} does not exist.");

            void OnlyFor(string field, object? value, string allowedKind)
            {
                if (value is not null && kind != allowedKind)
                    errors.Add($"{where}: {field} only applies to {allowedKind}.");
            }
            OnlyFor("merged", step.Merged, "adt-a40");
            OnlyFor("status", step.Status, "orm-status");
            OnlyFor("minutes", step.Minutes, "siu-s12");
            OnlyFor("text", step.Text, "raw");
            if (kind == "resend" && step.ControlId is not null)
                errors.Add($"{where}: resend sends the previous message unchanged, so it cannot set controlId.");

            var id = step.ControlId;
            var previous = sends.Count;
            Func<BuiltMessage>? build = kind switch
            {
                "adt-a04" => () => factory.AdtA04(patients[p], id),
                "adt-a08" => () =>
                {
                    // An update changes the patient's address and phone; later messages use the new values.
                    var moved = generator.NextPatient();
                    patients[p] = patients[p] with { Address = moved.Address, Phone = moved.Phone };
                    return factory.AdtA08(patients[p], id);
                },
                "adt-a40" => () => factory.AdtA40(patients[p], patients[step.Merged!.Value], id),
                "orm-new" => () => factory.OrmO01(patients[p], orders[p], OrderControl.New, controlId: id),
                "orm-cancel" => () => factory.OrmO01(patients[p], orders[p], OrderControl.Cancel, controlId: id),
                "orm-status" => () => factory.OrmO01(patients[p], orders[p], OrderControl.StatusChange, step.Status, id),
                "siu-s12" => () => factory.SiuS12(patients[p], orders[p], step.Minutes ?? 30, id),
                "resend" => () => lastBuilt!,
                "raw" => () => Raw(step.Text!, id),
                _ => null,
            };
            if (build is null)
                errors.Add($"{where}: unknown send '{step.Send}'. Use adt-a04, adt-a08, adt-a40, orm-new, orm-cancel, orm-status, siu-s12, resend or raw.");

            var label = kind is "resend" or "raw" ? kind : $"{kind} patient {p}";
            switch (kind)
            {
                case "adt-a40" when step.Merged is null:
                    errors.Add($"{where}: adt-a40 needs merged (the patient number merged into patient {p}).");
                    break;
                case "adt-a40" when !patients.ContainsKey(step.Merged.Value) || step.Merged == p:
                    errors.Add($"{where}: merged patient {step.Merged} does not exist or is the same as patient {p}.");
                    break;
                case "adt-a40":
                    label += $" <- {step.Merged}";
                    break;
                case "orm-status" when string.IsNullOrWhiteSpace(step.Status):
                    errors.Add($"{where}: orm-status needs status, e.g. IP.");
                    break;
                case "orm-status":
                    label += $" {step.Status}";
                    break;
                case "resend" when previous == 0:
                    errors.Add($"{where}: resend needs a message before it.");
                    break;
                case "resend":
                    label += $" of #{previous}";
                    break;
                case "raw" when string.IsNullOrWhiteSpace(step.Text):
                    errors.Add($"{where}: raw needs text.");
                    break;
            }

            label = step.Label ?? label;
            if (framing != MllpFraming.Normal)
                label += $" [{step.Framing!.Trim().ToLowerInvariant()}]";
            return errors.Count > errorsBefore ? null : (label, expect, framing, build!);
        }

        BuiltMessage Raw(string text, string? fixedId)
        {
            if (text.Contains("{controlId}"))
                text = text.Replace("{controlId}", fixedId ?? controlIds.Next());
            text = text.Replace("{now}", clock.GetLocalNow().ToString("yyyyMMddHHmmss"));
            var wire = Hl7Text.ToWire(text);
            return new BuiltMessage(Hl7Parsed.TryParse(wire)?.ControlId ?? "", "raw", wire);
        }

        Expand(file.Steps, "", null);
        if (sends.Count == 0 && errors.Count == 0)
            errors.Add("steps: nothing to send.");
        if (errors.Count > 0)
            throw new ScenarioException($"{file.Name} has {errors.Count} problem(s):{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", errors));

        return new ScenarioPlan(file.Name, file.Description, ackTimeout, sends);
    }
}
