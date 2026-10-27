using System.Net.Sockets;
using Hopital.Hl7;
using Hopital.Mllp;
using Microsoft.Extensions.Logging;

namespace Hopital.Scenarios;

public enum SendOutcome
{
    Accepted,
    Error,
    Rejected,

    /// <summary>A reply came back but it was not a readable ACK.</summary>
    Invalid,

    /// <summary>No ACK within the timeout.</summary>
    Timeout,

    /// <summary>The connection closed or broke before an ACK arrived.</summary>
    ConnectionLost,

    /// <summary>Could not connect at all.</summary>
    ConnectFailed,
}

/// <param name="Passed">The outcome matched the step's expectation and the ACK was for this message.</param>
public sealed record SendResult(PlannedSend Send, string ControlId, SendOutcome Outcome, string Detail, TimeSpan Elapsed, bool Passed);

/// <param name="Planned">How many messages the scenario has.</param>
/// <param name="Stopped">The run was cancelled before every message was sent.</param>
public sealed record RunSummary(string Name, IReadOnlyList<SendResult> Results, TimeSpan Duration, int Planned, bool Stopped)
{
    public int Failed => Results.Count(r => !r.Passed);

    public IEnumerable<string> Describe()
    {
        var stopped = Stopped ? $" (stopped after {Results.Count} of {Planned})" : "";
        yield return $"{Name}: {Results.Count} sent, {Results.Count - Failed} as expected, {Failed} not, in {Duration.TotalSeconds:0.0} s{stopped}";
        foreach (var group in Results.GroupBy(r => r.Outcome).OrderBy(g => g.Key))
            yield return $"  {group.Key,-15} {group.Count(),5}";

        var answered = Results.Where(r => r.Outcome is SendOutcome.Accepted or SendOutcome.Error or SendOutcome.Rejected).ToList();
        if (answered.Count > 0)
            yield return $"  ACK time: average {answered.Average(r => r.Elapsed.TotalMilliseconds):0} ms, slowest {answered.Max(r => r.Elapsed.TotalMilliseconds):0} ms";
        foreach (var r in Results.Where(r => !r.Passed).Take(10))
            yield return $"  Not as expected: #{r.Send.Number} {r.Send.Label} {r.ControlId}: {r.Outcome}, expected {r.Send.Expect}. {r.Detail}";
        if (Failed > 10)
            yield return $"  ... and {Failed - 10} more";
    }
}

/// <summary>
/// Sends a plan's messages in order over one MLLP connection, waiting for each ACK like Epic does.
/// A timeout or broken connection is recorded and the next message goes out on a new connection,
/// so one bad reply does not stop the run. Cancelling stops it and returns what was sent so far.
/// </summary>
public sealed class ScenarioRunner(string host, int port, ILogger logger, TimeProvider clock)
{
    /// <param name="onResult">Called after each message, e.g. to show progress live.</param>
    public async Task<RunSummary> RunAsync(ScenarioPlan plan, CancellationToken cancellationToken, Action<SendResult>? onResult = null)
    {
        var started = clock.GetTimestamp();
        var results = new List<SendResult>();
        MllpClient? client = null;
        var width = plan.Sends.Count.ToString().Length;
        var stopped = false;

        try
        {
            foreach (var send in plan.Sends)
            {
                if (send.DelayBefore > TimeSpan.Zero)
                    await Task.Delay(send.DelayBefore, clock, cancellationToken);

                var message = send.Build();
                var sentAt = clock.GetTimestamp();
                SendOutcome outcome;
                var detail = "";
                var idMatches = true;

                try
                {
                    client ??= await ConnectAsync(cancellationToken);
                    await client.SendRawAsync(MllpFrame.Wrap(Hl7Text.Encoding.GetBytes(message.Text), send.Framing), cancellationToken);
                    var reply = await client.ReceiveAsync(plan.AckTimeout, cancellationToken);
                    if (reply is null)
                    {
                        outcome = SendOutcome.ConnectionLost;
                        detail = "Connection closed without an ACK.";
                    }
                    else
                    {
                        var ack = AckResult.Parse(Hl7Text.Encoding.GetString(reply.Payload));
                        outcome = ack.Status switch
                        {
                            AckStatus.Accepted => SendOutcome.Accepted,
                            AckStatus.Error => SendOutcome.Error,
                            AckStatus.Rejected => SendOutcome.Rejected,
                            _ => SendOutcome.Invalid,
                        };
                        detail = string.Join(" ", new[] { ack.Code, ack.Text, ack.ErrorText }.Where(s => s != "").Distinct());
                        if (outcome != SendOutcome.Invalid && ack.AckedControlId != message.ControlId)
                        {
                            idMatches = false;
                            detail += $" ACK is for {ack.AckedControlId}, not {message.ControlId}.";
                        }
                    }
                }
                catch (ConnectException ex)
                {
                    outcome = SendOutcome.ConnectFailed;
                    detail = ex.Message;
                }
                catch (TimeoutException ex)
                {
                    outcome = SendOutcome.Timeout;
                    detail = ex.Message;
                }
                catch (Exception ex) when (ex is IOException or SocketException or MllpProtocolException)
                {
                    outcome = SendOutcome.ConnectionLost;
                    detail = ex.Message;
                }

                // After anything but a clean ACK the connection state is unknown (a late ACK could
                // still arrive), so start the next message on a fresh connection.
                if (outcome is SendOutcome.Timeout or SendOutcome.ConnectionLost or SendOutcome.Invalid && client is not null)
                {
                    await client.DisposeAsync();
                    client = null;
                }

                var passed = idMatches && send.Expect switch
                {
                    "AA" => outcome == SendOutcome.Accepted,
                    "AE" => outcome == SendOutcome.Error,
                    "AR" => outcome == SendOutcome.Rejected,
                    "NONE" => outcome is SendOutcome.Timeout or SendOutcome.ConnectionLost,
                    _ => outcome is SendOutcome.Accepted or SendOutcome.Error or SendOutcome.Rejected,
                };
                var elapsed = clock.GetElapsedTime(sentAt);
                var result = new SendResult(send, message.ControlId, outcome, detail.Trim(), elapsed, passed);
                results.Add(result);

                logger.Log(passed ? LogLevel.Information : LogLevel.Warning,
                    "[{Number}/{Total}] {Label,-28} {ControlId}  {Outcome} {Detail} ({Elapsed:0} ms)",
                    send.Number.ToString().PadLeft(width), plan.Sends.Count, send.Label, message.ControlId,
                    outcome, result.Detail, elapsed.TotalMilliseconds);
                onResult?.Invoke(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopped = true;
            logger.LogWarning("Stopped after {Sent} of {Total} messages", results.Count, plan.Sends.Count);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
        }

        return new RunSummary(plan.Name, results, clock.GetElapsedTime(started), plan.Sends.Count, stopped);
    }

    private async Task<MllpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await MllpClient.ConnectAsync(host, port, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new ConnectException($"Cannot connect to {host}:{port}: {ex.Message}");
        }
    }

    private sealed class ConnectException(string message) : Exception(message);
}
