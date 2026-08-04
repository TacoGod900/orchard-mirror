namespace Orchard.Mirror.Media;

/// <summary>
/// A small producer/consumer queue for real-time media. Once full it discards the oldest item so
/// latency remains bounded; a consumer can also take only the newest item and discard everything
/// that has already become stale.
/// </summary>
public sealed class LatestFrameBuffer<T>
{
    private readonly object sync = new();
    private readonly Queue<T> items;
    private readonly Action<T>? discard;

    public LatestFrameBuffer(int capacity = 2, Action<T>? discard = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        this.discard = discard;
        items = new Queue<T>(capacity);
    }

    public int Capacity { get; }

    public long DroppedCount { get; private set; }

    public int Count
    {
        get
        {
            lock (sync)
            {
                return items.Count;
            }
        }
    }

    public void Enqueue(T item)
    {
        lock (sync)
        {
            while (items.Count >= Capacity)
            {
                Drop(items.Dequeue());
            }

            items.Enqueue(item);
            Monitor.Pulse(sync);
        }
    }

    /// <summary>
    /// Removes the newest item and discards any older items queued before it. This is the normal
    /// presentation operation: showing an old frame merely turns scheduling delay into visible lag.
    /// </summary>
    public bool TryDequeueLatest(out T? item)
    {
        lock (sync)
        {
            if (items.Count == 0)
            {
                item = default;
                return false;
            }

            while (items.Count > 1)
            {
                Drop(items.Dequeue());
            }

            item = items.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// Removes the oldest queued item without discarding newer items. Predictive video decoders
    /// must use this operation because later coded pictures can reference every earlier picture.
    /// Presentation may skip decoded output, but decode order itself must remain contiguous.
    /// </summary>
    public bool TryDequeueOldest(out T? item)
    {
        lock (sync)
        {
            return items.TryDequeue(out item);
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            while (items.TryDequeue(out T? item))
            {
                Drop(item);
            }
        }
    }

    private void Drop(T item)
    {
        DroppedCount++;
        discard?.Invoke(item);
    }
}
