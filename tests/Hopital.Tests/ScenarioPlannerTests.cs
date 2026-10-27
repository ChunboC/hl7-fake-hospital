using Hopital.Hl7;
using Hopital.Scenarios;

namespace Hopital.Tests;

public class ScenarioPlannerTests
{
    private static ScenarioPlan Plan(string yaml) => ScenarioPlanner.Build(ScenarioLoader.Parse(yaml), TimeProvider.System);

    private static string Problems(string yaml) => Assert.Throws<ScenarioException>(() => Plan(yaml)).Message;

    [Theory]
    [InlineData("250ms", 250)]
    [InlineData("2s", 2000)]
    [InlineData("1.5m", 90000)]
    [InlineData("0", 0)]
    public void Durations_parse(string text, int milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), Durations.Parse(text));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1s")]
    [InlineData("soon")]
    public void Bad_durations_are_rejected(string text)
    {
        Assert.Throws<ScenarioException>(() => Durations.Parse(text));
    }

    [Theory]
    [InlineData("smoke-test.yaml", 11)]
    [InlineData("clinic-day.yaml", 64)]
    [InlineData("duplicate-message.yaml", 6)]
    [InlineData("cancel-after-start.yaml", 5)]
    [InlineData("merge.yaml", 6)]
    [InlineData("malformed.yaml", 12)]
    [InlineData("burst-500.yaml", 500)]
    [InlineData("schedule-only-day.yaml", 20)]
    public void The_scenario_files_are_valid(string name, int messages)
    {
        var plan = ScenarioPlanner.Build(ScenarioLoader.Load(Path.Combine(TestPaths.Scenarios, name)), TimeProvider.System);

        Assert.Equal(messages, plan.Sends.Count);
        Assert.All(plan.Sends, send => Assert.NotNull(send.Build()));
    }

    [Fact]
    public void Every_scenario_file_is_covered_by_the_test_above()
    {
        var tested = typeof(ScenarioPlannerTests).GetMethod(nameof(The_scenario_files_are_valid))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Select(a => (string)((InlineDataAttribute)a).GetData(null!).First()[0])
            .Order();

        Assert.Equal(Directory.GetFiles(TestPaths.Scenarios, "*.yaml").Select(Path.GetFileName).Order(), tested);
    }

    [Fact]
    public void Resend_repeats_the_previous_message_byte_for_byte()
    {
        var plan = Plan("""
            patients: 1
            steps:
              - send: orm-new
                patient: 1
              - send: resend
                delay: 1s
            """);

        var first = plan.Sends[0].Build();
        Thread.Sleep(1100); // MSH-7 would change if the resend were rebuilt.
        var again = plan.Sends[1].Build();

        Assert.Equal(first.Text, again.Text);
        Assert.Equal("resend of #1", plan.Sends[1].Label);
    }

    [Fact]
    public void Raw_fills_in_placeholders_and_keeps_its_control_id_for_ACK_matching()
    {
        var plan = Plan("""
            steps:
              - send: raw
                label: odd one
                text: |
                  MSH|^~\&|EPIC|HOPITAL|OIE|IMAGING|{now}||ADT^A04|{controlId}|P|2.5.1
                  PID|1
              - send: raw
                text: no header here
                framing: none
                expect: none
            """);

        var message = plan.Sends[0].Build();
        var hl7 = Hl7Parsed.TryParse(message.Text)!;
        Assert.Equal("odd one", plan.Sends[0].Label);
        Assert.Equal(20, message.ControlId.Length);
        Assert.Equal(message.ControlId, hl7.ControlId);
        Assert.Matches(@"^\d{14}$", hl7.Field("MSH", 7));
        Assert.Equal("MSH|^~\\&|EPIC|HOPITAL|OIE|IMAGING|" + hl7.Field("MSH", 7) + "||ADT^A04|" + message.ControlId + "|P|2.5.1\rPID|1\r", message.Text);

        Assert.Equal("", plan.Sends[1].Build().ControlId);
        Assert.Equal(Mllp.MllpFraming.None, plan.Sends[1].Framing);
        Assert.Equal("raw [none]", plan.Sends[1].Label);
        Assert.Equal("NONE", plan.Sends[1].Expect);
    }

    [Fact]
    public void Resend_and_raw_problems_are_reported()
    {
        var message = Problems("""
            patients: 1
            steps:
              - send: resend
              - send: raw
              - send: raw
                text: x
                patient: 1
              - send: resend
                controlId: X
              - send: adt-a04
                patient: 1
                text: x
                framing: wobbly
            """);

        Assert.Contains("step 1: resend needs a message before it", message);
        Assert.Contains("step 2: raw needs text", message);
        Assert.Contains("step 3: raw does not use patient", message);
        Assert.Contains("step 4: resend sends the previous message unchanged", message);
        Assert.Contains("step 5: text only applies to raw", message);
        Assert.Contains("step 5: framing 'wobbly'", message);
    }

    [Fact]
    public void ForEachPatient_expands_in_patient_order_with_spacing_and_delays()
    {
        var plan = Plan("""
            patients: 3
            spacing: 2s
            steps:
              - forEachPatient:
                  - send: adt-a04
                  - send: orm-new
                    delay: 100ms
              - wait: 5s
              - send: orm-cancel
                patient: 2
            """);

        Assert.Equal(
            ["adt-a04 patient 1", "orm-new patient 1", "adt-a04 patient 2", "orm-new patient 2", "adt-a04 patient 3", "orm-new patient 3", "orm-cancel patient 2"],
            plan.Sends.Select(s => s.Label));
        Assert.Equal(
            [0, 100, 2000, 100, 2000, 100, 7000],
            plan.Sends.Select(s => (int)s.DelayBefore.TotalMilliseconds));
        Assert.Equal(Enumerable.Range(1, 7), plan.Sends.Select(s => s.Number));
    }

    [Fact]
    public void Same_patient_and_order_are_used_across_steps()
    {
        var plan = Plan("""
            patients: 2
            steps:
              - send: orm-new
                patient: 2
              - send: orm-cancel
                patient: 2
            """);

        var placed = Hl7Parsed.TryParse(plan.Sends[0].Build().Text)!;
        var cancelled = Hl7Parsed.TryParse(plan.Sends[1].Build().Text)!;
        Assert.Equal(placed.Field("PID", 3), cancelled.Field("PID", 3));
        Assert.Equal(placed.Field("ORC", 2), cancelled.Field("ORC", 2));
        Assert.Equal("CA", cancelled.Field("ORC", 1));
    }

    [Fact]
    public void Duplicates_are_the_same_person_with_new_ids_and_merge_into_the_original()
    {
        var plan = Plan("""
            patients: 2
            duplicates: { 3: 1 }
            steps:
              - send: adt-a04
                patient: 1
              - send: adt-a04
                patient: 3
              - send: adt-a40
                patient: 1
                merged: 3
            """);

        var original = Hl7Parsed.TryParse(plan.Sends[0].Build().Text)!;
        var duplicate = Hl7Parsed.TryParse(plan.Sends[1].Build().Text)!;
        var merge = Hl7Parsed.TryParse(plan.Sends[2].Build().Text)!;

        Assert.Equal(original.Field("PID", 5), duplicate.Field("PID", 5));
        Assert.Equal(original.Field("PID", 7), duplicate.Field("PID", 7));
        Assert.NotEqual(original.Field("PID", 3), duplicate.Field("PID", 3));
        Assert.Equal(original.Field("PID", 3), merge.Field("PID", 3));
        Assert.Equal(duplicate.Field("PID", 3), merge.Field("MRG", 1));
        Assert.Equal("adt-a40 patient 1 <- 3", plan.Sends[2].Label);
    }

    [Fact]
    public void A08_changes_address_and_phone_for_later_messages()
    {
        var plan = Plan("""
            patients: 1
            steps:
              - send: adt-a04
                patient: 1
              - send: adt-a08
                patient: 1
              - send: orm-new
                patient: 1
            """);

        var before = Hl7Parsed.TryParse(plan.Sends[0].Build().Text)!;
        var update = Hl7Parsed.TryParse(plan.Sends[1].Build().Text)!;
        var after = Hl7Parsed.TryParse(plan.Sends[2].Build().Text)!;

        Assert.NotEqual(before.Field("PID", 11), update.Field("PID", 11));
        Assert.Equal(update.Field("PID", 11), after.Field("PID", 11));
        Assert.Equal(before.Field("PID", 5), update.Field("PID", 5));
    }

    [Fact]
    public void Control_id_override_header_and_expect_are_applied()
    {
        var plan = Plan("""
            patients: 1
            header: { version: "2.3", processingId: T }
            steps:
              - send: adt-a04
                patient: 1
                controlId: DUP-0001
              - send: adt-a04
                patient: 1
                controlId: DUP-0001
                expect: any
            """);

        var second = Hl7Parsed.TryParse(plan.Sends[1].Build().Text)!;
        Assert.Equal("DUP-0001", second.ControlId);
        Assert.Equal("2.3", second.Version);
        Assert.Equal("T", second.Field("MSH", 11));
        Assert.Equal("AA", plan.Sends[0].Expect);
        Assert.Equal("ANY", plan.Sends[1].Expect);
    }

    [Fact]
    public void Problems_are_reported_together_with_step_numbers()
    {
        var message = Problems("""
            patients: 2
            spacing: fast
            steps:
              - send: adt-a99
                patient: 1
              - send: adt-a04
              - send: orm-status
                patient: 1
              - send: adt-a40
                patient: 1
              - send: adt-a04
                patient: 7
                expect: maybe
              - forEachPatient:
                  - send: adt-a04
                    status: IP
              - send: adt-a04
                wait: 1s
            """);

        Assert.Contains("spacing:", message);
        Assert.Contains("step 1: unknown send 'adt-a99'", message);
        Assert.Contains("step 2: adt-a04 needs a patient number", message);
        Assert.Contains("step 3: orm-status needs status", message);
        Assert.Contains("step 4: adt-a40 needs merged", message);
        Assert.Contains("step 5: expect 'maybe'", message);
        Assert.Contains("step 5: patient 7 does not exist", message);
        Assert.Contains("step 6.1: status only applies to orm-status", message);
        Assert.Contains("step 7: needs exactly one of", message);
        // The forEachPatient problem is reported once, not once per patient.
        Assert.Single(message.Split('\n'), line => line.Contains("step 6.1"));
    }

    [Fact]
    public void Unknown_keys_are_errors_with_a_line_number()
    {
        var message = Problems("""
            patients: 1
            steps:
              - send: adt-a04
                patiant: 1
            """);

        Assert.Contains("line 4", message);
    }

    [Fact]
    public void Bad_duplicates_are_reported()
    {
        Assert.Contains("duplicates: 1 is already a patient", Problems("""
            patients: 2
            duplicates: { 1: 2 }
            steps: [ { send: adt-a04, patient: 1 } ]
            """));
        Assert.Contains("duplicates: 4 is a duplicate of 9", Problems("""
            patients: 2
            duplicates: { 4: 9 }
            steps: [ { send: adt-a04, patient: 1 } ]
            """));
    }
}
