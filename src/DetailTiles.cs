using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Atlas
{
    /// <summary>
    /// Close-ups as fixed tiles, as web maps do: TilePixels squared tiles at the finest
    /// magnification the view reaches, at most 4x, the one level at every zoom, mipmapped so they stay clean when zoomed out, each drawn on its own
    /// worker (DetailTile.Render) and kept as a
    /// texture. The view fills in piece by piece, each tile fading in, panning back is instant,
    /// and tiles are drawn ahead of need: a ring round the view, further ahead where it is being
    /// panned, the part a zoom out shows, round the pointer before the view is zoomed in far
    /// enough to show them, and, slowly, round the view the closed atlas will open on. Tiles in
    /// view take the workers' priority lane; tiles drawn ahead that the view has left are
    /// cancelled. Main thread, except the rendering.
    /// </summary>
    internal sealed class DetailTiles
    {
        private const int TilePixels = 256;
        // No 8x level: above 1080p it took four times the tiles of 4x for the same view, and in game
        // a view of them filled in 6 to 15 s (timing build, 1440p, the user's call). There 4x tiles
        // are magnified a little at the deepest zoom.
        private static readonly int[] s_scales = { 2, 4 };
        // Screen pixels per sheet pixel from which tiles are shown; below it the sheet is sharp
        // enough, and at the finest level a wider view would take hundreds of tiles.
        private const float DetailFrom = 1.25f;
        // The finest close-ups' coast in sheet pixels: they ink it with the base pen (InkPainter
        // DrawTileCoast, not the sheet's heavier CoastWidth), thinner by their level of detail
        // (DetailTile's lod). The hovered name's banner is inked with it (AtlasView.ShoreWidth).
        public static float FinestCoastPixels => 1f / Mathf.Sqrt(Mathf.Max(1f, s_scales[s_scales.Length - 1] * 0.5f));
        // The most tiles a view may take before close-ups start: a 1080p view at DetailFrom takes
        // about 340; at 1440p close-ups start at about 1.3 screen pixels a sheet pixel, at 4K
        // about 2 (the user asked for close-ups a little earlier).
        private const int MaxVisible = 520;
        // Ground samples every GridPixels tile pixels (the nearest SampleCache spacing).
        private const float GridPixels = 3f;
        // At about 340 KB a tile with its mipmaps, about 110 MB of textures; the least recently
        // used beyond go.
        private const int Capacity = 320;
        // Textures uploaded per frame, so a burst of finished tiles never stalls a frame: at most
        // UploadsPerFrame while open, and after the first only while the frame's uploads took
        // under UploadBudgetMs (a 256² tile with mips is about 350 KB to copy and send); one a
        // frame while closed, when the game is being played.
        private const int UploadsPerFrame = 3;
        private const double UploadBudgetMs = 1.0;
        // Tiles drawn ahead round the pointer while zoomed out, at most.
        private const int AheadTiles = 48;
        // Tiles drawn ahead of the view and its ring (where it is panned, a zoom out), at most,
        // and never past Capacity with the view and ring, so memory stays as before.
        private const int PrefetchTiles = 96;
        // Panning: the view's motion in sheet pixels a second, smoothed over MotionSmoothing
        // seconds, is drawn ahead LeadSeconds, at most a view's width and height.
        private const float LeadSeconds = 0.75f;
        private const float MotionSmoothing = 0.12f;
        // Zooming out shows the view grown by the wheel's step about the pointer; tiles for up to
        // ZoomOutSteps steps are drawn ahead (zooming in needs no new tiles: one level at every zoom).
        private const int ZoomOutSteps = 2;
        // While closed, once the close-up record is ready (after the opening's work): the view it
        // will open on is drawn on at most ClosedWorkers workers in the normal lane, planned every
        // ClosedPlanFrames frames, so play and the game's own loading keep the rest.
        private const int ClosedWorkers = 1;
        private const int ClosedPlanFrames = 15;
        // A tile being drawn that has not been wanted for this many frames (the view left it) is
        // cancelled, freeing its worker; a few frames' grace keeps a jittering pan from thrashing.
        private const int CancelAfterFrames = 20;
        private const float FadeSeconds = 0.2f;
        // Sheet pixels round a ticked pin's box within which a tile is drawn again (NewChecks):
        // tiles replay the calls whose bounds reach them, a pixel past their ink.
        private const int ChecksMargin = 2;
        // Tiles tested against the fog a frame at most (UnderFog reads up to 66² fog pixels each,
        // about 2.5 us offline for a 2x tile under fog), so a view that opens far out in the fog
        // never stalls a frame.
        private const int FogTestsPerFrame = 64;

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

        // A tile on a worker, with its own cancellation, so a tile the view has left can be
        // stopped without the others.
        private sealed class Running
        {
            public Task<byte[]> m_task;
            public RectInt m_region;
            public CancellationTokenSource m_cancel;
            // Queued in the workers' priority lane (it was in view).
            public bool m_first;
            // The last frame it was wanted.
            public long m_wanted;
        }

        private readonly Dictionary<long, Tile> m_tiles = new Dictionary<long, Tile>();
        private readonly Dictionary<long, Running> m_running = new Dictionary<long, Running>();
        private readonly List<Done> m_ready = new List<Done>();
        private readonly List<long> m_wanted = new List<long>(256);
        private readonly HashSet<long> m_wantedSet = new HashSet<long>();
        private readonly List<long> m_finished = new List<long>();
        // The tiles of the view and its ring this frame (the first of m_wanted); Trim keeps room
        // for them, the tiles drawn ahead come out of Capacity.
        private int m_core;
        // The part of the sheet in view (or the one the closed atlas opens on), whose finished
        // tiles are uploaded first; empty when no close-ups are shown there.
        private RectInt m_inView;
        // The view's motion (Track): its centre and size last frame and its smoothed velocity,
        // sheet pixels a second.
        private Vector2 m_lastCentre;
        private Vector2Int m_lastSize;
        private Vector2 m_velocity;
        private bool m_tracking;
        // Tiles lying wholly under opaque fog (UnderFog): done with no texture, as the fog covers
        // all they would show. Dropped like drawn tiles when the fog changes over them, so a fog
        // opened there tests them again.
        private readonly HashSet<long> m_fogged = new HashSet<long>();
        private readonly RectTransform[] m_layers = new RectTransform[s_scales.Length];
        private RectTransform m_parent;
        private long m_frame;
        private bool m_broken;

        private InkRecord m_record;
        private Paper.Wash m_paper;
        private WorldSource m_world;
        private SampleCache m_samples;

        public bool Ready => m_record != null && m_paper != null && m_world != null && m_samples != null && !m_broken;

        // Set by the controller while the atlas's own work for an opening runs (a fog or ink being
        // laid after a record): nothing is drawn ahead for the closed atlas meanwhile.
        public bool HoldWhileClosed { get; set; }

        public void SetSource(InkRecord record, Paper.Wash paper, WorldSource world, SampleCache samples)
        {
            m_record = record;
            m_paper = paper;
            m_world = world;
            m_samples = samples;
        }

        // The glimpse's bare parchment (Paper.Fog's glimpse, RGBA at FogSize), which tiles lay
        // over their wash, and where the drawing stops (Paper.Fog's FogEdge): the trees, glyphs
        // and wave strokes the band leaves out, which tiles leave out too; null for none.
        private byte[] m_glimpse;
        private FogEdge m_edge;
        // The record groups of the glyphs m_edge leaves out, for m_record (HiddenGroups).
        private HashSet<int> m_hiddenGroups;
        private FogEdge m_groupsEdge;
        private InkRecord m_groupsRecord;

        // A new fog. With its change from the fog the tiles were drawn under (FogChange, null when
        // it could not be compared) only the tiles that read a changed part are dropped, done,
        // running or queued; the rest draw the same under either fog and stay, so a record that
        // explores a little more does not redraw every close-up. Without it every tile goes.
        public void SetGlimpse(byte[] glimpse, FogEdge edge, FogChange change)
        {
            bool partial = change != null && m_record != null && change.m_fromGlimpse == m_glimpse && change.m_fromEdge == m_edge;
            m_glimpse = glimpse;
            m_edge = edge;
            if (!partial)
            {
                DropTiles();
                return;
            }
            InkRecord record = m_record;
            DropWhere((level, region) => change.Affects(region, DetailTile.FogReach(record, region, s_scales[level])));
        }

        // A new painting of the same world: its tiles are drawn again from the new record (set by
        // SetSource), under the same glimpse and fog. Clearing those too left close-ups without
        // the glimpse's bare parchment and greyer ink until the next fog, since the world is now
        // painted after login and finishes after the fog is laid.
        public void NewRecord()
        {
            DropTiles();
            m_record = null;
        }

        // The same painting with pins ticked or unticked (InkPainter.Recheck): only the tiles over
        // the redrawn pins (changed, sheet pixels) are drawn again, from the new record; the rest
        // are the same in both records, so they stay, and tiles still being drawn from the old
        // one elsewhere are kept too. Those over the pins are dropped, running or done.
        public void NewChecks(InkRecord record, List<RectInt> changed)
        {
            DropWhere((level, region) => Touches(region, changed));
            m_record = record;
        }

        // Drops the tiles, done, running or queued, for which drop (level, sheet region) holds.
        private void DropWhere(Func<int, RectInt, bool> drop)
        {
            m_finished.Clear();
            foreach (KeyValuePair<long, Tile> entry in m_tiles)
                if (drop(entry.Value.m_level, entry.Value.m_region)) m_finished.Add(entry.Key);
            foreach (long key in m_finished)
            {
                Destroy(m_tiles[key]);
                m_tiles.Remove(key);
            }
            m_finished.Clear();
            // Tested under the fog they were laid under (FogChange.Affects reads more than
            // UnderFog, so a change there drops them).
            foreach (long key in m_fogged)
                if (drop((int)(key >> 40), Region(key))) m_finished.Add(key);
            foreach (long key in m_finished) m_fogged.Remove(key);
            m_finished.Clear();
            foreach (KeyValuePair<long, Running> entry in m_running)
                if (drop((int)(entry.Key >> 40), entry.Value.m_region)) m_finished.Add(entry.Key);
            // Cancelled; whatever still runs on its worker is never collected.
            foreach (long key in m_finished)
            {
                m_running[key].m_cancel.Cancel();
                m_running.Remove(key);
            }
            m_finished.Clear();
            m_ready.RemoveAll(done => drop(done.m_level, done.m_region));
        }

        private static bool Touches(RectInt region, List<RectInt> changed)
        {
            foreach (RectInt pin in changed)
            {
                var grown = new RectInt(pin.x - ChecksMargin, pin.y - ChecksMargin, pin.width + 2 * ChecksMargin, pin.height + 2 * ChecksMargin);
                if (region.Overlaps(grown)) return true;
            }
            return false;
        }

        // Forgets everything, for a new world. Running tiles are cancelled and their results dropped.
        public void Clear()
        {
            DropTiles();
            m_parent = null;
            m_broken = false;
            m_record = null;
            m_paper = null;
            m_world = null;
            m_samples = null;
            m_glimpse = null;
            m_edge = null;
            m_hiddenGroups = null;
            m_groupsEdge = null;
            m_groupsRecord = null;
        }

        private void DropTiles()
        {
            foreach (Running running in m_running.Values) running.m_cancel.Cancel();
            foreach (Tile tile in m_tiles.Values) Destroy(tile);
            m_tiles.Clear();
            m_fogged.Clear();
            m_running.Clear();
            m_ready.Clear();
            m_tracking = false;
        }

        public void Update(AtlasView view)
        {
            m_frame++;
            Harvest();
            RectTransform parent = view.DetailLayer;
            if (parent != m_parent) Rebuild(parent);
            bool open = view.IsOpen;
            Upload(open);
            view.DetailShown = false;
            view.DetailLod = 1f;
            if (!open) m_tracking = false;
            if (!m_parent || !Ready) return;
            RectInt visible;
            float onScreen;
            Vector2 pointer;
            if (open)
            {
                if (!view.ViewRegion(out visible, out onScreen, out pointer)) return;
            }
            else
            {
                // Closed: the view it will open on (kept across openings, as are the tiles) is
                // drawn ahead slowly, planned now and then, so reopening after a record or a long
                // trip finds it drawn. Not before the first opening, which has no view yet.
                if (HoldWhileClosed) return;
                if (m_frame % ClosedPlanFrames != 0)
                {
                    // Between plans the next of the last plan's tiles starts once its worker is
                    // free, so the one worker is kept busy rather than idle until the next plan.
                    if (m_running.Count < ClosedWorkers && m_wanted.Count > 0) Start(false);
                    return;
                }
                if (!view.RememberedRegion(out visible, out onScreen, out pointer)) return;
            }

            // One level only, the finest the view can reach, at every zoom: levels drawn at their
            // own level of detail change the trees and marks as the zoom crosses them. Mipmaps keep
            // the tiles clean when zoomed out. Close-ups start once the view takes at most
            // MaxVisible of them (above 1080p the finest level has four times the tiles of 1080p's
            // over the same view, thousands at DetailFrom); until then the sheet shows.
            int level = Level(view.MaxOnScreen);
            int size = TileSize(level);
            Vector2 screen = view.ViewPixels;
            float from = Mathf.Max(DetailFrom, Mathf.Sqrt(screen.x * screen.y / MaxVisible) / size);
            bool detail = onScreen >= from;
            m_inView = detail ? visible : default(RectInt);
            if (open)
            {
                view.DetailShown = detail;
                // The shown level draws its pins smaller by its level of detail (InkCanvas.Pin);
                // markers, hover and names follow it.
                view.DetailLod = detail ? DetailTile.Lod(s_scales[level]) : 1f;
                Show(detail ? level : -1, visible);
                Fade();
                Track(visible, detail);
            }

            // What to draw, most wanted first: the view from the pointer outward and a ring of one
            // tile round it, then ahead of need: where the view is being panned and what a zoom out
            // shows; zoomed out, the part round the pointer a zoom to the start of close-ups shows.
            m_wanted.Clear();
            m_wantedSet.Clear();
            if (detail)
            {
                Want(level, visible, pointer, int.MaxValue);
                Want(level, Grow(visible, size), pointer, int.MaxValue);
                m_core = m_wanted.Count;
                // Ahead of need only while the view and ring leave room under Capacity: at the
                // start of close-ups the view alone takes more.
                int room = Mathf.Min(PrefetchTiles, Capacity - m_core);
                Vector2 centre = visible.center;
                if (room > 0 && open && Lead(visible, size, out Vector2 lead))
                {
                    var ahead = new RectInt(visible.x + Mathf.RoundToInt(lead.x), visible.y + Mathf.RoundToInt(lead.y), visible.width, visible.height);
                    room -= Want(level, Grow(ahead, size), centre, room);
                }
                // A zoom out keeps the point under the pointer still, so the view grows about it.
                float grow = 1f;
                for (int step = 0; step < ZoomOutSteps && onScreen / (grow * AtlasView.ZoomStep) >= from; step++)
                    grow *= AtlasView.ZoomStep;
                if (room > 0 && grow > 1f)
                {
                    var wider = new RectInt(
                        Mathf.FloorToInt(pointer.x - (pointer.x - visible.x) * grow),
                        Mathf.FloorToInt(pointer.y - (pointer.y - visible.y) * grow),
                        Mathf.CeilToInt(visible.width * grow), Mathf.CeilToInt(visible.height * grow));
                    Want(level, wider, pointer, room);
                }
            }
            else
            {
                m_core = 0;
                // Zooming in keeps the point under the pointer still, so that is where it lands.
                float shrink = onScreen / from;
                var ahead = new RectInt(
                    Mathf.RoundToInt(pointer.x - (pointer.x - visible.x) * shrink),
                    Mathf.RoundToInt(pointer.y - (pointer.y - visible.y) * shrink),
                    Mathf.CeilToInt(visible.width * shrink), Mathf.CeilToInt(visible.height * shrink));
                Want(level, ahead, pointer, AheadTiles);
            }
            Start(open);
            Trim();
        }

        private static RectInt Grow(RectInt area, int by) =>
            new RectInt(area.x - by, area.y - by, area.width + 2 * by, area.height + 2 * by);

        // Follows the view's centre from frame to frame while close-ups show. A change of size is a
        // zoom, not a pan (the centre moves toward the pointer), so it restarts the track.
        private void Track(RectInt visible, bool detail)
        {
            float dt = Time.unscaledDeltaTime;
            Vector2 centre = visible.center;
            bool zoomed = Mathf.Abs(visible.width - m_lastSize.x) > 2 || Mathf.Abs(visible.height - m_lastSize.y) > 2;
            if (!detail || !m_tracking || zoomed || dt <= 0f)
            {
                m_velocity = Vector2.zero;
                m_tracking = detail;
            }
            else
            {
                Vector2 now = (centre - m_lastCentre) / dt;
                m_velocity += (now - m_velocity) * (1f - Mathf.Exp(-dt / MotionSmoothing));
            }
            m_lastCentre = centre;
            m_lastSize = new Vector2Int(visible.width, visible.height);
        }

        // Where a pan carries the view in LeadSeconds, at most a view either way; false while it
        // moves under a tile in that time.
        private bool Lead(RectInt visible, int size, out Vector2 lead)
        {
            lead = m_velocity * LeadSeconds;
            if (lead.sqrMagnitude < size * size) return false;
            lead.x = Mathf.Clamp(lead.x, -visible.width, visible.width);
            lead.y = Mathf.Clamp(lead.y, -visible.height, visible.height);
            return true;
        }

        // The coarsest level at least as fine as the screen, so tiles are never magnified.
        private static int Level(float onScreen)
        {
            for (int i = 0; i < s_scales.Length; i++)
                if (s_scales[i] >= onScreen) return i;
            return s_scales.Length - 1;
        }

        private static int TileSize(int level) => TilePixels / s_scales[level];

        private static long Key(int level, int x, int y) => ((long)level << 40) | ((long)(x & 0xFFFFF) << 20) | (long)(y & 0xFFFFF);

        // Adds the tiles of a level touching a sheet rectangle not wanted yet, nearest the pointer
        // first, up to limit; returns how many.
        private int Want(int level, RectInt area, Vector2 pointer, int limit)
        {
            int size = TileSize(level);
            // Floor division, so a rectangle reaching past the sheet's north or west edge is cut
            // at it rather than rounded onto its first row.
            int x0 = Mathf.Max(0, Mathf.FloorToInt(area.xMin / (float)size)), x1 = Mathf.Min(InkPainter.CanvasSize / size - 1, Mathf.FloorToInt((area.xMax - 1) / (float)size));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(area.yMin / (float)size)), y1 = Mathf.Min(InkPainter.CanvasSize / size - 1, Mathf.FloorToInt((area.yMax - 1) / (float)size));
            if (x1 < x0 || y1 < y0) return 0;
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
            if (count <= 1) return count;
            var range = m_wanted.GetRange(first, count);
            range.Sort((a, b) => Distance(a, pointer).CompareTo(Distance(b, pointer)));
            m_wanted.RemoveRange(first, count);
            for (int i = 0; i < range.Count; i++)
            {
                if (i < limit) m_wanted.Add(range[i]);
                else m_wantedSet.Remove(range[i]);
            }
            return Mathf.Min(count, limit);
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

        // Starts the most wanted tiles not drawn yet. Open, tiles in view go to the workers'
        // priority lane, as many as there are workers, ahead of anything queued (a background
        // paint's loops included); the rest share the workers with them, so a tile drawn ahead
        // never waits in front of one in view. Closed, ClosedWorkers in the normal lane. Tiles
        // wholly under opaque fog are done at once with no texture (UnderFog), taking no worker.
        // Tiles being drawn that the view has left are cancelled.
        private void Start(bool open)
        {
            foreach (long key in m_wanted)
            {
                if (m_tiles.TryGetValue(key, out Tile tile)) tile.m_used = m_frame;
                else if (m_running.TryGetValue(key, out Running running)) running.m_wanted = m_frame;
            }
            Cancel();

            int limit = open ? Workers.Count : ClosedWorkers;
            int inView = 0;
            foreach (Running running in m_running.Values)
                if (running.m_first) inView++;
            int fogTests = 0;
            foreach (long key in m_wanted)
            {
                if (m_tiles.ContainsKey(key) || m_fogged.Contains(key) || m_running.ContainsKey(key) || Queued(key)) continue;
                int level = (int)(key >> 40);
                RectInt region = Region(key);
                bool first = open && region.Overlaps(m_inView);
                // The wanted tiles come in view first, so once one cannot start none after can.
                if (first ? inView >= Workers.Count : m_running.Count >= limit) break;
                if (m_edge != null)
                {
                    if (fogTests++ >= FogTestsPerFrame) break;
                    if (UnderFog(m_edge.m_edge, region))
                    {
                        m_fogged.Add(key);
                        continue;
                    }
                }
                InkRecord record = m_record;
                Paper.Wash paper = m_paper;
                WorldSource world = m_world;
                SampleCache samples = m_samples;
                int scale = s_scales[level];
                var source = new CancellationTokenSource();
                CancellationToken cancel = source.Token;
                byte[] glimpse = m_glimpse;
                FogEdge edge = m_edge;
                HashSet<int> hiddenGroups = HiddenGroups(record);
                Func<byte[]> work = () => WithMips(DetailTile.Render(record, paper, world, samples, region, scale, GridPixels, 1, cancel, glimpse, edge, hiddenGroups));
                m_running[key] = new Running
                {
                    m_task = first ? Workers.RunFirst(work, cancel) : Workers.Run(work, cancel),
                    m_region = region,
                    m_cancel = source,
                    m_first = first,
                    m_wanted = m_frame,
                };
                if (first) inView++;
            }
        }

        // Cancels the tiles being drawn that have not been wanted for CancelAfterFrames: a tile
        // still queued never runs, one running stops at its next check.
        private void Cancel()
        {
            m_finished.Clear();
            foreach (KeyValuePair<long, Running> entry in m_running)
                if (m_frame - entry.Value.m_wanted > CancelAfterFrames) m_finished.Add(entry.Key);
            foreach (long key in m_finished)
            {
                m_running[key].m_cancel.Cancel();
                m_running.Remove(key);
            }
            m_finished.Clear();
        }

        // Whether the fog as laid (FogEdge.m_edge, lifts included: the fog texture's own alpha,
        // FogSize² rows from the south) is fully opaque over a sheet region and one fog pixel
        // round it. The fog layer lies over the close-ups and is sampled bilinearly, a sheet
        // point x reading fog pixels floor(x / 2 - 0.5) and the one after, so the region's
        // pixels show nothing but fog there and a close-up would be wasted. Outside the world's
        // disc the fog is clear, so the sheet's margin is drawn as before.
        internal static bool UnderFog(byte[] opacity, RectInt region)
        {
            if (opacity == null) return false;
            int n = Paper.FogSize;
            float scale = n / (float)InkPainter.CanvasSize, canvas = InkPainter.CanvasSize;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(region.xMin * scale) - 1);
            int x1 = Mathf.Min(n - 1, Mathf.CeilToInt(region.xMax * scale));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((canvas - region.yMax) * scale) - 1);
            int y1 = Mathf.Min(n - 1, Mathf.CeilToInt((canvas - region.yMin) * scale));
            for (int y = y0; y <= y1; y++)
            {
                int row = y * n;
                for (int x = x0; x <= x1; x++)
                    if (opacity[row + x] != 255) return false;
            }
            return true;
        }

        // The record groups the edge leaves out, worked out once per edge and record (a few
        // thousand glyphs); null when the record was drawn with other pins than the edge judged.
        private HashSet<int> HiddenGroups(InkRecord record)
        {
            if (m_groupsEdge != m_edge || m_groupsRecord != record)
            {
                m_groupsEdge = m_edge;
                m_groupsRecord = record;
                m_hiddenGroups = m_edge?.HiddenGroups(record);
            }
            return m_hiddenGroups;
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
            foreach (KeyValuePair<long, Running> entry in m_running)
                if (entry.Value.m_task.IsCompleted) m_finished.Add(entry.Key);
            foreach (long key in m_finished)
            {
                Task<byte[]> task = m_running[key].m_task;
                RectInt region = m_running[key].m_region;
                m_running.Remove(key);
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
                m_ready.Add(new Done { m_key = key, m_level = (int)(key >> 40), m_region = region, m_rgba = task.Result });
            }
        }

        // Uploads finished tiles, those in view first, within the frame's budget (UploadsPerFrame,
        // UploadBudgetMs; one while closed).
        private void Upload(bool open)
        {
            if (m_ready.Count == 0) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double budget = UploadBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            int most = open ? UploadsPerFrame : 1;
            for (int n = 0; n < most && m_ready.Count > 0; n++)
            {
                if (n > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - started >= budget) break;
                int pick = 0;
                for (int i = 0; i < m_ready.Count; i++)
                {
                    if (!m_ready[i].m_region.Overlaps(m_inView)) continue;
                    pick = i;
                    break;
                }
                Done done = m_ready[pick];
                m_ready.RemoveAt(pick);
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
            // A view with its ring can want more than Capacity (MaxVisible and a ring round it);
            // room for those and half of Capacity keeps a pan back instant without a tile list
            // sorted every frame. Tiles drawn ahead come out of Capacity (they are wanted only
            // while the view and ring leave room), so they never raise the cap.
            if (m_tiles.Count <= Mathf.Max(Capacity, m_core + Capacity / 2)) return;
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
