using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Hopital.Web;

/// <summary>One live update for the web page: an event type and its data as JSON.</summary>
public sealed record ServerEvent(string Type, string Json);

/// <summary>
/// Hands live updates (mode changes, received messages, run progress) to every open page.
/// Each listener has its own queue; a listener that falls far behind loses its oldest updates
/// rather than slowing down the receiver or a run.
/// </summary>
public sealed class EventHub
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const int QueueLength = 2000;

    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();

    public int Listeners => _subscriptions.Count;

    public void Publish<T>(string type, T data)
    {
        var item = new ServerEvent(type, JsonSerializer.Serialize(data, Json));
        foreach (var subscription in _subscriptions.Keys)
            subscription.Writer.TryWrite(item);
    }

    /// <summary>Start receiving updates; dispose to stop.</summary>
    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        _subscriptions[subscription] = 0;
        return subscription;
    }

    public sealed class Subscription : IDisposable
    {
        private readonly EventHub _hub;
        private readonly Channel<ServerEvent> _channel = Channel.CreateBounded<ServerEvent>(
            new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        internal Subscription(EventHub hub) => _hub = hub;

        internal ChannelWriter<ServerEvent> Writer => _channel.Writer;

        public ChannelReader<ServerEvent> Reader => _channel.Reader;

        public void Dispose()
        {
            _hub._subscriptions.TryRemove(this, out _);
            _channel.Writer.TryComplete();
        }
    }
}
