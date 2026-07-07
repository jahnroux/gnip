using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Gnip;

/// <summary>
/// In-memory fan-out of live samples to connected SSE clients. The collector calls
/// <see cref="Publish"/>; each client holds a <see cref="Subscription"/> and reads its channel.
/// Per-subscriber channels are bounded and drop the oldest sample if a client falls behind,
/// so one slow client never blocks the collector or the other clients.
/// </summary>
public sealed class SampleHub
{
    private readonly ConcurrentDictionary<Guid, Channel<PingSample>> _subscribers = new();

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<PingSample>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    public void Publish(PingSample sample)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(sample);
    }

    public int SubscriberCount => _subscribers.Count;

    private void Remove(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly SampleHub _hub;
        private readonly Guid _id;

        public ChannelReader<PingSample> Reader { get; }

        internal Subscription(SampleHub hub, Guid id, ChannelReader<PingSample> reader)
        {
            _hub = hub;
            _id = id;
            Reader = reader;
        }

        public void Dispose() => _hub.Remove(_id);
    }
}
