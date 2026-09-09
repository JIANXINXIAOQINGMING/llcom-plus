using System;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Model
{
    // UI sends keep their submission order while wake delays and driver writes run
    // off the dispatcher. Callers capture the connection and copy data beforehand.
    internal sealed class SerialSendQueue
    {
        private const int MaxPendingSends = 256;
        private const long MaxPendingBytes = 16L * 1024 * 1024;
        private readonly object gate = new object();
        private Task tail = Task.CompletedTask;
        private int pendingSends;
        private long pendingBytes;
        private bool stopped;

        internal Task<bool> Enqueue(Func<bool> send, long byteCount)
        {
            if (send == null)
                throw new ArgumentNullException(nameof(send));
            lock (gate)
            {
                if (stopped)
                    return Task.FromCanceled<bool>(new CancellationToken(true));
                if (byteCount < 0 || pendingSends >= MaxPendingSends ||
                    byteCount > MaxPendingBytes - pendingBytes)
                {
                    return Task.FromException<bool>(new InvalidOperationException(
                        "Serial send queue is full. Wait for pending sends to finish."));
                }

                pendingSends++;
                pendingBytes += byteCount;
                var task = tail.ContinueWith(previous =>
                {
                    // Observe a predecessor failure without preventing the next item.
                    var ignored = previous.Exception;
                    try
                    {
                        lock (gate)
                        {
                            if (stopped)
                                throw new OperationCanceledException();
                        }
                        return send();
                    }
                    finally
                    {
                        lock (gate)
                        {
                            pendingSends--;
                            pendingBytes -= byteCount;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                tail = task;
                return task;
            }
        }

        internal void Stop()
        {
            lock (gate)
                stopped = true;
        }
    }
}
