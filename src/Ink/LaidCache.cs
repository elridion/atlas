using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Keeps each world's last fog band and laid ink texture on disk beside the cached sheet
    /// (<c>worldUid.fog</c>, <c>worldUid.laid</c>), so a login whose inputs did not change loads
    /// them instead of laying them again (about 0.5 s and 0.65 s on the workers in game). Each
    /// file carries a SHA-256 key of everything its content was made from (Key*), checked
    /// against the key worked out at login: any other key, format, drawing version, seed or size,
    /// or a read that fails, lays them as before. The band does not depend on the sheet, so its
    /// marks are judged again on it (Paper.FogOfBand, a few ms); the ink's key holds the band's
    /// and the sheet's pins key. Written raw, not gzipped: gzip read back the ink as slowly as
    /// laying it (docs/performance.md). The ink leaves out the 32² blocks a lay left clear under
    /// opaque fog, which hold nothing on any level up to Paper.KeptLevel.
    /// </summary>
    internal static class LaidCache
    {
        private const int BandMagic = 0x44424C41; // "ALBD"
        private const int InkMagic = 0x4B494C41; // "ALIK"
        // The files' own layout; InkCache.Version is checked as well, as the drawing decides them.
        private const int Format = 1;
        private const int KeyBytes = 32;
        private const int IoBuffer = 1 << 20;

        /// <summary>A fog band read back from disk, with the key it was laid for.</summary>
        public sealed class Band
        {
            public byte[] m_key;
            public byte[] m_opacity;
            public byte[] m_thin;
            public byte[] m_unexplored;

            // A new band for Paper.FogOfBand, which fills in the rest; these planes are only read.
            public FogBase Base(Snapshot snapshot) =>
                new FogBase { m_opacity = m_opacity, m_thin = m_thin, m_unexplored = m_unexplored, m_snapshot = snapshot };
        }

        /// <summary>A laid ink read back from disk (Paper.LaidInk's pixels and kept level), with its key.</summary>
        public sealed class Ink
        {
            public byte[] m_key;
            public byte[] m_data;
            public byte[] m_coarse;
        }

        public static string BandPath(long worldUid) => Path.Combine(Paths.CachePath, AtlasPlugin.PluginGuid, $"{worldUid}.fog");

        public static string InkPath(long worldUid) => Path.Combine(Paths.CachePath, AtlasPlugin.PluginGuid, $"{worldUid}.laid");

        // Any thread. What Paper.Fog's band reads: the snapshot's explored land (size, pixel
        // size, every bit; the sea illustrations are judged on it too), the glimpses and the
        // Glimpse setting, under this layout and drawing version and the world's seed.
        public static byte[] BandKey(int seed, Snapshot snapshot, IList<Vector2> glimpses, bool glimpseBand)
        {
            using (var key = new Hasher())
            {
                key.Add(BandMagic);
                key.Add(Format);
                key.Add(InkCache.Version);
                key.Add(seed);
                key.Add(Paper.FogSize);
                key.Add(InkPainter.CanvasSize);
                key.Add(glimpseBand ? 1 : 0);
                key.Add(glimpses?.Count ?? -1);
                if (glimpses != null)
                {
                    foreach (Vector2 at in glimpses)
                    {
                        key.Add(at.x);
                        key.Add(at.y);
                    }
                }
                if (snapshot?.m_explored == null) key.Add(-1);
                else
                {
                    int length = snapshot.m_explored.Length;
                    var bits = new byte[(length + 7) / 8];
                    snapshot.m_explored.CopyTo(bits, 0);
                    // The last byte's bits past the length are not part of the snapshot.
                    if (length % 8 != 0) bits[bits.Length - 1] &= (byte)((1 << (length % 8)) - 1);
                    key.Add(snapshot.m_size);
                    key.Add(snapshot.m_pixelSize);
                    key.Add(length);
                    key.Add(bits);
                }
                return key.Done();
            }
        }

        // Any thread. What a whole ink lay reads (Paper.LayInk): the sheet, fixed by its pins key
        // under the seed and drawing version (only sheets drawn with the world's alt biome groves
        // are cached), its sea illustrations, the band (bandKey) and the decisions judged on it
        // for the sheet's marks, which follow from the band and the sheet.
        public static byte[] InkKey(byte[] bandKey, int seed, long pinsKey, IList<SeaMark> seaMarks, SheetMarks sheetMarks)
        {
            using (var key = new Hasher())
            {
                key.Add(InkMagic);
                key.Add(Format);
                key.Add(InkCache.Version);
                key.Add(seed);
                key.Add(InkPainter.CanvasSize);
                key.Add(bandKey);
                key.Add(pinsKey);
                key.Add(sheetMarks?.m_key ?? 0L);
                key.Add(sheetMarks?.m_trees.Count ?? -1);
                key.Add(sheetMarks?.m_glyphs.Count ?? -1);
                key.Add(sheetMarks?.m_waves.Count ?? -1);
                key.Add(seaMarks?.Count ?? -1);
                if (seaMarks != null)
                {
                    foreach (SeaMark mark in seaMarks)
                    {
                        key.Add(mark.m_west);
                        key.Add(mark.m_east);
                        key.Add(mark.m_south);
                        key.Add(mark.m_north);
                        key.Add(mark.m_kind);
                        key.Add(mark.m_x);
                        key.Add(mark.m_y);
                        key.Add(mark.m_scale);
                        key.Add(mark.m_mirror ? 1 : 0);
                    }
                }
                return key.Done();
            }
        }

        public static bool SameKey(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // Worker thread. The band on disk, or null when there is none for this seed, version and
        // size or it cannot be read.
        public static Band LoadBand(string path, int seed)
        {
            if (!File.Exists(path)) return null;
            int plane = Paper.FogSize * Paper.FogSize;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer))
                using (var reader = new BinaryReader(file))
                {
                    if (!Header(reader, BandMagic, seed, Paper.FogSize)) return null;
                    if (file.Length != file.Position + KeyBytes + 3L * plane) return null;
                    var band = new Band { m_key = reader.ReadBytes(KeyBytes), m_opacity = new byte[plane], m_thin = new byte[plane], m_unexplored = new byte[plane] };
                    if (!ReadFully(file, band.m_opacity, 0, plane) || !ReadFully(file, band.m_thin, 0, plane) || !ReadFully(file, band.m_unexplored, 0, plane)) return null;
                    return band;
                }
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Could not read the cached atlas fog {path}, laying it again: {e.Message}");
                return null;
            }
        }

        // Worker thread. The band's planes are never written once laid, so this may run while
        // the fog is in use. order is the ticket taken when the save was queued (Order).
        public static void SaveBand(string path, int seed, byte[] key, FogBase band, int order)
        {
            if (key == null || band?.m_opacity == null || band.m_thin == null || band.m_unexplored == null) return;
            Write(path, order, "fog", (file, writer) =>
            {
                WriteHeader(writer, BandMagic, seed, Paper.FogSize);
                writer.Write(key);
                writer.Flush();
                file.Write(band.m_opacity, 0, band.m_opacity.Length);
                file.Write(band.m_thin, 0, band.m_thin.Length);
                file.Write(band.m_unexplored, 0, band.m_unexplored.Length);
            });
        }

        // Worker thread. The laid ink on disk, or null when there is none for this seed, version
        // and size or it cannot be read. The blocks left out are clear (a new array's zeros).
        public static Ink LoadInk(string path, int seed)
        {
            if (!File.Exists(path)) return null;
            int blocks = InkPainter.CanvasSize / Paper.InkBlock;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer))
                using (var reader = new BinaryReader(file))
                {
                    if (!Header(reader, InkMagic, seed, InkPainter.CanvasSize)) return null;
                    byte[] key = reader.ReadBytes(KeyBytes);
                    byte[] stored = reader.ReadBytes(blocks * blocks);
                    if (key.Length != KeyBytes || stored.Length != blocks * blocks) return null;
                    int count = 0;
                    foreach (byte b in stored)
                    {
                        if (b > 1) return null;
                        count += b;
                    }
                    int[] offsets = Paper.LevelOffsets();
                    int tail = Paper.InkBytes - offsets[Paper.KeptLevel + 1];
                    if (file.Length != file.Position + blocks * blocks * 4L + (long)count * BlockBytes() + tail) return null;
                    var ink = new Ink { m_key = key, m_coarse = new byte[blocks * blocks * 4], m_data = new byte[Paper.InkBytes] };
                    if (!ReadFully(file, ink.m_coarse, 0, ink.m_coarse.Length)) return null;
                    bool ok = true;
                    Runs(stored, offsets, (at, length) => ok = ok && ReadFully(file, ink.m_data, at, length));
                    if (!ok || !ReadFully(file, ink.m_data, offsets[Paper.KeptLevel + 1], tail)) return null;
                    return ink;
                }
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Could not read the cached atlas ink {path}, laying it again: {e.Message}");
                return null;
            }
        }

        // Worker thread. laid must not be laid into again until this returns (the caller hands
        // it back after); order as for SaveBand. A block the lay left clear under opaque fog (LaidInk.m_fogged; its
        // first level clear, and so every level up to KeptLevel, made from it alone) is left out.
        public static void SaveInk(string path, int seed, byte[] key, Paper.LaidInk laid, int order)
        {
            if (key == null || laid?.m_data == null || laid.m_data.Length != Paper.InkBytes || laid.m_coarse == null) return;
            int n = InkPainter.CanvasSize, block = Paper.InkBlock, blocks = n / block;
            if (laid.m_coarse.Length != blocks * blocks * 4) return;
            byte[] data = laid.m_data;
            var stored = new byte[blocks * blocks];
            for (int b = 0; b < stored.Length; b++)
            {
                stored[b] = 1;
                if (laid.m_fogged == null || !laid.m_fogged[b]) continue;
                // Checked rather than trusted, as a block left out is read back clear.
                bool clear = true;
                int x0 = b % blocks * block, y0 = b / blocks * block;
                for (int y = y0; y < y0 + block && clear; y++)
                {
                    int from = (y * n + x0) * 4;
                    for (int i = from; i < from + block * 4; i++)
                    {
                        if (data[i] == 0) continue;
                        clear = false;
                        break;
                    }
                }
                if (clear) stored[b] = 0;
            }
            int[] offsets = Paper.LevelOffsets();
            Write(path, order, "ink", (file, writer) =>
            {
                WriteHeader(writer, InkMagic, seed, n);
                writer.Write(key);
                writer.Write(stored);
                writer.Flush();
                file.Write(laid.m_coarse, 0, laid.m_coarse.Length);
                Runs(stored, offsets, (at, length) => file.Write(data, at, length));
                file.Write(data, offsets[Paper.KeptLevel + 1], data.Length - offsets[Paper.KeptLevel + 1]);
            });
        }

        // A stored block's bytes over the levels up to KeptLevel.
        private static int BlockBytes()
        {
            int bytes = 0;
            for (int k = 0; k <= Paper.KeptLevel; k++) bytes += (Paper.InkBlock >> k) * (Paper.InkBlock >> k) * 4;
            return bytes;
        }

        // Visits the stored blocks' texels on the levels up to KeptLevel as runs of a texel row
        // (offset and length in a laid ink's bytes): level by level, rows from the south, the
        // stored blocks side by side in a row of blocks taken as one run.
        private static void Runs(byte[] stored, int[] offsets, Action<int, int> visit)
        {
            int n = InkPainter.CanvasSize, blocks = n / Paper.InkBlock;
            var runs = new List<int>[blocks];
            for (int by = 0; by < blocks; by++)
            {
                runs[by] = new List<int>();
                for (int bx = 0; bx < blocks; bx++)
                {
                    if (stored[by * blocks + bx] == 0) continue;
                    int end = bx;
                    while (end < blocks && stored[by * blocks + end] != 0) end++;
                    runs[by].Add(bx);
                    runs[by].Add(end);
                    bx = end;
                }
            }
            for (int k = 0; k <= Paper.KeptLevel; k++)
            {
                int size = n >> k, side = Paper.InkBlock >> k;
                for (int y = 0; y < size; y++)
                {
                    List<int> row = runs[y / side];
                    for (int r = 0; r < row.Count; r += 2)
                        visit(offsets[k] + (y * size + row[r] * side) * 4, (row[r + 1] - row[r]) * side * 4);
                }
            }
        }

        private static bool Header(BinaryReader reader, int magic, int seed, int size) =>
            reader.ReadInt32() == magic && reader.ReadInt32() == Format && reader.ReadInt32() == InkCache.Version
            && reader.ReadInt32() == seed && reader.ReadInt32() == size;

        private static void WriteHeader(BinaryWriter writer, int magic, int seed, int size)
        {
            writer.Write(magic);
            writer.Write(Format);
            writer.Write(InkCache.Version);
            writer.Write(seed);
            writer.Write(size);
        }

        // Counts the writes asked for (Order, on the main thread), so of two writes of one file
        // running at once the later one asked for is what stays on disk.
        private static int s_order;
        private static readonly object s_writeLock = new object();
        private static readonly Dictionary<string, int> s_written = new Dictionary<string, int>();

        // Main thread. The ticket a save passes, taken when the save is queued.
        public static int Order() => ++s_order;

        // Written beside the file and moved over it, so a write cut short leaves no half file.
        // One write at a time, and none after a later one of the same file.
        private static void Write(string path, int order, string what, Action<FileStream, BinaryWriter> body)
        {
            lock (s_writeLock)
            {
                if (s_written.TryGetValue(path, out int done) && done > order) return;
                s_written[path] = order;
                WriteNow(path, what, body);
            }
        }

        private static void WriteNow(string path, string what, Action<FileStream, BinaryWriter> body)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, IoBuffer))
                using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
                    body(file, writer);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Could not cache the atlas {what} at {path}; it will be laid again next time: {e.Message}");
            }
        }

        private static bool ReadFully(Stream stream, byte[] into, int offset, int count)
        {
            while (count > 0)
            {
                int got = stream.Read(into, offset, count);
                if (got <= 0) return false;
                offset += got;
                count -= got;
            }
            return true;
        }

        // SHA-256 over the inputs, each scalar in little-endian bytes and each array after its length.
        private sealed class Hasher : IDisposable
        {
            private readonly SHA256 m_sha = SHA256.Create();
            private readonly byte[] m_word = new byte[8];

            public void Add(int value)
            {
                for (int i = 0; i < 4; i++) m_word[i] = (byte)(value >> (8 * i));
                m_sha.TransformBlock(m_word, 0, 4, null, 0);
            }

            public void Add(long value)
            {
                for (int i = 0; i < 8; i++) m_word[i] = (byte)(value >> (8 * i));
                m_sha.TransformBlock(m_word, 0, 8, null, 0);
            }

            public void Add(float value) => Add(BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

            public void Add(byte[] bytes)
            {
                Add(bytes?.Length ?? -1);
                if (bytes != null && bytes.Length > 0) m_sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            public byte[] Done()
            {
                m_sha.TransformFinalBlock(m_word, 0, 0);
                return m_sha.Hash;
            }

            public void Dispose() => ((IDisposable)m_sha).Dispose();
        }
    }
}
