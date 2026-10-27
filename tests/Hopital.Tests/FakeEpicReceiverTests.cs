using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Hopital.Hl7;
using Hopital.Mllp;
using Hopital.Receiver;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hopital.Tests;

public sealed class FakeEpicReceiverTests : IAsyncDisposable
{
    private const string Oru =
        "MSH|^~\\&|IMAGING|IMAGING|EPIC|HOPITAL|20261025150000||ORU^R01^ORU_R01|IMG0000000001|P|2.5.1\r" +
        "PID|1||98230284^^^HOPITAL^MR||Quintero^Omar\r" +
        "OBR|1|ORD52625264^EPIC|ACC16942728^HOPITAL|ENDO-EUS^Endoscopic ultrasound^L\r";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hopital-tests", Guid.NewGuid().ToString("N"));
    private readonly int _port = TestPaths.FreePort();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly ReceiverState _state = new(TimeProvider.System);
    private readonly MessageStore _store;
    private readonly Task _running;
    private readonly List<ReceivedSummary> _announced = [];

    public FakeEpicReceiverTests()
    {
        _store = new MessageStore(_root, TimeProvider.System);
        var receiver = new FakeEpicReceiver(IPAddress.Loopback, _port, _state, _store, NullLogger.Instance);
        receiver.MessageSaved += s =>
        {
            lock (_announced)
                _announced.Add(s);
        };
        _running = receiver.RunAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _running;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<MllpClient> Connect()
    {
        // The listener starts in the background; give it a moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await MllpClient.ConnectAsync("127.0.0.1", _port, _stop.Token);
            }
            catch (SocketException) when (attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }

    private async Task<AckResult?> Send(MllpClient client, TimeSpan? timeout = null)
    {
        var reply = await client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(Oru), timeout ?? TimeSpan.FromSeconds(5), _stop.Token);
        return reply is null ? null : AckResult.Parse(Hl7Text.Encoding.GetString(reply.Payload));
    }

    [Fact]
    public async Task Accept_replies_AA_and_saves_the_message()
    {
        await using var client = await Connect();

        var ack = await Send(client);

        Assert.Equal(AckStatus.Accepted, ack!.Status);
        Assert.Equal("IMG0000000001", ack.AckedControlId);
        var saved = Directory.GetFiles(_root, "*.hl7", SearchOption.AllDirectories);
        Assert.Equal(Oru, File.ReadAllText(Assert.Single(saved)));
        Assert.Equal(1, _state.Status.Received);
        var announced = Assert.Single(_announced);
        Assert.Equal("IMG0000000001", announced.ControlId);
        Assert.Equal("Accept", announced.Mode);
    }

    [Fact]
    public async Task Error_and_reject_reply_AE_and_AR_with_text_then_go_back_to_accept()
    {
        await using var client = await Connect();

        _state.Set(ReceiverMode.Error, messages: 1, text: "Chart locked");
        var error = await Send(client);
        _state.Set(ReceiverMode.Reject, messages: 1);
        var reject = await Send(client);
        var after = await Send(client);

        Assert.Equal(AckStatus.Error, error!.Status);
        Assert.Equal("Chart locked", error.Text);
        Assert.Equal(AckStatus.Rejected, reject!.Status);
        Assert.Equal(AckStatus.Accepted, after!.Status);
        Assert.Equal(3, Directory.GetFiles(_root, "*.hl7", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Hang_never_replies_so_the_sender_times_out()
    {
        await using var client = await Connect();
        _state.Set(ReceiverMode.Hang);

        await Assert.ThrowsAsync<TimeoutException>(() => Send(client, TimeSpan.FromMilliseconds(300)));
        Assert.Single(Directory.GetFiles(_root, "*.hl7", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Drop_closes_the_connection_without_replying()
    {
        await using var client = await Connect();
        _state.Set(ReceiverMode.Drop);

        Assert.Null(await Send(client));
    }

    [Fact]
    public async Task Offline_refuses_connections_and_closes_open_ones_until_back_online()
    {
        await using (var open = await Connect())
        {
            Assert.Equal(AckStatus.Accepted, (await Send(open))!.Status);

            _state.Set(ReceiverMode.Offline);
            // The open connection is closed: the next send gets no ACK.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                if (await Send(open, TimeSpan.FromSeconds(2)) is null)
                    throw new IOException("closed");
            });
        }

        await Assert.ThrowsAsync<SocketException>(() => MllpClient.ConnectAsync("127.0.0.1", _port, _stop.Token));

        _state.Set(ReceiverMode.Accept);
        await using var again = await Connect();
        Assert.Equal(AckStatus.Accepted, (await Send(again))!.Status);
    }

    [Fact]
    public async Task Control_API_switches_mode_and_reports_status()
    {
        var hub = new Web.EventHub();
        await using var runs = new Web.RunManager(TestPaths.Scenarios, hub, NullLogger.Instance, TimeProvider.System);
        var app = ControlApi.Build("127.0.0.1", 0, _state, _store, runs, hub);
        await app.StartAsync(_stop.Token);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            var set = await http.PostAsync("mode/error?messages=2&text=Busy", null, _stop.Token);
            var status = await http.GetFromJsonAsync<Dictionary<string, object>>("status", _stop.Token);
            var bad = await http.PostAsync("mode/sideways", null, _stop.Token);
            var badDuration = await http.PostAsync("mode/offline?for=soon", null, _stop.Token);

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(ReceiverMode.Error, _state.Status.Mode);
            Assert.Equal("Busy", _state.Status.Text);
            Assert.Equal("Error", status!["mode"].ToString());
            Assert.Equal("2", status["remainingMessages"].ToString());
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, badDuration.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
