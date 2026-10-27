using Hopital.Generation;
using static Hopital.Hl7.Hl7Escape;

namespace Hopital.Hl7;

/// <param name="ControlId">MSH-10.</param>
/// <param name="Type">MSH-9, e.g. ADT^A04^ADT_A01.</param>
/// <param name="Text">Wire format: segments separated by 0x0D, ending with 0x0D.</param>
public sealed record BuiltMessage(string ControlId, string Type, string Text);

/// <summary>ORC-1 order control codes used by the fake Epic sender.</summary>
public enum OrderControl
{
    /// <summary>NW: new order.</summary>
    New,

    /// <summary>CA: cancel order.</summary>
    Cancel,

    /// <summary>SC: status changed (ORC-5 holds the new status, e.g. IP for in progress).</summary>
    StatusChange,
}

/// <summary>
/// Builds the HL7 v2 messages Epic sends to an imaging or procedure system:
/// ADT A04/A08/A40, ORM O01 and SIU S12. Segments are written by hand so scenarios
/// keep byte-level control. Every builder takes an optional control ID, so a scenario
/// can send the same MSH-10 twice on purpose.
/// </summary>
public sealed class Hl7MessageFactory(Hl7Settings settings, ControlIdSource controlIds, TimeProvider clock)
{
    private string Facility => settings.SendingFacility;

    /// <summary>A04: register an outpatient.</summary>
    public BuiltMessage AdtA04(FakePatient patient, string? controlId = null) =>
        Build("ADT^A04^ADT_A01", controlId, now =>
        [
            Evn("A04", now),
            Pid(patient),
            Pv1(patient, room: null, attending: null),
        ]);

    /// <summary>A08: update patient information.</summary>
    public BuiltMessage AdtA08(FakePatient patient, string? controlId = null) =>
        Build("ADT^A08^ADT_A01", controlId, now =>
        [
            Evn("A08", now),
            Pid(patient),
            Pv1(patient, room: null, attending: null),
        ]);

    /// <summary>A40: merge <paramref name="merged"/> into <paramref name="survivor"/> (patient identifier list).</summary>
    public BuiltMessage AdtA40(FakePatient survivor, FakePatient merged, string? controlId = null) =>
        Build("ADT^A40^ADT_A39", controlId, now =>
        [
            Evn("A40", now),
            Pid(survivor),
            Segment("MRG",
                (1, Components(merged.Mrn, "", "", Facility, "MR")),
                (3, Components(merged.AccountNumber, "", "", Facility, "AN"))),
        ]);

    /// <summary>
    /// O01 order message. New orders get ORC-5 = SC (scheduled), cancels get CA;
    /// a status change needs <paramref name="status"/> (e.g. IP when the procedure has started).
    /// </summary>
    public BuiltMessage OrmO01(FakePatient patient, FakeOrder order, OrderControl control, string? status = null, string? controlId = null)
    {
        var (code, defaultStatus) = control switch
        {
            OrderControl.New => ("NW", "SC"),
            OrderControl.Cancel => ("CA", "CA"),
            OrderControl.StatusChange => ("SC", null),
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };
        var orderStatus = status ?? defaultStatus
            ?? throw new ArgumentException("A status change needs a status, e.g. IP.", nameof(status));

        return Build("ORM^O01^ORM_O01", controlId, now =>
        [
            Pid(patient),
            Pv1(patient, order.Room, order.OrderingProvider),
            Segment("ORC",
                (1, code),
                (2, Components(order.PlacerOrderNumber, settings.SendingApplication)),
                (3, Components(order.AccessionNumber, Facility)),
                (5, orderStatus),
                (7, Components("", "", "", Hl7Time(order.ScheduledAt))),
                (9, Hl7Time(now)),
                (12, Provider(order.OrderingProvider))),
            Segment("OBR",
                (1, "1"),
                (2, Components(order.PlacerOrderNumber, settings.SendingApplication)),
                (3, Components(order.AccessionNumber, Facility)),
                (4, Components(order.Procedure.Code, order.Procedure.Description, "L")),
                (16, Provider(order.OrderingProvider)),
                (18, Escape(order.AccessionNumber)),
                (27, Components("", "", "", Hl7Time(order.ScheduledAt))),
                (36, Hl7Time(order.ScheduledAt))),
        ]);
    }

