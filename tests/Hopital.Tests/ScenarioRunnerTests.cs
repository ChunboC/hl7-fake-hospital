using System.Net;
using Hopital.Hl7;
using Hopital.Mllp;
using Hopital.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hopital.Tests;

public class ScenarioRunnerTests
{
    private const string FivePatients = """
        patients: 5
        ackTimeout: 300ms
        steps:
          - forEachPatient:
              - send: adt-a04
        """;

    /// <summary>Runs a scenario against a loopback listener whose reply is decided per message (1-based).</summary>
    private static async Task<RunSummary> RunAgainst(string yaml, Func<int, string, string?> reply)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var count = 0;
        var listener = new MllpListener(IPAddress.Loopback, 0, (message, _, _) =>
        {
            var text = Hl7Text.Encoding.GetString(message.Payload);
            var answer = reply(Interlocked.Increment(ref count), text);
            return Task.FromResult(answer is null ? MllpReply.None : MllpReply.Send(Hl7Text.Encoding.GetBytes(answer)));
        }, NullLogger.Instance);
        listener.Start();
        var listening = listener.RunAsync(cts.Token);

        var plan = ScenarioPlanner.Build(ScenarioLoader.Parse(yaml), TimeProvider.System);
        var summary = await new ScenarioRunner("127.0.0.1", listener.Port, NullLogger.Instance, TimeProvider.System).RunAsync(plan, cts.Token);

