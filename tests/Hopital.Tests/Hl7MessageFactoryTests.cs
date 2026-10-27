using Hopital.Generation;
using Hopital.Hl7;

namespace Hopital.Tests;

public class Hl7MessageFactoryTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 14, 30, 5);
    private static readonly FixedClock Clock = new(Now);

    private readonly FakePatient _patient;
    private readonly FakePatient _other;
    private readonly FakeOrder _order;

    public Hl7MessageFactoryTests()
    {
        var generator = new FakeDataGenerator(11, DateOnly.FromDateTime(Now));
        _patient = generator.NextPatient();
        _other = generator.NextPatient();
        _order = generator.NextOrder(Now.Date.AddHours(9));
    }

    private static Hl7MessageFactory Factory(Hl7Settings? settings = null) =>
        new(settings ?? new Hl7Settings(), new ControlIdSource(Clock), Clock);

    private static Hl7Parsed Parse(BuiltMessage message) => Hl7Parsed.TryParse(message.Text)!;

    private static string[] SegmentIds(BuiltMessage message) => Parse(message).Segments.Select(s => s[..3]).ToArray();

    [Fact]
    public void Header_has_settings_time_type_control_id_and_version()
    {
        var message = Factory().AdtA04(_patient);
        var hl7 = Parse(message);

        Assert.StartsWith(@"MSH|^~\&|EPIC|HOPITAL|OIE|IMAGING|20261025143005||ADT^A04^ADT_A01|", message.Text);
        Assert.Equal(message.ControlId, hl7.ControlId);
        Assert.Equal("P", hl7.Field("MSH", 11));
        Assert.Equal("2.5.1", hl7.Version);
        Assert.EndsWith("\r", message.Text);
        Assert.DoesNotContain("\n", message.Text);
    }

    [Fact]
    public void Version_comes_from_settings()
    {
        Assert.Equal("2.3", Parse(Factory(new Hl7Settings { Version = "2.3" }).AdtA04(_patient)).Version);
    }

    [Fact]
    public void Control_ids_are_unique_and_fit_in_20_characters_unless_one_is_given()
    {
        var factory = Factory();
        var ids = Enumerable.Range(0, 100).Select(_ => factory.AdtA08(_patient).ControlId).ToList();

        Assert.Equal(100, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(20, id.Length));
        Assert.Equal("SAME0001", factory.AdtA08(_patient, controlId: "SAME0001").ControlId);
    }

    [Fact]
    public void A04_and_A08_carry_patient_demographics()
    {
        foreach (var message in new[] { Factory().AdtA04(_patient), Factory().AdtA08(_patient) })
        {
            var hl7 = Parse(message);
            Assert.Equal(["MSH", "EVN", "PID", "PV1"], SegmentIds(message));
            Assert.Equal($"{_patient.Mrn}^^^HOPITAL^MR", hl7.Field("PID", 3));
            Assert.Equal($"{_patient.LastName}^{_patient.FirstName}^{_patient.MiddleInitial}", hl7.Field("PID", 5));
            Assert.Equal(_patient.DateOfBirth.ToString("yyyyMMdd"), hl7.Field("PID", 7));
            Assert.Equal(_patient.Sex.ToString(), hl7.Field("PID", 8));
            Assert.Equal(_patient.AccountNumber, hl7.Component("PID", 18, 1));
            Assert.Equal(hl7.Component("MSH", 9, 2), hl7.Field("EVN", 1));
        }
    }

    [Fact]
    public void A40_puts_the_merged_mrn_in_MRG()
    {
        var hl7 = Parse(Factory().AdtA40(_patient, _other));

        Assert.Equal("ADT^A40^ADT_A39", hl7.MessageType);
        Assert.Equal(_patient.Mrn, hl7.Component("PID", 3, 1));
        Assert.Equal(_other.Mrn, hl7.Component("MRG", 1, 1));
        Assert.Equal(_other.AccountNumber, hl7.Component("MRG", 3, 1));
    }

    [Theory]
    [InlineData(OrderControl.New, null, "NW", "SC")]
    [InlineData(OrderControl.Cancel, null, "CA", "CA")]
    [InlineData(OrderControl.StatusChange, "IP", "SC", "IP")]
    public void ORM_sets_order_control_and_status(OrderControl control, string? status, string orc1, string orc5)
    {
        var message = Factory().OrmO01(_patient, _order, control, status);
        var hl7 = Parse(message);

        Assert.Equal(["MSH", "PID", "PV1", "ORC", "OBR"], SegmentIds(message));
        Assert.Equal(orc1, hl7.Field("ORC", 1));
        Assert.Equal(orc5, hl7.Field("ORC", 5));
        Assert.Equal(_order.PlacerOrderNumber, hl7.Component("ORC", 2, 1));
        Assert.Equal(_order.AccessionNumber, hl7.Component("ORC", 3, 1));
        Assert.Equal(_order.PlacerOrderNumber, hl7.Component("OBR", 2, 1));
        Assert.Equal(_order.Procedure.Code, hl7.Component("OBR", 4, 1));
        Assert.Equal(_order.AccessionNumber, hl7.Field("OBR", 18));
        Assert.Equal("20261025090000", hl7.Field("OBR", 36));
        Assert.Equal(_order.OrderingProvider.Id, hl7.Component("ORC", 12, 1));
    }

    [Fact]
    public void ORM_status_change_needs_a_status()
    {
        Assert.Throws<ArgumentException>(() => Factory().OrmO01(_patient, _order, OrderControl.StatusChange));
    }

    [Fact]
    public void SIU_S12_links_the_appointment_to_the_order()
    {
        var message = Factory().SiuS12(_patient, _order, durationMinutes: 45);
        var hl7 = Parse(message);

        Assert.Equal(["MSH", "SCH", "PID", "PV1", "RGS", "AIS", "AIL", "AIP"], SegmentIds(message));
        Assert.Equal(_order.AppointmentId, hl7.Component("SCH", 1, 1));
        Assert.Equal("^^45^20261025090000^20261025094500", hl7.Field("SCH", 11));
        Assert.Equal(_order.PlacerOrderNumber, hl7.Component("SCH", 26, 1));
        Assert.Equal(_order.AccessionNumber, hl7.Component("SCH", 27, 1));
        Assert.Equal(_order.Procedure.Code, hl7.Component("AIS", 3, 1));
        Assert.Equal(_order.Room, hl7.Component("AIL", 3, 2));
    }

    [Fact]
    public void Separators_in_values_are_escaped()
    {
        var odd = _patient with { LastName = "O'Neil|Smith^Jr", FirstName = @"A&B\C~D" };

        var hl7 = Parse(Factory().AdtA04(odd));

        Assert.Equal(@"O'Neil\F\Smith\S\Jr^A\T\B\E\C\R\D", hl7.Field("PID", 5)[..^2]);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(now, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