    /// <summary>S12: new appointment booked, for sites that send scheduling instead of (or as well as) orders.</summary>
    public BuiltMessage SiuS12(FakePatient patient, FakeOrder order, int durationMinutes = 30, string? controlId = null)
    {
        var start = Hl7Time(order.ScheduledAt);
        var end = Hl7Time(order.ScheduledAt.AddMinutes(durationMinutes));
        var duration = durationMinutes.ToString();

        return Build("SIU^S12^SIU_S12", controlId, _ =>
        [
            Segment("SCH",
                (1, Components(order.AppointmentId, settings.SendingApplication)),
                (2, Components(order.AppointmentId, Facility)),
                (6, Components("NEW", "New appointment", "HL70276")),
                (7, Components(order.Procedure.Code, order.Procedure.Description, "L")),
                (9, duration),
                (10, "MIN"),
                (11, Components("", "", duration, start, end)),
                (25, "Booked"),
                (26, Components(order.PlacerOrderNumber, settings.SendingApplication)),
                (27, Components(order.AccessionNumber, Facility))),
            Pid(patient),
            Pv1(patient, order.Room, order.OrderingProvider),
            Segment("RGS", (1, "1"), (2, "A")),
            Segment("AIS",
                (1, "1"),
                (2, "A"),
                (3, Components(order.Procedure.Code, order.Procedure.Description, "L")),
                (4, start),
                (7, duration),
                (8, "MIN")),
            Segment("AIL", (1, "1"), (2, "A"), (3, Components("ENDO", order.Room, "", Facility))),
            Segment("AIP", (1, "1"), (2, "A"), (3, Provider(order.OrderingProvider)), (4, "ATTENDING")),
        ]);
    }

    private BuiltMessage Build(string type, string? controlId, Func<DateTime, string[]> body)
    {
        var now = clock.GetLocalNow().DateTime;
        var id = controlId ?? controlIds.Next();
        var msh = string.Join('|',
            "MSH", @"^~\&",
            Escape(settings.SendingApplication), Escape(settings.SendingFacility),
            Escape(settings.ReceivingApplication), Escape(settings.ReceivingFacility),
            Hl7Time(now), "", type, Escape(id), settings.ProcessingId, settings.Version);
        var segments = body(now).Prepend(msh);
        return new BuiltMessage(id, type, string.Join('\r', segments) + '\r');
    }

    private static string Evn(string trigger, DateTime now) => Segment("EVN", (1, trigger), (2, Hl7Time(now)));

    private string Pid(FakePatient p) =>
        Segment("PID",
            (1, "1"),
            (3, Components(p.Mrn, "", "", Facility, "MR")),
            (5, Components(p.LastName, p.FirstName, p.MiddleInitial)),
            (7, p.DateOfBirth.ToString("yyyyMMdd")),
            (8, p.Sex.ToString()),
            (11, Components(p.Address.Street, "", p.Address.City, p.Address.State, p.Address.Zip, "USA")),
            (13, Components(p.Phone, "PRN", "PH")),
            (18, Components(p.AccountNumber, "", "", Facility, "AN")));

    private string Pv1(FakePatient p, string? room, FakeProvider? attending) =>
        Segment("PV1",
            (1, "1"),
            (2, "O"),
            (3, Components("ENDO", room, "", Facility)),
            (7, attending is null ? "" : Provider(attending)),
            (19, Components(p.AccountNumber)));

    private static string Provider(FakeProvider d) => Components(d.Id, d.LastName, d.FirstName);

    private static string Hl7Time(DateTime time) => time.ToString("yyyyMMddHHmmss");

    /// <summary>Writes a segment from (field number, already-escaped value) pairs; gaps become empty fields.</summary>
    private static string Segment(string id, params (int Number, string Value)[] fields)
    {
        var values = new string[fields.Max(f => f.Number) + 1];
        Array.Fill(values, "");
        values[0] = id;
        foreach (var (number, value) in fields)
            values[number] = value;
        return string.Join('|', values);
    }
}
