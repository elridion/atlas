using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BepInEx;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Keeps each world's inked canvas on disk (BepInEx/cache/elridion.atlas), so a world is only
    /// drawn again when its pins change. Files are named by world id and checked against the world
    /// seed and the drawing version, and carry the key of the pins drawn in; bump Version whenever the drawing changes, so old sheets are drawn again. The sea's
    /// marks, figures and stand-ins (their boxes, for the fog, and the glyph drawn, for its wash) and the marks
    /// the fog's edge shows or leaves out whole (SheetMarks) are kept in the header, ahead of the
    /// sheet.
    /// </summary>
    internal static class InkCache
    {
        private const int Magic = 0x534C5441; // "ATLS"
        // The development sheets used 23 to 26: the count jumped from 22 to 27.
        private const int Version = 27;
        // A file never holds more sea illustrations than this; more means it is damaged.
        private const int MostSeaMarks = 256;
        // Nor more marks of a kind (trees, glyphs, wave strokes) than this.
        private const int MostMarks = 1 << 22;

        private static string FilePath(long worldUid) =>
            Path.Combine(Paths.CachePath, AtlasPlugin.PluginGuid, $"{worldUid}.ink");

        // Worker thread. Returns null when there is no matching sheet; marks gets the sheet's sea
        // illustrations (InkRecord.m_seaMarks), which the fog needs before the world is painted,
        // pinsKey the key of the pins it was drawn with (SheetPin.Key), and sheetMarks the marks
        // its fog's edge decides on (InkRecord.m_marks).
        public static byte[] Load(long worldUid, int seed, out SeaMark[] marks, out long pinsKey, out SheetMarks sheetMarks)
        {
            marks = null;
            sheetMarks = null;
            pinsKey = 0L;
            string path = FilePath(worldUid);
            if (!File.Exists(path)) return null;
            try
            {
                using (var file = File.OpenRead(path))
                using (var reader = new BinaryReader(file))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return null;
                    if (reader.ReadInt32() != seed || reader.ReadInt32() != InkPainter.CanvasSize) return null;
                    long key = reader.ReadInt64();
                    int count = reader.ReadInt32();
                    if (count < 0 || count > MostSeaMarks) return null;
                    var seaMarks = new SeaMark[count];
                    for (int i = 0; i < count; i++)
                    {
                        seaMarks[i] = new SeaMark
                        {
                            m_west = reader.ReadSingle(), m_east = reader.ReadSingle(), m_south = reader.ReadSingle(), m_north = reader.ReadSingle(),
                            m_kind = reader.ReadInt32(), m_x = reader.ReadSingle(), m_y = reader.ReadSingle(), m_scale = reader.ReadSingle(),
                            m_mirror = reader.ReadBoolean(),
                        };
                        if (seaMarks[i].m_kind < 0 || seaMarks[i].m_kind >= SeaGlyphs.Names.Length) return null;
                    }
                    var read = new SheetMarks { m_key = key };
                    if (!ReadMarks(reader, read.m_trees) || !ReadMarks(reader, read.m_glyphs) || !ReadMarks(reader, read.m_waves)) return null;
                    // Ink coverage, then the body masks (InkPainter.Paint).
                    var ink = new byte[InkPainter.CanvasSize * InkPainter.CanvasSize * InkPainter.SheetPlanes];
                    using (var zip = new GZipStream(file, CompressionMode.Decompress))
                    {
                        int done = 0;
                        while (done < ink.Length)
                        {
                            int got = zip.Read(ink, done, ink.Length - done);
                            if (got <= 0) return null;
                            done += got;
                        }
                    }
                    marks = seaMarks;
                    pinsKey = key;
                    sheetMarks = read;
                    return ink;
                }
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Could not read the cached atlas {path}, drawing it again: {e.Message}");
                return null;
            }
        }

        // Worker thread.
        public static void Save(long worldUid, int seed, byte[] ink, IList<SeaMark> marks, long pinsKey, SheetMarks sheetMarks)
        {
            string path = FilePath(worldUid);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                using (var file = File.Create(temporary))
                {
                    using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(Magic);
                        writer.Write(Version);
                        writer.Write(seed);
                        writer.Write(InkPainter.CanvasSize);
                        writer.Write(pinsKey);
                        writer.Write(marks.Count);
                        foreach (SeaMark mark in marks)
                        {
                            writer.Write(mark.m_west);
                            writer.Write(mark.m_east);
                            writer.Write(mark.m_south);
                            writer.Write(mark.m_north);
                            writer.Write(mark.m_kind);
                            writer.Write(mark.m_x);
                            writer.Write(mark.m_y);
                            writer.Write(mark.m_scale);
                            writer.Write(mark.m_mirror);
                        }
                        WriteMarks(writer, sheetMarks.m_trees);
                        WriteMarks(writer, sheetMarks.m_glyphs);
                        WriteMarks(writer, sheetMarks.m_waves);
                    }
                    using (var zip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                        zip.Write(ink, 0, ink.Length);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Could not cache the atlas at {path}; it will be drawn again next time: {e.Message}");
            }
        }

        // A list of marks: its count, then each one's footprint, order, shape and whether it is
        // thinned. The record group is not kept: close-ups take it from their own record.
        private static void WriteMarks(BinaryWriter writer, List<FogMark> marks)
        {
            writer.Write(marks.Count);
            foreach (FogMark mark in marks)
            {
                writer.Write(mark.m_box.x);
                writer.Write(mark.m_box.y);
                writer.Write(mark.m_box.z);
                writer.Write(mark.m_box.w);
                writer.Write(mark.m_order);
                writer.Write((byte)mark.m_shape);
                writer.Write(mark.m_thinned);
            }
        }

        private static bool ReadMarks(BinaryReader reader, List<FogMark> into)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MostMarks) return false;
            for (int i = 0; i < count; i++)
            {
                var mark = new FogMark
                {
                    m_box = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    m_order = reader.ReadSingle(),
                    m_shape = (MarkShape)reader.ReadByte(),
                    m_thinned = reader.ReadBoolean(),
                    m_group = -1,
                };
                if (mark.m_shape > MarkShape.Box) return false;
                into.Add(mark);
            }
            return true;
        }
    }
}
