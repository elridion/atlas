using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas
{
    /// <summary>
    /// The atlas's own worker threads: half the cores, at most 4, below normal priority. All of the
    /// atlas's background work (the per-world jobs, close-up tiles and the Parallel.For loops inside
    /// them) runs here, so it never takes every core from Unity's main and render threads and
    /// Valheim's terrain builder, as the shared ThreadPool did right after login.
    /// Work on these threads must never block on another atlas task: with one worker it would wait
    /// on a task queued behind itself. Chain with ContinueWith instead.
    /// </summary>
    internal static class Workers
    {
        public static readonly int Count = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

        private static readonly LimitedScheduler s_scheduler = new LimitedScheduler(Count);

        public static TaskScheduler Scheduler => s_scheduler;

        // Options for a Parallel.For inside atlas work: it stays on these threads.
        public static ParallelOptions LoopOptions(CancellationToken cancel = default(CancellationToken)) =>
            new ParallelOptions { MaxDegreeOfParallelism = Count, TaskScheduler = s_scheduler, CancellationToken = cancel };

        public static Task<T> Run<T>(Func<T> work, CancellationToken cancel = default(CancellationToken)) =>
            Task.Factory.StartNew(work, cancel, TaskCreationOptions.DenyChildAttach, s_scheduler);

        public static Task<TResult> Then<T, TResult>(Task<T> first, Func<T, TResult> work) =>
            first.ContinueWith(done => work(done.Result), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, s_scheduler);

        private sealed class LimitedScheduler : TaskScheduler
        {
            private readonly BlockingCollection<Task> m_queue = new BlockingCollection<Task>();
            [ThreadStatic] private static bool s_onWorker;

            public LimitedScheduler(int threads)
            {
                for (int i = 0; i < threads; i++)
                {
                    var thread = new Thread(Loop)
                    {
                        Name = "Atlas worker " + i,
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                    };
                    thread.Start();
                }
            }

            public override int MaximumConcurrencyLevel => Count;

            private void Loop()
            {
                s_onWorker = true;
                foreach (Task task in m_queue.GetConsumingEnumerable()) TryExecuteTask(task);
            }

            protected override void QueueTask(Task task) => m_queue.Add(task);

            // Parallel.For runs part of its loop on the calling thread, which keeps it from waiting
            // on its own queued parts; that is allowed on these threads only.
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
                s_onWorker && !taskWasPreviouslyQueued && TryExecuteTask(task);

            protected override IEnumerable<Task> GetScheduledTasks() => m_queue.ToArray();
        }
    }
}
