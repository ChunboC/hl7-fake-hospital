using System.Text.Json;
using System.Text.Json.Serialization;
using Hopital.Hl7;
using Hopital.Scenarios;
using Hopital.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Hopital.Receiver;

public sealed record StatusResponse(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReceiverMode>))] ReceiverMode Mode,
    int? RemainingMessages,
    DateTimeOffset? Until,
    string? Text,
    long Received,
    DateTimeOffset? LastReceived,
    string SavingTo);

/// <param name="Text">The message with one segment per line.</param>
public sealed record ReceivedDetail(ReceivedSummary Summary, string Text);

public sealed record RunRequest(string? Scenario, string? Host, int? Port, bool DryRun);

/// <summary>
/// The receiver's HTTP side: the web page, switching modes from anywhere on the network, and the API behind the page.
///   curl http://hopital.local:6680/status
///   curl -X POST "http://hopital.local:6680/mode/error?messages=3"
///   curl -X POST "http://hopital.local:6680/mode/offline?for=2m"
/// No authentication: it is a test tool for a private network.
/// </summary>
public static class ControlApi
{
    private const int MaxRecent = 500;
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new()
    {
        Mappings = { [".hl7"] = "text/plain; charset=utf-8", [".dcm"] = "application/dicom" },
    };

    public static WebApplication Build(string bind, int port, ReceiverState state, MessageStore store, RunManager runs, EventHub hub)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{bind}:{port}");
        var app = builder.Build();

        StatusResponse Status(ReceiverStatus s) =>
            new(s.Mode, s.RemainingMessages, s.Until, s.Text, s.Received, s.LastReceived, store.Root);

        state.Changed += s => hub.Publish("mode", Status(s));

        app.MapGet("/", () => Results.Text(Page.Html, "text/html; charset=utf-8"));

        app.MapGet("/help", () => Results.Text("""
            Hopital fake Epic receiver

            GET  /                          the web page

            GET  /status
            POST /mode/{accept|error|reject|hang|drop|offline}?messages=N&for=2m&text=...

              messages  go back to accept after N messages
              for       go back to accept after this long (e.g. 30s, 2m)
              text      AE/AR text for MSA-3 and ERR

            GET  /api/scenarios
            POST /api/runs                  {"scenario": "smoke-test.yaml", "host": "...", "port": 6661, "dryRun": false}
            GET  /api/runs/current
            POST /api/runs/current/stop
            GET  /api/received?limit=50
            GET  /api/received/{day}/{name}
            GET  /api/files/{day}/{file}
            GET  /api/events                live updates (server-sent events)
            """));

        app.MapGet("/status", () => Status(state.Status));

        app.MapPost("/mode/{mode}", (string mode, int? messages, string? @for, string? text) =>
        {
            if (!Enum.TryParse<ReceiverMode>(mode, ignoreCase: true, out var parsed) || int.TryParse(mode, out _))
                return Results.BadRequest($"Unknown mode '{mode}'. Use accept, error, reject, hang, drop or offline.");
            try
            {
                return Results.Ok(Status(state.Set(parsed, messages, @for is null ? null : Durations.Parse(@for), text)));
            }
            catch (Exception ex) when (ex is ScenarioException or ArgumentOutOfRangeException)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/scenarios", () => Results.Json(runs.Scenarios(), EventHub.Json));

        app.MapPost("/api/runs", (RunRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Scenario))
                return Results.BadRequest("Choose a scenario.");
            try
            {
                if (request.DryRun)
                    return Results.Json(runs.Preview(request.Scenario), EventHub.Json);
                var run = runs.Start(request.Scenario, request.Host ?? "", request.Port ?? 0);
                return Results.Json(run, EventHub.Json, statusCode: StatusCodes.Status201Created);
            }
            catch (ScenarioNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (RunInProgressException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (Exception ex) when (ex is ScenarioException or ArgumentException)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/runs/current", () =>
            runs.Current is { } current ? Results.Json(current, EventHub.Json) : Results.NotFound("Nothing has been run yet."));

        app.MapPost("/api/runs/current/stop", () =>
            runs.Stop() ? Results.Accepted() : Results.Conflict("Nothing is running."));

        app.MapGet("/api/received", (int? limit) =>
            limit is <= 0 or > MaxRecent
                ? Results.BadRequest($"limit must be 1 to {MaxRecent}.")
                : Results.Json(store.Recent(limit ?? 50), EventHub.Json));

        app.MapGet("/api/received/{day}/{name}", (string day, string name) =>
        {
            if (store.Read(day, name) is not { } summary || store.PathOf(day, name + ".hl7") is not { } path)
                return Results.NotFound();
            var text = Hl7Text.ToDisplay(Hl7Text.Encoding.GetString(File.ReadAllBytes(path)));
            return Results.Json(new ReceivedDetail(summary, text), EventHub.Json);
        });

        app.MapGet("/api/files/{day}/{file}", (string day, string file, HttpContext context) =>
        {
            if (store.PathOf(day, file) is not { } path)
                return Results.NotFound();
            // Attachments came from whoever sent the message: never let one run script on this page.
            // Images, PDFs and text cannot, and a sandbox would stop the browser showing a PDF,
            // so only everything else (HTML, XML, SVG, unknown types) is sandboxed.
            var type = ContentTypes.TryGetContentType(file, out var known) ? known : "application/octet-stream";
            if (!IsInert(type))
                context.Response.Headers.ContentSecurityPolicy = "sandbox";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(path, type);
        });

        app.MapGet("/api/events", async (HttpContext context) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            // End the stream when the page closes or the receiver shuts down, whichever comes first.
            using var open = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            var cancellationToken = open.Token;
            using var subscription = hub.Subscribe();

            async Task Write(string type, string json)
            {
                await context.Response.WriteAsync($"event: {type}\ndata: {json}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            try
            {
                // Start with where things stand, then send changes as they happen.
                await Write("mode", JsonSerializer.Serialize(Status(state.Status), EventHub.Json));
                if (runs.Current is { } current)
                    await Write("run", JsonSerializer.Serialize(current.Run, EventHub.Json));

                while (!cancellationToken.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(KeepAlive);
                    try
                    {
                        if (!await subscription.Reader.WaitToReadAsync(wait.Token))
                            break;
                        while (subscription.Reader.TryRead(out var item))
                            await Write(item.Type, item.Json);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Quiet for a while: a comment line keeps proxies and browsers from closing the stream.
                        await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The page was closed or the receiver is stopping.
            }
        });

        return app;
    }

    private static bool IsInert(string contentType) =>
        contentType is "application/pdf" or "application/json" || contentType.StartsWith("text/plain")
        || (contentType.StartsWith("image/") && !contentType.StartsWith("image/svg"));
}
