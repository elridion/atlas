using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Atlas
{
    /// <summary>
    /// TEMPORARY timing build only (never commit): logs when each stage of a world's atlas is
    /// ready after joining, the main thread's cost of each upload, garbage collections, and the
    /// atlas's per-frame cost until the first opening. Every method is [Conditional], so without
    /// ATLAS_TIMING defined (build with -p:DefineConstants=TRACE%3BATLAS_TIMING) the calls compile
    /// away.
    /// </summary>
    internal static class Timing
    {
        private const string Define = "ATLAS_TIMING";
        // Frames costing more than this (ms) on the main thread are counted and the worst listed.
        private const double SlowFrame = 4.0;

        private static readonly Stopwatch s_since = new Stopwatch();
        private static readonly Stopwatch s_frame = new Stopwatch();
        private static readonly Stopwatch s_block = new Stopwatch();
        private static readonly HashSet<string> s_once = new HashSet<string>();
        private static readonly object s_lock = new object();
        private static bool s_reported;
        private static int s_frames;
        private static int s_slow;
        private static double s_total;
        private static double s_worst;
        private static double s_worstAt;
        private static int s_gcStart;

        [Conditional(Define)]
        public static void Start()
        {
            lock (s_lock)
            {
                s_since.Restart();
                s_once.Clear();
            }
            s_reported = false;
            s_frames = 0;
            s_slow = 0;
            s_total = 0.0;
            s_worst = 0.0;
            s_gcStart = GC.CollectionCount(0);
            Log($"world joined, timing from here ({Workers.Count} workers, {Environment.ProcessorCount} logical CPUs)");
        }

        // A stage reached, logged the first time only. Any thread.
        [Conditional(Define)]
        public static void Once(string stage)
        {
            lock (s_lock)
            {
                if (!s_since.IsRunning || !s_once.Add(stage)) return;
            }
            Log(stage);
        }

        // A stage logged every time. Any thread.
        [Conditional(Define)]
        public static void Mark(string stage)
        {
            if (!s_since.IsRunning) return;
            Log(stage);
        }

        [Conditional(Define)]
        public static void BlockBegin() => s_block.Restart();

        // A main-thread block's cost, logged every time.
        [Conditional(Define)]
        public static void BlockEnd(string what)
        {
            s_block.Stop();
            if (!s_since.IsRunning) return;
            Log($"{what}: {s_block.Elapsed.TotalMilliseconds:F1} ms on the main thread");
        }

        // A frame over this (ms) logs where its time went (Step) and the collections in it.
        private const double BrokenFrame = 16.0;
        private static readonly List<string> s_steps = new List<string>();
        private static double s_stepAt;
        private static int s_frameGc;

        [Conditional(Define)]
        public static void FrameBegin()
        {
            s_steps.Clear();
            s_stepAt = 0.0;
            s_frameGc = GC.CollectionCount(0);
            s_frame.Restart();
        }

        // Main thread: the time since the last step this frame, kept when over a millisecond.
        [Conditional(Define)]
        public static void Step(string what)
        {
            double now = s_frame.Elapsed.TotalMilliseconds;
            if (now - s_stepAt > 1.0) s_steps.Add($"{what} {now - s_stepAt:F1}");
            s_stepAt = now;
        }

        // The atlas's cost this frame, summed until the first opening, then reported once.
        [Conditional(Define)]
        public static void FrameEnd()
        {
            s_frame.Stop();
            if (!s_since.IsRunning) return;
            double ms = s_frame.Elapsed.TotalMilliseconds;
            if (ms > BrokenFrame)
                Log($"slow frame {ms:F1} ms, {GC.CollectionCount(0) - s_frameGc} collections in it: {string.Join(", ", s_steps.ToArray())}");
            if (s_reported) return;
            s_frames++;
            s_total += ms;
            if (ms > SlowFrame) s_slow++;
            if (ms > s_worst)
            {
                s_worst = ms;
                s_worstAt = s_since.Elapsed.TotalSeconds;
            }
        }

        // The first opening: the frame summary since joining.
        [Conditional(Define)]
        public static void Opened()
        {
            if (!s_since.IsRunning || s_reported) return;
            s_reported = true;
            Log($"first opening. Atlas main-thread cost since joining: {s_frames} frames, {s_total:F0} ms in all, "
                + $"{(s_frames > 0 ? s_total / s_frames : 0.0):F2} ms a frame on average, {s_slow} frames over {SlowFrame} ms, "
                + $"worst {s_worst:F1} ms at {s_worstAt:F2} s");
        }

        // A paint's stages (InkPainter.Run): each Stage logs the time since the last one on this
        // thread and how much of it went into filling pixels (Raster), so a record-only paint can
        // be costed. Per thread, since close-up tiles draw on other workers at the same time.
        [ThreadStatic] private static long t_stageAt;
        [ThreadStatic] private static long t_paintAt;
        [ThreadStatic] private static double t_stageRaster;
        [ThreadStatic] private static int t_rasterDepth;
        [ThreadStatic] private static long t_rasterAt;
        [ThreadStatic] private static double t_raster;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        [Conditional(Define)]
        public static void StagesBegin()
        {
            t_paintAt = t_stageAt = Stopwatch.GetTimestamp();
            t_raster = t_stageRaster = 0.0;
        }

        [Conditional(Define)]
        public static void Stage(string what)
        {
            long now = Stopwatch.GetTimestamp();
            if (s_since.IsRunning)
                Log($"paint stage {what}: {Ms(now - t_stageAt):F0} ms, raster {t_raster - t_stageRaster:F0} ms");
            t_stageAt = now;
            t_stageRaster = t_raster;
        }

        [Conditional(Define)]
        public static void StagesEnd()
        {
            if (s_since.IsRunning)
                Log($"paint total: {Ms(Stopwatch.GetTimestamp() - t_paintAt):F0} ms, raster {t_raster:F0} ms");
        }

        // Around InkCanvas's pixel loops; nested calls count once.
        [Conditional(Define)]
        public static void RasterBegin()
        {
            if (t_rasterDepth++ == 0) t_rasterAt = Stopwatch.GetTimestamp();
        }

        [Conditional(Define)]
        public static void RasterEnd()
        {
            if (--t_rasterDepth == 0) t_raster += Ms(Stopwatch.GetTimestamp() - t_rasterAt);
        }

        // Every line carries the time since joining and the collections since then (gen 0, which
        // under Mono's Boehm GC counts every full collection).
        private static void Log(string text) =>
            Jotunn.Logger.LogInfo($"[atlas timing] {s_since.Elapsed.TotalSeconds,7:F2} s  gc {GC.CollectionCount(0) - s_gcStart,3}  {text}");
    }
}
