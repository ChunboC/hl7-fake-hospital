using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Hopital.Hl7;
using Hopital.Mllp;
using Hopital.Receiver;
using Hopital.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hopital.Tests;

/// <summary>
/// The web API end to end: a real receiver, its HTTP side, and scenarios run from a temporary
/// folder against that same receiver, so every run also shows up as received messages.
/// </summary>
public sealed class WebApiTests : IAsyncLifetime
{
    private const string TwoPatients = """
        name: Two patients
        description: For the web tests.
        patients: 2
        spacing: 0s
        ackTimeout: 5s
        steps:
          - forEachPatient:
              - send: adt-a04
        """;

    private const string SlowThree = """
        name: Slow three
        patients: 3
        spacing: 10s
        steps:
          - forEachPatient:
              - send: adt-a04
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hopital-tests", Guid.NewGuid().ToString("N"));
    private readonly int _mllpPort = TestPaths.FreePort();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
    private readonly ReceiverState _state = new(TimeProvider.System);
    private readonly EventHub _hub = new();
    private MessageStore _store = null!;
    private RunManager _runs = null!;
    private WebApplication _app = null!;
    private Task _receiving = null!;
    private HttpClient _http = null!;

    private string Scenarios => Path.Combine(_root, "scenarios");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Scenarios);
        File.WriteAllText(Path.Combine(Scenarios, "two-patients.yaml"), TwoPatients);
        File.WriteAllText(Path.Combine(Scenarios, "slow-three.yaml"), SlowThree);
        File.WriteAllText(Path.Combine(Scenarios, "broken.yaml"), "patients: 1\nsteps:\n  - send: adt-a99\n");
        File.WriteAllText(Path.Combine(Scenarios, "notes.txt"), "not a scenario");

        _store = new MessageStore(Path.Combine(_root, "received"), TimeProvider.System);
        _runs = new RunManager(Scenarios, _hub, NullLogger.Instance, TimeProvider.System);
        var receiver = new FakeEpicReceiver(IPAddress.Loopback, _mllpPort, _state, _store, NullLogger.Instance);
        receiver.MessageSaved += saved => _hub.Publish("received", saved);
        _receiving = receiver.RunAsync(_stop.Token);

        _app = ControlApi.Build("127.0.0.1", 0, _state, _store, _runs, _hub);
        await _app.StartAsync(_stop.Token);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
        await WaitForReceiver();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _runs.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _stop.Cancel();
        await _receiving;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task WaitForReceiver()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, _mllpPort, _stop.Token);
                return;
            }
            catch (SocketException) when (attempt < 40)
            {
                await Task.Delay(50);
            }
        }
    }

    private Task<HttpResponseMessage> StartRun(string scenario, int? port = null, bool dryRun = false, string host = "127.0.0.1") =>
        _http.PostAsJsonAsync("api/runs", new { scenario, host, port = port ?? _mllpPort, dryRun }, _stop.Token);

    private async Task<JsonElement> Json(string url) =>
        JsonDocument.Parse(await _http.GetStringAsync(url, _stop.Token)).RootElement;

    [Fact]
    public async Task Scenarios_lists_yaml_files_with_message_counts_and_problems()
    {
        var list = (await Json("api/scenarios")).EnumerateArray().ToList();

        Assert.Equal(["broken.yaml", "slow-three.yaml", "two-patients.yaml"], list.Select(s => s.GetProperty("file").GetString()));
        var two = list[2];
        Assert.Equal("Two patients", two.GetProperty("name").GetString());
        Assert.Equal("For the web tests.", two.GetProperty("description").GetString());
        Assert.Equal(2, two.GetProperty("messages").GetInt32());
        Assert.Equal(JsonValueKind.Null, two.GetProperty("problem").ValueKind);
        Assert.Contains("adt-a99", list[0].GetProperty("problem").GetString());
        Assert.Equal(20, list[1].GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task A_run_goes_through_the_receiver_and_its_progress_streams_live()
    {
        using var events = await EventStream.Open(_http, _stop.Token);
        Assert.Equal("mode", (await events.Next()).Type);

        var start = await StartRun("two-patients.yaml");
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);

        var seen = new List<(string Type, JsonElement Data)>();
        while (!seen.Any(e => e.Type == "run" && e.Data.GetProperty("state").GetString() != "running")
               || seen.Count(e => e.Type == "received") < 2)
            seen.Add(await events.Next());

        Assert.Equal("running", seen.First(e => e.Type == "run").Data.GetProperty("state").GetString());
        var results = seen.Where(e => e.Type == "result").Select(e => e.Data.GetProperty("result")).ToList();
        Assert.Equal([1, 2], results.Select(r => r.GetProperty("number").GetInt32()));
        Assert.All(results, r => Assert.Equal("Accepted", r.GetProperty("outcome").GetString()));
        var received = seen.Where(e => e.Type == "received").Select(e => e.Data.GetProperty("controlId").GetString());
        Assert.Equal(results.Select(r => r.GetProperty("controlId").GetString()), received);

        var current = await Json("api/runs/current");
        var run = current.GetProperty("run");
        Assert.Equal("finished", run.GetProperty("state").GetString());
        Assert.Equal(2, run.GetProperty("sent").GetInt32());
        Assert.Equal(0, run.GetProperty("failed").GetInt32());
        Assert.StartsWith("Two patients: 2 sent, 2 as expected", run.GetProperty("summary")[0].GetString());
        Assert.Equal(2, current.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Mode_changes_stream_live()
    {
        using var events = await EventStream.Open(_http, _stop.Token);
        await events.Next();

        await _http.PostAsync("mode/error?messages=2&text=Busy", null, _stop.Token);
        var change = await events.Next();

        Assert.Equal("mode", change.Type);
        Assert.Equal("Error", change.Data.GetProperty("mode").GetString());
        Assert.Equal(2, change.Data.GetProperty("remainingMessages").GetInt32());
    }

    [Fact]
    public async Task Only_one_run_at_a_time_and_stop_ends_it_with_what_was_sent()
    {
        Assert.Equal(HttpStatusCode.Created, (await StartRun("slow-three.yaml")).StatusCode);
        var second = await StartRun("two-patients.yaml");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("slow-three.yaml is still running", await second.Content.ReadAsStringAsync());

        // Wait for the first message, then stop during the 10 s pause.
        while (_runs.Current!.Run.Sent == 0)
            await Task.Delay(20, _stop.Token);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync("api/runs/current/stop", null, _stop.Token)).StatusCode);
        await _runs.Completion.WaitAsync(_stop.Token);

        var run = (await Json("api/runs/current")).GetProperty("run");
        Assert.Equal("stopped", run.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("sent").GetInt32());
        Assert.Contains("(stopped after 1 of 3)", run.GetProperty("summary")[0].GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync("api/runs/current/stop", null, _stop.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await StartRun("two-patients.yaml")).StatusCode);
    }

    [Fact]
    public async Task The_page_is_served_at_the_root_and_the_help_at_help()
    {
        var page = await _http.GetAsync("", _stop.Token);
        var help = await _http.GetStringAsync("help", _stop.Token);

        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<title>Hopital</title>", html);
        Assert.Contains("new EventSource(\"api/events\")", html);
        Assert.Contains("<dialog id=\"viewer\">", html);
        Assert.Contains("POST /api/runs", help);
    }

    [Fact]
    public async Task Nothing_run_yet_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("api/runs/current", _stop.Token)).StatusCode);
    }

    [Theory]
    [InlineData("missing.yaml", HttpStatusCode.NotFound)]
    [InlineData("../two-patients.yaml", HttpStatusCode.NotFound)]
    [InlineData("notes.txt", HttpStatusCode.NotFound)]
    [InlineData("broken.yaml", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.BadRequest)]
    public async Task Bad_scenarios_are_refused(string scenario, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await StartRun(scenario)).StatusCode);
        Assert.Null(_runs.Current);
    }

    [Theory]
    [InlineData(0, "127.0.0.1")]
    [InlineData(70000, "127.0.0.1")]
    [InlineData(6661, " ")]
    public async Task Bad_targets_are_refused(int port, string host)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await StartRun("two-patients.yaml", port, host: host)).StatusCode);
    }

    [Fact]
    public async Task Dry_run_returns_the_messages_without_sending()
    {
        var response = await StartRun("two-patients.yaml", dryRun: true);
        var messages = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, messages.Count);
        Assert.StartsWith("MSH|^~\\&|EPIC|HOPITAL", messages[0].GetProperty("text").GetString());
        Assert.Equal("AA", messages[0].GetProperty("expect").GetString());
        Assert.Null(_runs.Current);
        Assert.Empty(_store.Recent(10));
    }

    [Fact]
    public async Task Received_messages_can_be_listed_read_and_their_files_fetched()
    {
        var oru = Hl7Text.ToWire(File.ReadAllText(Path.Combine(TestPaths.ResultSamples, "oru-r01-embedded-images.hl7")));
        await using (var client = await MllpClient.ConnectAsync("127.0.0.1", _mllpPort, _stop.Token))
            await client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(oru), TimeSpan.FromSeconds(5), _stop.Token);

        var summary = Assert.Single((await Json("api/received?limit=10")).EnumerateArray());
        var file = summary.GetProperty("file").GetString()!;
        var day = file.Split('/')[0];
        var name = Path.GetFileNameWithoutExtension(file);

        var detail = await Json($"api/received/{day}/{name}");
        Assert.Equal(summary.GetProperty("controlId").GetString(), detail.GetProperty("summary").GetProperty("controlId").GetString());
        Assert.StartsWith("MSH|", detail.GetProperty("text").GetString());
        Assert.Contains("\nPID|", detail.GetProperty("text").GetString());

        var image = summary.GetProperty("attachments").EnumerateArray().First(a => a.TryGetProperty("file", out _)).GetProperty("file").GetString();
        var fetched = await _http.GetAsync($"api/files/{day}/{image}", _stop.Token);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType!.MediaType);
        Assert.False(fetched.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", fetched.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], (await fetched.Content.ReadAsByteArrayAsync())[..4]);

        var raw = await _http.GetAsync($"api/files/{day}/{name}.hl7", _stop.Token);
        Assert.StartsWith("text/plain", raw.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task PDFs_open_in_the_browser_but_HTML_attachments_are_sandboxed()
    {
        const string html =
            "MSH|^~\\&|IMAGING|IMAGING|EPIC|HOPITAL|20261026120000||ORU^R01^ORU_R01|IMG000000000000099|P|2.5.1\r" +
            "PID|1||98230284^^^HOPITAL^MR||Quintero^Omar\r" +
            "OBX|1|ED|REPORT^Report^L||^TEXT^HTML^A^<script>alert(1)</script>||||||F\r";
        var pdf = Hl7Text.ToWire(File.ReadAllText(Path.Combine(TestPaths.ResultSamples, "mdm-t02-pdf-report.hl7")));
        await using (var client = await MllpClient.ConnectAsync("127.0.0.1", _mllpPort, _stop.Token))
        {
            await client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(html), TimeSpan.FromSeconds(5), _stop.Token);
            await client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(pdf), TimeSpan.FromSeconds(5), _stop.Token);
        }

        var saved = (await Json("api/received?limit=10")).EnumerateArray()
            .Select(s => (Day: s.GetProperty("file").GetString()!.Split('/')[0],
                          File: s.GetProperty("attachments")[0].GetProperty("file").GetString()!))
            .ToList();
        var pdfResponse = await _http.GetAsync($"api/files/{saved[0].Day}/{saved[0].File}", _stop.Token);
        var htmlResponse = await _http.GetAsync($"api/files/{saved[1].Day}/{saved[1].File}", _stop.Token);

        Assert.Equal("application/pdf", pdfResponse.Content.Headers.ContentType!.MediaType);
        Assert.False(pdfResponse.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("text/html", htmlResponse.Content.Headers.ContentType!.MediaType);
        Assert.Equal("sandbox", htmlResponse.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("api/received?limit=0")]
    [InlineData("api/received?limit=501")]
    public async Task Bad_limits_are_refused(string url)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync(url, _stop.Token)).StatusCode);
    }

    [Theory]
    [InlineData("api/received/2026-10-26/missing")]
    [InlineData("api/received/..%2F..%2Fscenarios/two-patients")]
    [InlineData("api/files/2026-10-26/..%2F..%2Fscenarios%2Ftwo-patients.yaml")]
    [InlineData("api/files/..%2Fscenarios/two-patients.yaml")]
    public async Task Paths_outside_the_store_are_not_found(string url)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(url, _stop.Token)).StatusCode);
    }

    /// <summary>Reads server-sent events one at a time.</summary>
    private sealed class EventStream(HttpResponseMessage response, StreamReader reader, CancellationToken cancellationToken) : IDisposable
    {
        public static async Task<EventStream> Open(HttpClient http, CancellationToken cancellationToken)
        {
            var response = await http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            return new EventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken)), cancellationToken);
        }

        public async Task<(string Type, JsonElement Data)> Next()
        {
            string? type = null;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("event: "))
                    type = line["event: ".Length..];
                else if (line.StartsWith("data: ") && type is not null)
                    return (type, JsonDocument.Parse(line["data: ".Length..]).RootElement);
            }
            throw new EndOfStreamException("The event stream ended.");
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }
}
