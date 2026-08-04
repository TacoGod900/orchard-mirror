using Orchard.Mirror.Media;

namespace Orchard.Mirror.Tests;

internal static class LatestFrameQueueTests
{
    internal static void BoundsLatencyByDroppingTheOldestFrame()
    {
        List<int> discarded = [];
        LatestFrameBuffer<int> queue = new(2, discarded.Add);

        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(3);

        Assert(queue.Count == 2, $"Queue grew to {queue.Count}; its capacity is 2.");
        Assert(discarded.SequenceEqual([1]), "The oldest frame was not discarded when full.");
        Assert(queue.DroppedCount == 1, $"Expected one drop, got {queue.DroppedCount}.");
    }

    internal static void ConsumerTakesOnlyTheNewestFrame()
    {
        List<int> discarded = [];
        LatestFrameBuffer<int> queue = new(2, discarded.Add);
        queue.Enqueue(10);
        queue.Enqueue(11);

        Assert(queue.TryDequeueLatest(out int frame), "A non-empty queue returned no frame.");
        Assert(frame == 11, $"Presented frame {frame}; expected newest frame 11.");
        Assert(discarded.SequenceEqual([10]), "The stale queued frame was not discarded.");
        Assert(queue.Count == 0, "Taking the latest frame did not drain the queue.");
    }

    internal static void DecoderTakesTheOldestFrame()
    {
        LatestFrameBuffer<int> queue = new(4);
        queue.Enqueue(20);
        queue.Enqueue(21);

        Assert(queue.TryDequeueOldest(out int frame), "A non-empty decode queue returned no frame.");
        Assert(frame == 20, $"Decoded frame {frame}; expected oldest frame 20.");
        Assert(queue.Count == 1, "Taking the oldest frame discarded its dependent successor.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
