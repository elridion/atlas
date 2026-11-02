using System;
using System.IO;
using System.IO.Compression;
using BepInEx;

namespace Atlas
{
    /// <summary>
    /// Keeps each world's inked canvas on disk (BepInEx/cache/elridion.atlas), so a world is only
    /// drawn once. Files are named by world id and checked against the world seed and the drawing
    /// version; bump Version whenever the drawing changes, so old sheets are drawn again.
    /// </summary>
    internal static class InkCache
    {
        private const int Magic = 0x534C5441; // "ATLS"
        private const int Version = 23;

        private static string FilePath(long worldUid) =>
            Path.Combine(Paths.CachePath, AtlasPlugin.PluginGuid, $"{worldUid}.ink");

        // Worker thread. Returns null when there is no matching sheet.
        public static byte[] Load(long worldUid, int seed)
        {
            string path = FilePath(worldUid);
            if (!File.Exists(path)) return null;
            try
            {
                using (var file = File.OpenRead(path))
                using (var reader = new BinaryReader(file))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return null;
                    if (reader.ReadInt32() != seed || reader.ReadInt32() != InkPainter.CanvasSize) return null;
                    var ink = new byte[InkPainter.CanvasSize * InkPainter.CanvasSize];
                    using (var zip = new GZipStream(file, CompressionMode.Decompress))
                    {
                        int read = 0;
                        while (read < ink.Length)
                        {
                            int got = zip.Read(ink, read, ink.Length - read);
                            if (got <= 0) return null;
                            read += got;
                        }
                    }
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
        public static void Save(long worldUid, int seed, byte[] ink)
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
                    }
                    using (var zip = new GZipStream(file, CompressionLevel.Fastest))
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
    }
}
