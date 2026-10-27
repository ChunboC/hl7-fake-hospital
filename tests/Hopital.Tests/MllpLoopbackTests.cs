using System.Net;
using Hopital.Hl7;
using Hopital.Mllp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hopital.Tests;

public class MllpLoopbackTests
{
    private const string Adt =
        "MSH|^~\\&|EPIC|FAKEHOSP|OIE|IMAGING|20261025120000||ADT^A04^ADT_A01|MSG0001|P|2.5.1\r" +
        "PID|1||MRN100001^^^FAKEHOSP^MR||TESTPATIENT^ALEX\r";

    [Fact]
    public async Task Client_gets_an_AA_ack_for_each_message_on_one_connection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<string>();
        var listener = new MllpListener(IPAddress.Loopback, 0, (message, _, _) =>
        {
            var text = Hl7Text.Encoding.GetString(message.Payload);
            lock (received)
                received.Add(text);
            return Task.FromResult(MllpReply.Send(Hl7Text.Encoding.GetBytes(AckBuilder.For(text))));
        }, NullLogger.Instance);
        listener.Start();
        var run = listener.RunAsync(cts.Token);

        await using (var client = await MllpClient.ConnectAsync("127.0.0.1", listener.Port, cts.Token))
        {
            for (var i = 0; i < 3; i++)
            {
                var reply = await client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(Adt), TimeSpan.FromSeconds(5), cts.Token);
                var ack = Hl7Text.Encoding.GetString(reply!.Payload);
                Assert.Contains("\rMSA|AA|MSG0001\r", ack);
                Assert.StartsWith("MSH|^~\\&|OIE|IMAGING|EPIC|FAKEHOSP|", ack);
                Assert.Contains("|ACK^A04^ACK|", ack);
            }
        }

        cts.Cancel();
        await run;
        Assert.Equal(3, received.Count);
        Assert.All(received, text => Assert.Equal(Adt, text));
    }

    [Fact]
    public async Task Client_times_out_when_no_ack_comes_back()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new MllpListener(IPAddress.Loopback, 0, (_, _, _) => Task.FromResult(MllpReply.None), NullLogger.Instance);
        listener.Start();
        var run = listener.RunAsync(cts.Token);

        await using (var client = await MllpClient.ConnectAsync("127.0.0.1", listener.Port, cts.Token))
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.SendAndReceiveAsync(Hl7Text.Encoding.GetBytes(Adt), TimeSpan.FromMilliseconds(300), cts.Token));
        }

        cts.Cancel();
        await run;
    }

    [Fact]
    public void ToWire_turns_file_line_endings_into_segment_separators()
    {
        Assert.Equal("MSH|a\rPID|b\r", Hl7Text.ToWire("MSH|a\r\nPID|b\n\n"));
    }
}
