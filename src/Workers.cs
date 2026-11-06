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
    /// atlas needs before it can open), which a free worker takes before any other. Nothing running
    /// is ever put aside.
    /// A loop's parts (LoopOptions) are only queued for a worker that is idle at that moment, and
    /// idle workers take them before any job; with none idle a part runs at once on the thread
    /// that made it. Under Valheim's Mono a Parallel.For waits for every part it queued to start,
    /// even after its own thread has done all the rows: queued behind long jobs it stalled the
    /// fog for seconds, and with every worker inside a loop at once none ever started (a deadlock
    /// measured offline under Valheim's Mono; .NET Framework does not wait so).
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
        // job run first its parts keep that lane (and pass it on to work they start).
        public static ParallelOptions LoopOptions(CancellationToken cancel = default(CancellationToken)) =>
            new ParallelOptions { MaxDegreeOfParallelism = Count, TaskScheduler = s_first ? s_pool.m_firstParts : s_pool.m_restParts, CancellationToken = cancel };

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

        // A loop's part waiting for the idle worker it was queued for, with the lane it runs in.
        private struct Part
        {
            public Task m_task;
            public Lane m_lane;
        }

        // The threads and their queues. Each lane is a TaskScheduler of its own, since a task
        // may only be run by the scheduler it was queued to.
        private sealed class Pool
        {
            public readonly Lane m_first;
            public readonly Lane m_rest;
            public readonly Lane m_firstParts;
            public readonly Lane m_restParts;
            private readonly object m_lock = new object();
            private readonly Queue<Task> m_firstQueue = new Queue<Task>();
            private readonly Queue<Task> m_restQueue = new Queue<Task>();
            private readonly Queue<Part> m_parts = new Queue<Part>();
            // Workers waiting for work; each queued part has one of them to itself.
            private int m_idle;

            public Pool(int threads)
            {
                m_first = new Lane(this, true, false);
                m_rest = new Lane(this, false, false);
                m_firstParts = new Lane(this, true, true);
                m_restParts = new Lane(this, false, true);
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
                    Monitor.PulseAll(m_lock);
                }
            }

            // Queues a loop's part only if an idle worker is left for it; false when the caller
            // must run it itself.
            public bool AddPart(Task task, Lane lane)
            {
                lock (m_lock)
                {
                    if (m_idle <= m_parts.Count) return false;
                    m_parts.Enqueue(new Part { m_task = task, m_lane = lane });
                    Monitor.PulseAll(m_lock);
                    return true;
                }
            }

            public Task[] Queued(Lane lane)
            {
                lock (m_lock)
                {
                    if (!lane.m_isParts) return (lane.m_isFirst ? m_firstQueue : m_restQueue).ToArray();
                    var tasks = new List<Task>();
                    foreach (Part part in m_parts)
                        if (part.m_lane == lane) tasks.Add(part.m_task);
                    return tasks.ToArray();
                }
            }

            private void Loop()
            {
                s_onWorker = true;
                while (true)
                {
                    Task task;
                    Lane lane;
                    lock (m_lock)
                    {
                        while (m_parts.Count == 0 && m_firstQueue.Count == 0 && m_restQueue.Count == 0)
                        {
                            m_idle++;
                            Monitor.Wait(m_lock);
                            m_idle--;
                        }
                        if (m_parts.Count > 0)
                        {
                            Part part = m_parts.Dequeue();
                            task = part.m_task;
                            lane = part.m_lane;
                        }
                        else if (m_firstQueue.Count > 0)
                        {
                            task = m_firstQueue.Dequeue();
                            lane = m_first;
                        }
                        else
                        {
                            task = m_restQueue.Dequeue();
                            lane = m_rest;
                        }
                    }
                    lane.Execute(task);
                }
            }
        }

        private sealed class Lane : TaskScheduler
        {
            private readonly Pool m_pool;
            public readonly bool m_isFirst;
            // A lane for loops' parts rather than jobs.
            public readonly bool m_isParts;

            public Lane(Pool pool, bool first, bool parts)
            {
                m_pool = pool;
                m_isFirst = first;
                m_isParts = parts;
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

            // A part with no idle worker for it runs here and now, on the thread whose loop made
            // it (it takes the rows that thread would have taken); queued, it could wait behind
            // jobs that run for seconds, with its loop waiting for it to start.
            protected override void QueueTask(Task task)
            {
                if (!m_isParts) m_pool.Add(task, m_isFirst);
                else if (!m_pool.AddPart(task, this)) Execute(task);
            }

            // Parallel.For runs part of its loop on the calling thread; that is allowed on these
            // threads only.
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

            protected override IEnumerable<Task> GetScheduledTasks() => m_pool.Queued(this);
        }
    }
}
