using Hopital.Receiver;

namespace Hopital.Tests;

public class ReceiverStateTests
{
    [Fact]
    public void Starts_in_accept_and_counts_messages()
    {
        var state = new ReceiverState(TimeProvider.System);

        Assert.Equal((ReceiverMode.Accept, null), state.NextMessage());
        Assert.Equal(ReceiverMode.Accept, state.Status.Mode);
        Assert.Equal(1, state.Status.Received);
        Assert.NotNull(state.Status.LastReceived);
    }

    [Fact]
    public void A_mode_for_N_messages_goes_back_to_accept_afterwards()
    {
        var state = new ReceiverState(TimeProvider.System);
        var changes = new List<ReceiverMode>();
        state.Changed += s => changes.Add(s.Mode);

        state.Set(ReceiverMode.Error, messages: 2, text: "Busy");

        Assert.Equal((ReceiverMode.Error, "Busy"), state.NextMessage());
        Assert.Equal(1, state.Status.RemainingMessages);
        Assert.Equal((ReceiverMode.Error, "Busy"), state.NextMessage());
        Assert.Equal((ReceiverMode.Accept, null), state.NextMessage());
        Assert.Equal([ReceiverMode.Error, ReceiverMode.Accept], changes);
    }

    [Fact]
    public async Task A_mode_for_a_time_goes_back_to_accept_afterwards()
    {
        var state = new ReceiverState(TimeProvider.System);
        var reverted = new TaskCompletionSource<ReceiverStatus>();
        state.Set(ReceiverMode.Offline, duration: TimeSpan.FromMilliseconds(150));
        state.Changed += s => reverted.TrySetResult(s);

        Assert.Equal(ReceiverMode.Offline, state.Status.Mode);
        Assert.NotNull(state.Status.Until);
        var status = await reverted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReceiverMode.Accept, status.Mode);
        Assert.Null(status.Until);
    }

    [Fact]
    public void Setting_a_new_mode_cancels_the_old_temporary_one()
    {
        var state = new ReceiverState(TimeProvider.System);
        state.Set(ReceiverMode.Reject, messages: 1, duration: TimeSpan.FromMinutes(5));

        state.Set(ReceiverMode.Hang);

        Assert.Equal(ReceiverMode.Hang, state.NextMessage().Mode);
        Assert.Equal(ReceiverMode.Hang, state.NextMessage().Mode);
        Assert.Null(state.Status.Until);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    public void Bad_limits_are_refused(int? messages, int? seconds)
    {
        var state = new ReceiverState(TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            state.Set(ReceiverMode.Error, messages, seconds is null ? null : TimeSpan.FromSeconds(seconds.Value)));
    }
}
