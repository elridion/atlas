using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Atlas
{
    /// <summary>
    /// Close-ups as fixed tiles, as web maps do: TilePixels squared tiles at the magnification
    /// that keeps about one texel per screen pixel (2x, 4x or 8x), one level on screen at a time,
    /// mipmapped, each drawn on its own worker (DetailTile.Render) and kept as a
    /// texture. The view fills in piece by piece, each tile fading in, panning back is instant,
    /// and tiles are drawn ahead of need: a ring round the view, and round the pointer before the
    /// view is zoomed in far enough to show them. Main thread, except the rendering.
    /// </summary>
    internal sealed class DetailTiles
    {
        private const int TilePixels = 256;
        private static readonly int[] s_scales = { 2, 4, 8 };
        // Screen pixels per sheet pixel from which tiles are shown; below it the sheet is sharp
        // enough, and at the finest level a wider view would take hundreds of tiles.
        private const float DetailFrom = 1.5f;
        // Ground samples every GridPixels tile pixels (the nearest SampleCache spacing).
        private const float GridPixels = 3f;
        // At about 340 KB a tile with its mipmaps, about 110 MB of textures; the least recently
        // used beyond go.
        private const int Capacity = 320;
        // Textures uploaded per frame, so a burst of finished tiles never stalls a frame.
        private const int UploadsPerFrame = 3;
        // Tiles drawn ahead round the pointer while zoomed out, at most.
        private const int AheadTiles = 48;
        private const float FadeSeconds = 0.2f;

        private sealed class Tile
        {
            public int m_level;
            public RectInt m_region;
            public Texture2D m_texture;
            public RawImage m_image;
            public long m_used;
            // How far it has faded in, 0 to 1.
            public float m_shown;
        }

        private sealed class Done
        {
            public long m_key;
            public int m_level;
            public RectInt m_region;
            public byte[] m_rgba;
        }

        private readonly Dictionary<long, Tile> m_tiles = new Dictionary<long, Tile>();
        private readonly Dictionary<long, Task<byte[]>> m_running = new Dictionary<long, Task<byte[]>>();
        private readonly Dictionary<long, RectInt> m_runningRegion = new Dictionary<long, RectInt>();
        private readonly Queue<Done> m_ready = new Queue<Done>();
        private readonly List<long> m_wanted = new List<long>(256);
        private readonly HashSet<long> m_wantedSet = new HashSet<long>();
        private readonly List<long> m_finished = new List<long>();
        private readonly RectTransform[] m_layers = new RectTransform[3];
        private RectTransform m_parent;
        private CancellationTokenSource m_cancel = new CancellationTokenSource();
        private long m_frame;
        private bool m_broken;
        // The level on screen, or -1 below DetailFrom.
        private int m_shownLevel = -1;

        private InkRecord m_record;
        private Paper.Wash m_paper;
        private WorldSource m_world;
        private SampleCache m_samples;

        public bool Ready => m_record != null && m_paper != null && m_world != null && m_samples != null && !m_broken;

        public void SetSource(InkRecord record, Paper.Wash paper, WorldSource world, SampleCache samples)
        {
            m_record = record;
            m_paper = paper;
            m_world = world;
            m_samples = samples;
        }

        // Forgets everything, for a new world. Running tiles are cancelled and their results dropped.
        public void Clear()
        {
            m_cancel.Cancel();
            m_cancel.Dispose();
            m_cancel = new CancellationTokenSource();
            foreach (Tile tile in m_tiles.Values) Destroy(tile);
            m_tiles.Clear();
            m_running.Clear();
            m_runningRegion.Clear();
            m_ready.Clear();
            m_parent = null;
            m_broken = false;
            m_shownLevel = -1;
            m_record = null;
            m_paper = null;
            m_world = null;
            m_samples = null;
        }

        public void Update(AtlasView view)
        {
            m_frame++;
            Harvest();
            RectTransform parent = view.DetailLayer;
            if (parent != m_parent) Rebuild(parent);
            Upload();
            if (!view.IsOpen || !m_parent || !Ready) return;
            if (!view.ViewRegion(out RectInt visible, out float onScreen, out Vector2 pointer)) return;

            // The level that keeps about one tile texel per screen pixel at this zoom. The finest
            // level the view can reach at every zoom took thousands of tiles above 1080p, past the
            // cap. Levels never show mixed (tiles switching between levels as they came in looked
            // scratchy): on a zoom across levels the shown one stays until the new one covers the
            // view, then they swap whole.
            bool detail = onScreen >= DetailFrom;
            int level = Level(detail ? onScreen : DetailFrom);
            if (!detail) m_shownLevel = -1;
            else if (m_shownLevel != level && (m_shownLevel < 0 || Covers(level, visible))) Swap(level, visible);
            Show(detail ? m_shownLevel : -1, visible);
            Fade();

            // What to draw, most wanted first: the view from the pointer outward and a ring of one
            // tile round it; zoomed out, the part round the pointer a zoom to DetailFrom shows.
            m_wanted.Clear();
            m_wantedSet.Clear();
            int size = TileSize(level);
            if (detail)
            {
                Want(level, visible, pointer, int.MaxValue);
                Want(level, new RectInt(visible.x - size, visible.y - size, visible.width + 2 * size, visible.height + 2 * size), pointer, int.MaxValue);
            }
            else
            {
                // Zooming in keeps the point under the pointer still, so that is where it lands.
                float shrink = onScreen / DetailFrom;
                var ahead = new RectInt(
                    Mathf.RoundToInt(pointer.x - (pointer.x - visible.x) * shrink),
                    Mathf.RoundToInt(pointer.y - (pointer.y - visible.y) * shrink),
                    Mathf.CeilToInt(visible.width * shrink), Mathf.CeilToInt(visible.height * shrink));
                Want(level, ahead, pointer, AheadTiles);
            }
            Start();
            Trim();
        }

        // The coarsest level at least as fine as the screen, so tiles are never magnified.
        private static int Level(float onScreen)
        {
            for (int i = 0; i < s_scales.Length; i++)
                if (s_scales[i] >= onScreen) return i;
            return s_scales.Length - 1;
        }

        private static int TileSize(int level) => TilePixels / s_scales[level];

        // Whether every tile of a level touching the visible part of the sheet is uploaded.
        private bool Covers(int level, RectInt visible)
        {
            int size = TileSize(level);
            int x0 = Mathf.Max(0, visible.xMin / size), x1 = Mathf.Min(InkPainter.CanvasSize / size - 1, (visible.xMax - 1) / size);
            int y0 = Mathf.Max(0, visible.yMin / size), y1 = Mathf.Min(InkPainter.CanvasSize / size - 1, (visible.yMax - 1) / size);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (!m_tiles.ContainsKey(Key(level, x, y))) return false;
            return true;
        }

        // Shows a new level. Swapping for a level already on screen, its tiles come in at full
        // strength, since fading them in over the sheet would flash the coarser sheet through.
        private void Swap(int level, RectInt visible)
        {
            bool replacing = m_shownLevel >= 0;
            m_shownLevel = level;
            if (!replacing) return;
            foreach (Tile tile in m_tiles.Values)
            {
                if (tile.m_level != level || !tile.m_region.Overlaps(visible) || !tile.m_image) continue;
                tile.m_shown = 1f;
                tile.m_image.color = Color.white;
            }
        }

        private static long Key(int level, int x, int y) => ((long)level << 40) | ((long)(x & 0xFFFFF) << 20) | (long)(y & 0xFFFFF);

        // Adds the tiles of a level touching a sheet rectangle, nearest the pointer first, up to limit.
        private void Want(int level, RectInt area, Vector2 pointer, int limit)
        {
            int size = TileSize(level);
            int x0 = Mathf.Max(0, area.xMin / size), x1 = Mathf.Min(InkPainter.CanvasSize / size - 1, (area.xMax - 1) / size);
            int y0 = Mathf.Max(0, area.yMin / size), y1 = Mathf.Min(InkPainter.CanvasSize / size - 1, (area.yMax - 1) / size);
            if (x1 < x0 || y1 < y0) return;
            int first = m_wanted.Count;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    long key = Key(level, x, y);
                    if (m_wantedSet.Add(key)) m_wanted.Add(key);
                }
            }
            int count = m_wanted.Count - first;
            if (count <= 1) return;
            var range = m_wanted.GetRange(first, count);
            range.Sort((a, b) => Distance(a, pointer).CompareTo(Distance(b, pointer)));
            m_wanted.RemoveRange(first, count);
            for (int i = 0; i < range.Count; i++)
            {
                if (i < limit) m_wanted.Add(range[i]);
                else m_wantedSet.Remove(range[i]);
            }
        }

        private static float Distance(long key, Vector2 pointer)
        {
            RectInt region = Region(key);
            return (region.center - pointer).sqrMagnitude;
        }

        private static RectInt Region(long key)
        {
            int level = (int)(key >> 40);
            int size = TileSize(level);
            int x = (int)((key >> 20) & 0xFFFFF), y = (int)(key & 0xFFFFF);
            return new RectInt(x * size, y * size, size, size);
        }

        // Starts the most wanted tiles not drawn yet, as many as there are workers.
        private void Start()
        {
            int workers = Mathf.Clamp(Environment.ProcessorCount / 2, 1, 4);
            foreach (long key in m_wanted)
            {
                if (m_running.Count >= workers) break;
                if (m_tiles.TryGetValue(key, out Tile tile))
                {
                    tile.m_used = m_frame;
                    continue;
                }
                if (m_running.ContainsKey(key) || Queued(key)) continue;
                int level = (int)(key >> 40);
                RectInt region = Region(key);
                InkRecord record = m_record;
                Paper.Wash paper = m_paper;
                WorldSource world = m_world;
                SampleCache samples = m_samples;
                int scale = s_scales[level];
                CancellationToken cancel = m_cancel.Token;
                m_running[key] = Task.Run(() => WithMips(DetailTile.Render(record, paper, world, samples, region, scale, GridPixels, 1, cancel)), cancel);
                m_runningRegion[key] = region;
            }
            foreach (long key in m_wanted)
                if (m_tiles.TryGetValue(key, out Tile tile)) tile.m_used = m_frame;
        }

        private bool Queued(long key)
        {
            foreach (Done done in m_ready)
                if (done.m_key == key) return true;
            return false;
        }

        // Collects finished tiles. If the world generator fails, close-ups fall back to the
        // minimap's data once; another failure gives up on close-ups.
        private void Harvest()
        {
            m_finished.Clear();
            foreach (KeyValuePair<long, Task<byte[]>> entry in m_running)
                if (entry.Value.IsCompleted) m_finished.Add(entry.Key);
            foreach (long key in m_finished)
            {
                Task<byte[]> task = m_running[key];
                RectInt region = m_runningRegion[key];
                m_running.Remove(key);
                m_runningRegion.Remove(key);
                if (task.IsCanceled) continue;
                if (task.IsFaulted)
                {
                    if (m_samples != null && m_samples.Sampler is GeneratorSampler && m_world != null)
                    {
                        Jotunn.Logger.LogWarning($"The atlas could not sample the world for a close-up, using the map instead: {task.Exception?.GetBaseException()}");
                        m_samples = new SampleCache(new MinimapSampler(m_world));
                    }
                    else if (!m_broken)
                    {
                        Jotunn.Logger.LogWarning($"An atlas close-up could not be drawn: {task.Exception?.GetBaseException()}");
                        m_broken = true;
                    }
                    continue;
                }
                m_ready.Enqueue(new Done { m_key = key, m_level = (int)(key >> 40), m_region = region, m_rgba = task.Result });
            }
        }

        private void Upload()
        {
            for (int n = 0; n < UploadsPerFrame && m_ready.Count > 0; n++)
            {
                Done done = m_ready.Dequeue();
                if (!m_parent || m_tiles.ContainsKey(done.m_key)) continue;
                var texture = new Texture2D(TilePixels, TilePixels, TextureFormat.RGBA32, mipChain: true)
                {
                    name = "Atlas tile",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                };
                texture.LoadRawTextureData(done.m_rgba);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                var go = new GameObject("Tile", typeof(RectTransform), typeof(RawImage));
                var rect = (RectTransform)go.transform;
                rect.SetParent(m_layers[done.m_level], false);
                float half = InkPainter.CanvasSize * 0.5f;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(done.m_region.width, done.m_region.height);
                rect.anchoredPosition = new Vector2(done.m_region.center.x - half, half - done.m_region.center.y);
                var image = go.GetComponent<RawImage>();
                image.texture = texture;
                image.raycastTarget = false;
                image.color = new Color(1f, 1f, 1f, 0f);
                go.SetActive(false);
                m_tiles[done.m_key] = new Tile { m_level = done.m_level, m_region = done.m_region, m_texture = texture, m_image = image, m_used = m_frame };
            }
        }

        // Shows the tiles of the level in view. A tile shown for the first time fades in.
        private void Show(int level, RectInt visible)
        {
            foreach (Tile tile in m_tiles.Values)
            {
                bool show = tile.m_level == level && tile.m_region.Overlaps(visible);
                if (show) tile.m_used = m_frame;
                if (!tile.m_image || tile.m_image.gameObject.activeSelf == show) continue;
                tile.m_image.gameObject.SetActive(show);
            }
        }

        // Fades each newly shown tile in over FadeSeconds, so tiles ease onto the sheet rather
        // than pop in.
        private void Fade()
        {
            float step = Time.unscaledDeltaTime / FadeSeconds;
            foreach (Tile tile in m_tiles.Values)
            {
                if (tile.m_shown >= 1f || !tile.m_image || !tile.m_image.gameObject.activeSelf) continue;
                tile.m_shown = Mathf.Min(1f, tile.m_shown + step);
                tile.m_image.color = new Color(1f, 1f, 1f, tile.m_shown);
            }
        }

        // Worker thread. A tile's RGBA followed by its mip levels down to 1 pixel, each a box
        // average of the one above, as LoadRawTextureData expects for a mipmapped texture.
        private static byte[] WithMips(byte[] rgba)
        {
            int total = 0;
            for (int size = TilePixels; size >= 1; size /= 2) total += size * size * 4;
            var data = new byte[total];
            Buffer.BlockCopy(rgba, 0, data, 0, rgba.Length);
            int from = 0, to = rgba.Length;
            for (int size = TilePixels / 2; size >= 1; size /= 2)
            {
                int above = size * 2;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int a = from + ((2 * y) * above + 2 * x) * 4, b = a + above * 4;
                        int o = to + (y * size + x) * 4;
                        for (int c = 0; c < 4; c++)
                            data[o + c] = (byte)((data[a + c] + data[a + 4 + c] + data[b + c] + data[b + 4 + c] + 2) / 4);
                    }
                }
                from = to;
                to += size * size * 4;
            }
            return data;
        }

        // Drops the least recently used tiles beyond Capacity.
        private void Trim()
        {
            // A 4K view takes up to about 600 tiles with its ring; room for twice what is wanted
            // keeps a pan back instant there without a tile list sorted every frame.
            if (m_tiles.Count <= Mathf.Max(Capacity, 2 * m_wanted.Count)) return;
            var tiles = new List<KeyValuePair<long, Tile>>(m_tiles);
            tiles.Sort((a, b) => a.Value.m_used.CompareTo(b.Value.m_used));
            for (int i = 0; i < tiles.Count - Capacity; i++)
            {
                if (tiles[i].Value.m_used == m_frame) break;
                Destroy(tiles[i].Value);
                m_tiles.Remove(tiles[i].Key);
            }
        }

        // The view's tile layer changed (a new window): the old images went with it.
        private void Rebuild(RectTransform parent)
        {
            foreach (Tile tile in m_tiles.Values) Destroy(tile);
            m_tiles.Clear();
            m_shownLevel = -1;
            m_parent = parent;
            if (!parent) return;
            for (int i = 0; i < m_layers.Length; i++)
            {
                var go = new GameObject("Level" + s_scales[i], typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(parent, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                m_layers[i] = rect;
            }
        }

        private static void Destroy(Tile tile)
        {
            if (tile.m_image) UnityEngine.Object.Destroy(tile.m_image.gameObject);
            if (tile.m_texture) UnityEngine.Object.Destroy(tile.m_texture);
        }
    }
}
