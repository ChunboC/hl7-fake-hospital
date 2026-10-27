using System.Text;
using Hopital.Mllp;

namespace Hopital.Tests;

public class MllpReaderTests
{
    private static byte[] Frame(string text) => MllpFrame.Wrap(Encoding.ASCII.GetBytes(text));

    private static string Text(MllpMessage? message) => Encoding.ASCII.GetString(message!.Payload);

    [Fact]
    public void Wrap_adds_start_and_end_blocks()
    {
        Assert.Equal(new byte[] { 0x0B, (byte)'A', 0x1C, 0x0D }, MllpFrame.Wrap("A"u8));
    }

    [Fact]
    public async Task Reads_one_message()
    {
        var reader = new MllpReader(new MemoryStream(Frame("MSH|^~\\&|A\r")));

        var message = await reader.ReadMessageAsync();

        Assert.Equal("MSH|^~\\&|A\r", Text(message));
        Assert.Equal(0, message!.SkippedBytes);
        Assert.False(message.MissingTrailingCr);
        Assert.Null(await reader.ReadMessageAsync());
    }

    [Fact]
    public async Task Reads_a_message_that_arrives_one_byte_at_a_time()
    {
        var reader = new MllpReader(new TrickleStream(Frame("MSH|one\rPID|1\r")));

        Assert.Equal("MSH|one\rPID|1\r", Text(await reader.ReadMessageAsync()));
    }

    [Fact]
    public async Task Reads_back_to_back_messages_from_one_read()
    {
        var bytes = Frame("first").Concat(Frame("second")).Concat(Frame("third")).ToArray();
        var reader = new MllpReader(new MemoryStream(bytes));

        Assert.Equal("first", Text(await reader.ReadMessageAsync()));
        Assert.Equal("second", Text(await reader.ReadMessageAsync()));
        Assert.Equal("third", Text(await reader.ReadMessageAsync()));
        Assert.Null(await reader.ReadMessageAsync());
    }

    [Fact]
    public async Task Skips_and_counts_junk_before_the_start_block()
    {
        var reader = new MllpReader(new MemoryStream("\r\njunk"u8.ToArray().Concat(Frame("msg")).ToArray()));

        var message = await reader.ReadMessageAsync();

        Assert.Equal("msg", Text(message));
        Assert.Equal(6, message!.SkippedBytes);
    }

    [Fact]
    public async Task Accepts_an_end_block_without_trailing_cr_and_keeps_the_next_byte()
    {
        byte[] bytes = [0x0B, (byte)'a', 0x1C, 0x0B, (byte)'b', 0x1C, 0x0D];
        var reader = new MllpReader(new MemoryStream(bytes));

        var first = await reader.ReadMessageAsync();
        var second = await reader.ReadMessageAsync();

        Assert.Equal("a", Text(first));
        Assert.True(first!.MissingTrailingCr);
        Assert.Equal("b", Text(second));
        Assert.False(second!.MissingTrailingCr);
    }

    [Fact]
    public async Task Drops_a_partial_message_cut_off_by_a_new_start_block()
    {
        byte[] bytes = [0x0B, (byte)'x', (byte)'y', 0x0B, (byte)'z', 0x1C, 0x0D];
        var reader = new MllpReader(new MemoryStream(bytes));

        var message = await reader.ReadMessageAsync();

        Assert.Equal("z", Text(message));
        Assert.Equal(3, message!.SkippedBytes);
    }

    [Fact]
    public async Task Throws_when_the_stream_ends_partway_through_a_message()
    {
        var reader = new MllpReader(new MemoryStream([0x0B, (byte)'M', (byte)'S', (byte)'H']));

        await Assert.ThrowsAsync<MllpProtocolException>(() => reader.ReadMessageAsync());
    }

    [Fact]
    public async Task Throws_when_a_message_is_over_the_size_limit()
    {
        var reader = new MllpReader(new MemoryStream(Frame("0123456789")), maxMessageBytes: 5);

        await Assert.ThrowsAsync<MllpProtocolException>(() => reader.ReadMessageAsync());
    }

    /// <summary>Returns at most one byte per read, to simulate a slow or fragmented connection.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
