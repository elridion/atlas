using System;
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
    /// Jobs run in the order queued, except those started with RunFirst or ThenFirst (the work the
    /// atlas needs before it can open), which a free worker takes before any other; a loop run
    /// inside one (LoopOptions) gets its parts ahead too. Nothing running is ever put aside.
    /// Work on these threads must never block on another atlas task: with one worker it would wait
    /// on a task queued behind itself. Chain with ContinueWith instead.
    /// </summary>
    internal static class Workers
    {
        public static readonly int Count = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

        private static readonly Pool s_pool = new Pool(Count);

        // Set on a worker while it runs a job of the first lane, so loops inside take that lane.
        [ThreadStatic] private static bool s_first;
        [ThreadStatic] private static bool s_onWorker;

        public static TaskScheduler Scheduler => s_pool.m_rest;

        // Options for a Parallel.For inside atlas work: it stays on these threads, and inside a
        // job run first its parts are taken first as well (they are tasks queued on the lane given
        // here, and a job of the opening waits for its slowest part).
        public static ParallelOptions LoopOptions(CancellationToken cancel = default(CancellationToken)) =>
            new ParallelOptions { MaxDegreeOfParallelism = Count, TaskScheduler = s_first ? s_pool.m_first : s_pool.m_rest, CancellationToken = cancel };

        public static Task<T> Run<T>(Func<T> work, CancellationToken cancel = default(CancellationToken)) =>
            Task.Factory.StartNew(work, cancel, TaskCreationOptions.DenyChildAttach, s_pool.m_rest);

        // A job the atlas needs before it can open (its fog, its ink, the cached sheet): queued
        // ahead of every job started with Run.
        public static Task<T> RunFirst<T>(Func<T> work, CancellationToken cancel = default(CancellationToken)) =>
            Task.Factory.StartNew(work, cancel, TaskCreationOptions.DenyChildAttach, s_pool.m_first);

        public static Task<TResult> Then<T, TResult>(Task<T> first, Func<T, TResult> work) =>
            first.ContinueWith(done => work(done.Result), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, s_pool.m_rest);

        // Then, queued ahead as RunFirst is.
        public static Task<TResult> ThenFirst<T, TResult>(Task<T> first, Func<T, TResult> work) =>
            first.ContinueWith(done => work(done.Result), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, s_pool.m_first);

        // The threads and their two queues. Each lane is a TaskScheduler of its own, since a task
        // may only be run by the scheduler it was queued to.
        private sealed class Pool
        {
            public readonly Lane m_first;
            public readonly Lane m_rest;
            private readonly object m_lock = new object();
            private readonly Queue<Task> m_firstQueue = new Queue<Task>();
            private readonly Queue<Task> m_restQueue = new Queue<Task>();

            public Pool(int threads)
            {
                m_first = new Lane(this, true);
                m_rest = new Lane(this, false);
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

            public void Add(Task task, bool first)
            {
                lock (m_lock)
                {
                    (first ? m_firstQueue : m_restQueue).Enqueue(task);
                    Monitor.Pulse(m_lock);
                }
            }

            public Task[] Queued(bool first)
            {
                lock (m_lock) return (first ? m_firstQueue : m_restQueue).ToArray();
            }

            private void Loop()
            {
                s_onWorker = true;
                while (true)
                {
                    Task task;
                    bool first;
                    lock (m_lock)
                    {
                        while (m_firstQueue.Count == 0 && m_restQueue.Count == 0) Monitor.Wait(m_lock);
                        first = m_firstQueue.Count > 0;
                        task = first ? m_firstQueue.Dequeue() : m_restQueue.Dequeue();
                    }
                    (first ? m_first : m_rest).Execute(task);
                }
            }
        }

        private sealed class Lane : TaskScheduler
        {
            private readonly Pool m_pool;
            private readonly bool m_isFirst;

            public Lane(Pool pool, bool first)
            {
                m_pool = pool;
                m_isFirst = first;
            }

            public override int MaximumConcurrencyLevel => Count;

            public void Execute(Task task)
            {
                bool was = s_first;
                s_first = m_isFirst;
                try
                {
                    TryExecuteTask(task);
                }
                finally
                {
                    s_first = was;
                }
            }

            protected override void QueueTask(Task task) => m_pool.Add(task, m_isFirst);

            // Parallel.For runs part of its loop on the calling thread, which keeps it from waiting
            // on its own queued parts; that is allowed on these threads only.
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                if (!s_onWorker || taskWasPreviouslyQueued) return false;
                bool was = s_first;
                s_first = m_isFirst;
                try
                {
                    return TryExecuteTask(task);
                }
                finally
                {
                    s_first = was;
                }
            }

            protected override IEnumerable<Task> GetScheduledTasks() => m_pool.Queued(m_isFirst);
        }
    }
}
