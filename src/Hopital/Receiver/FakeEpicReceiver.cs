using System.Net;
using Hopital.Hl7;
using Hopital.Mllp;
using Microsoft.Extensions.Logging;

namespace Hopital.Receiver;

/// <summary>
/// Plays Epic receiving results (ORU, MDM): saves everything it gets and answers according
/// to <see cref="ReceiverState"/>. Going offline really stops the listener, so the sender
/// sees refused connections, not just missing ACKs.
/// </summary>
public sealed class FakeEpicReceiver(IPAddress address, int port, ReceiverState state, MessageStore store, ILogger logger)
{
    /// <summary>Raised after each received message has been saved.</summary>
    public event Action<ReceivedSummary>? MessageSaved;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var changed = new SemaphoreSlim(0);
        void OnChanged(ReceiverStatus _) => changed.Release();
        state.Changed += OnChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (state.Status.Mode == ReceiverMode.Offline)
                {
                    logger.LogWarning("Offline: port {Port} is closed", port);
                    while (state.Status.Mode == ReceiverMode.Offline)
                        await changed.WaitAsync(cancellationToken);
                    continue;
                }

                using var online = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var listener = new MllpListener(address, port, HandleAsync, logger);
                listener.Start();
                logger.LogInformation("Listening on {Address}:{Port}, mode {Mode}", address, listener.Port, state.Status.Mode);
                var running = listener.RunAsync(online.Token);
                try
                {
                    while (state.Status.Mode != ReceiverMode.Offline)
                        await changed.WaitAsync(cancellationToken);
                }
                finally
                {
                    online.Cancel();
                    await running;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.Changed -= OnChanged;
        }
    }

    private Task<MllpReply> HandleAsync(MllpMessage message, EndPoint? remote, CancellationToken cancellationToken)
    {
        var (mode, text) = state.NextMessage();
        var wire = Hl7Text.Encoding.GetString(message.Payload);
        var ack = mode switch
        {
            ReceiverMode.Accept => AckBuilder.For(wire),
            ReceiverMode.Error => AckBuilder.For(wire, "AE", text ?? "Fake Epic is in error mode."),
            ReceiverMode.Reject => AckBuilder.For(wire, "AR", text ?? "Fake Epic is in reject mode."),
            _ => null,
        };

        var warnings = new List<string>();
        if (message.SkippedBytes > 0)
            warnings.Add($"{message.SkippedBytes} bytes outside MLLP framing were skipped before this message.");
        if (message.MissingTrailingCr)
            warnings.Add("MLLP end block was not followed by 0x0D.");

        var answer = ack is null ? (mode == ReceiverMode.Hang ? "no reply (hang)" : "connection closed (drop)") : AckResult.Parse(ack).Code;
        try
        {
            var saved = store.Save(message.Payload, remote?.ToString(), mode.ToString(), ack, warnings);
            var files = saved.Attachments.Count(a => a.File is not null);
            var references = saved.Attachments.Count(a => a.Reference is not null);
            logger.LogInformation(
                "{Type} {ControlId} from {Remote}: MRN {Mrn}, order {Placer}, accession {Accession}, {Files} file(s) saved, {References} reference(s) -> {Answer}",
                Or(saved.MessageType), Or(saved.ControlId), remote, Or(saved.PatientMrn), Or(saved.PlacerOrderNumber), Or(saved.AccessionNumber),
                files, references, answer);
            foreach (var problem in saved.Attachments.Where(a => a.Problem is not null))
                logger.LogWarning("OBX {Obx}: {Problem}", problem.Obx, problem.Problem);
            logger.LogInformation("Saved {File}", saved.File);
            MessageSaved?.Invoke(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Could not save a {Bytes}-byte message from {Remote}: {Reason}", message.Payload.Length, remote, ex.Message);
        }

        var reply = mode switch
        {
            ReceiverMode.Hang => MllpReply.None,
            ReceiverMode.Drop or ReceiverMode.Offline => MllpReply.Close,
            _ => MllpReply.Send(Hl7Text.Encoding.GetBytes(ack!)),
        };
        return Task.FromResult(reply);
    }

    private static string Or(string value) => value == "" ? "-" : value;
}