        cts.Cancel();
        await listening;
        return summary;
    }

    [Fact]
    public async Task All_accepted_passes()
    {
        var summary = await RunAgainst(FivePatients, (_, text) => AckBuilder.For(text));

        Assert.Equal(5, summary.Results.Count);
        Assert.All(summary.Results, r => Assert.Equal(SendOutcome.Accepted, r.Outcome));
        Assert.Equal(0, summary.Failed);
        Assert.Equal(5, summary.Results.Select(r => r.ControlId).Distinct().Count());
    }

    [Fact]
    public async Task Error_and_reject_are_recorded_with_their_text()
    {
        var summary = await RunAgainst(FivePatients, (n, text) => n switch
        {
            2 => AckBuilder.For(text, "AE", "Database busy"),
            4 => AckBuilder.For(text, "AR", "Not allowed"),
            _ => AckBuilder.For(text),
        });

        Assert.Equal(
            [SendOutcome.Accepted, SendOutcome.Error, SendOutcome.Accepted, SendOutcome.Rejected, SendOutcome.Accepted],
            summary.Results.Select(r => r.Outcome));
        Assert.Contains("Database busy", summary.Results[1].Detail);
        Assert.Equal(2, summary.Failed);
    }

    [Fact]
    public async Task A_timeout_is_recorded_and_the_run_continues_on_a_new_connection()
    {
        var summary = await RunAgainst(FivePatients, (n, text) => n == 3 ? null : AckBuilder.For(text));

        Assert.Equal(SendOutcome.Timeout, summary.Results[2].Outcome);
        Assert.Equal(SendOutcome.Accepted, summary.Results[3].Outcome);
        Assert.Equal(SendOutcome.Accepted, summary.Results[4].Outcome);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task An_ACK_for_a_different_control_id_fails()
    {
        var summary = await RunAgainst(FivePatients, (n, text) =>
            n == 1 ? AckBuilder.For(text).Replace(Hl7Parsed.TryParse(text)!.ControlId, "SOMETHINGELSE") : AckBuilder.For(text));

        Assert.Equal(SendOutcome.Accepted, summary.Results[0].Outcome);
        Assert.False(summary.Results[0].Passed);
        Assert.Contains("SOMETHINGELSE", summary.Results[0].Detail);
    }

    [Fact]
    public async Task Expect_lets_an_error_pass()
    {
        var summary = await RunAgainst("""
            patients: 1
            steps:
              - send: adt-a04
                patient: 1
                expect: AE
              - send: adt-a04
                patient: 1
                expect: any
            """, (_, text) => AckBuilder.For(text, "AE", "Duplicate"));

        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task Broken_framing_gets_the_expected_result_and_the_next_message_still_works()
    {
        var summary = await RunAgainst("""
            patients: 1
            ackTimeout: 300ms
            steps:
              - send: adt-a04
                patient: 1
                framing: no-trailing-cr
              - send: adt-a04
                patient: 1
                framing: junk-before
              - send: adt-a04
                patient: 1
                framing: none
                expect: none
              - send: adt-a04
                patient: 1
                framing: no-end-block
                expect: none
              - send: adt-a04
                patient: 1
            """, (_, text) => AckBuilder.For(text));

        Assert.Equal(
            [SendOutcome.Accepted, SendOutcome.Accepted, SendOutcome.Timeout, SendOutcome.Timeout, SendOutcome.Accepted],
            summary.Results.Select(r => r.Outcome));
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task Resend_goes_out_identical_and_gets_its_own_ACK()
    {
        var received = new List<string>();
        var summary = await RunAgainst("""
            patients: 1
            steps:
              - send: orm-new
                patient: 1
              - send: resend
            """, (_, text) =>
            {
                lock (received)
                    received.Add(text);
                return AckBuilder.For(text);
            });

        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, received.Count);
        Assert.Equal(received[0], received[1]);
        Assert.Equal(summary.Results[0].ControlId, summary.Results[1].ControlId);
    }

    [Fact]
    public async Task Each_result_is_reported_as_it_happens()
    {
        var reported = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new MllpListener(IPAddress.Loopback, 0, (message, _, _) =>
            Task.FromResult(MllpReply.Send(Hl7Text.Encoding.GetBytes(AckBuilder.For(Hl7Text.Encoding.GetString(message.Payload))))),
            NullLogger.Instance);
        listener.Start();
        var listening = listener.RunAsync(cts.Token);

        var plan = ScenarioPlanner.Build(ScenarioLoader.Parse(FivePatients), TimeProvider.System);
        var summary = await new ScenarioRunner("127.0.0.1", listener.Port, NullLogger.Instance, TimeProvider.System)
            .RunAsync(plan, cts.Token, result => reported.Add(result.Send.Number));

        cts.Cancel();
        await listening;
        Assert.Equal([1, 2, 3, 4, 5], reported);
        Assert.False(summary.Stopped);
        Assert.Equal(5, summary.Planned);
    }

    [Fact]
    public async Task Stopping_returns_what_was_sent_so_far()
    {
        using var stop = new CancellationTokenSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new MllpListener(IPAddress.Loopback, 0, (message, _, _) =>
            Task.FromResult(MllpReply.Send(Hl7Text.Encoding.GetBytes(AckBuilder.For(Hl7Text.Encoding.GetString(message.Payload))))),
            NullLogger.Instance);
        listener.Start();
        var listening = listener.RunAsync(cts.Token);

        // A long pause after message 2, so the stop lands while the runner is waiting.
        var plan = ScenarioPlanner.Build(ScenarioLoader.Parse(FivePatients.Replace("ackTimeout: 300ms", "ackTimeout: 300ms\nspacing: 10s")), TimeProvider.System);
        var summary = await new ScenarioRunner("127.0.0.1", listener.Port, NullLogger.Instance, TimeProvider.System)
            .RunAsync(plan, stop.Token, result =>
            {
                if (result.Send.Number == 1)
                    stop.CancelAfter(200);
            });

        cts.Cancel();
        await listening;
        Assert.True(summary.Stopped);
        Assert.Single(summary.Results);
        Assert.Contains("(stopped after 1 of 5)", summary.Describe().First());
    }

    [Fact]
    public async Task Nothing_listening_is_a_connect_failure_for_each_message()
    {
        // Two messages only: on Windows each refused connection takes about 2 s.
        var plan = ScenarioPlanner.Build(ScenarioLoader.Parse(FivePatients.Replace("patients: 5", "patients: 2")), TimeProvider.System);
        var freePort = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        freePort.Start();
        var port = ((IPEndPoint)freePort.LocalEndpoint).Port;
        freePort.Stop();

        var summary = await new ScenarioRunner("127.0.0.1", port, NullLogger.Instance, TimeProvider.System).RunAsync(plan, CancellationToken.None);

        Assert.All(summary.Results, r => Assert.Equal(SendOutcome.ConnectFailed, r.Outcome));
        Assert.Equal(2, summary.Failed);
    }
}
