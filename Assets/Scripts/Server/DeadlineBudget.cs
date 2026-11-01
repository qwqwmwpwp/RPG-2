using System;
using System.Diagnostics;
using System.Threading;

namespace RPG4.Server
{
    internal static class DeadlineBudget
    {
        public static void ThrowIfExpired(long deadline, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline) throw new OperationCanceledException(cancellation);
        }
        public static CancellationTokenSource Create(long deadline, CancellationToken cancellation)
        {
            ThrowIfExpired(deadline, cancellation);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            double remaining = (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
            source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(remaining, 600000))));
            return source;
        }
    }
}
