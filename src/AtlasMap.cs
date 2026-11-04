using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Ties the atlas together for the current world: draws (or loads) the ink sheet once in the
    /// background, keeps the fog in step with the last recorded snapshot, and opens the view on the
    /// map key. All heavy work runs on worker threads; the main thread only copies vanilla's map
    /// textures once per world and uploads finished pixels. Nothing happens in worlds with a map.
    /// </summary>
    internal static class AtlasMap
    {
        private static readonly AtlasView s_view = new AtlasView();
        private static readonly TableSparkles s_sparkles = new TableSparkles();

        private static long s_worldUid;
        private static long s_playerId;

        private static Snapshot s_snapshot;
        private static Task<CachedSheet> s_cacheTask;
        private static bool s_cacheStarted;
        private static Task<WorldSource> s_decodeTask;
        private static Task<PaintResult> s_paintTask;
        private static Task<byte[]> s_mipsTask;
        private static InkRecord s_record;
        // The sheet's ink density, for placing pin names on clear ground.
        private static InkDensity s_density;
        // Cancelled when the world changes, so a half-finished drawing for the old world stops.
        private static CancellationTokenSource s_cancel = new CancellationTokenSource();
        private static bool s_inkFailed;
        // Why decoding or painting failed, kept while the disk cache is still loading: if the
        // cache then has no sheet either, the atlas has failed rather than still drawing.
        private static AggregateException s_paintError;
        // Set after an error in Update: the atlas is off for the session.
        private static bool s_stopped;
        // The pins to draw into the sheet and their key (KeepPins), the key of the uploaded sheet,
        // of the sheet on disk and of the painting under way, and pins whose painting failed.
        private static SheetPin[] s_pins = new SheetPin[0];
        private static long s_pinsKey;
        private static bool s_pinsDirty = true;
        private static long s_sheetKey;
        private static long s_cachedKey;
        private static bool s_hasCachedKey;
        private static long s_paintKey;
        private static long s_failedKey;
        private static bool s_hasFailedKey;
        private static long s_mipsKey;
        private static InkDensity s_mipsDensity;
        private static bool s_decodeFailed;
        private static Texture2D s_ink;
        // The sea illustrations on the uploaded sheet, those the uploaded fog showed or hid whole,
        // and those the fog being laid does. The atlas opens only once the first two agree, so a
        // monster never shows cut by the fog's torn edge.
        private static SeaMark[] s_seaMarks;
        private static SeaMark[] s_fogMarks;
        private static SeaMark[] s_fogTaskMarks;

        private static byte[] s_parchment;
        // The parchment, made once per world and shared by the fog and the wash.
        private static Task<byte[]> s_parchmentTask;
        // The sheet's stains and patina, laid over everything (Paper.Dirt); a second or two to make.
        private static Task<byte[]> s_dirtTask;
        private static Texture2D s_dirt;
        // The sheet's paper: the parchment under the biome wash, or the bare parchment if the wash
        // failed. The fog keeps the bare parchment.
        private static Task<Paper.Wash> s_washTask;
        private static bool s_washFailed;
        private static Paper.Wash s_wash;
        private static byte[] s_fogPixels;
        private static Task<byte[]> s_fogTask;
        private static bool s_fogDirty;
        private static Texture2D s_paper;
        private static Texture2D s_fog;

        // Close-ups: the decoded world and its sample cache, and the tiles drawn from them.
        private static readonly DetailTiles s_tiles = new DetailTiles();
        private static WorldSource s_source;
        private static SampleCache s_samples;

        // True while the atlas is open and for a couple of frames after, as vanilla does for its
        // own map, so the key that closes it does not also open the menu.
        public static bool BlocksGame => !s_stopped && (s_view.IsOpen || s_view.ClosedFrames <= 2);

        // Switches the atlas off for the session after an error. The view is closed and stops
        // blocking input: left open, the Minimap.IsOpen postfix would keep the player frozen.
        public static void Stop()
        {
            s_stopped = true;
            try
            {
                Reset();
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"The atlas could not clean up after its error: {e}");
            }
        }

        public static void Update()
        {
            ZNet net = ZNet.instance;
            Player player = Player.m_localPlayer;
            if (!net)
            {
                if (s_worldUid != 0L) Reset();
                return;
            }
            long worldUid = net.GetWorldUID();
            if (worldUid != s_worldUid)
            {
                Reset();
                s_worldUid = worldUid;
            }
            if (!player || !Game.m_noMap)
            {
                s_view.Hide();
                s_view.Update();
                return;
            }
            if (player.GetPlayerID() != s_playerId)
            {
                s_playerId = player.GetPlayerID();
                s_snapshot = Snapshot.Load(player, worldUid);
                s_fogDirty = true;
                s_pinsDirty = true;
            }

            KeepPins();
            KeepInk(worldUid);
            KeepFog();
            KeepPaper();
            KeepDirt();
            if (!s_tiles.Ready && s_record != null && s_wash != null && s_samples != null)
                s_tiles.SetSource(s_record, s_wash, s_source, s_samples);
            s_tiles.Update(s_view);
            // The atlas is up to date once its fog is laid and its sheet is there with the current
            // pins (or failed).
            bool pinsBehind = s_ink && s_sheetKey != s_pinsKey && !(s_hasFailedKey && s_failedKey == s_pinsKey);
            s_sparkles.Update(s_fogDirty || s_fogTask != null || s_mipsTask != null || (!s_ink && !s_inkFailed) || pinsBehind);

            if (!s_view.IsOpen && s_view.ClosedFrames > 2 && OpenPressed() && CanOpen(player)) Open(player);
            s_view.Update();
        }

        // A cartography table recorded the player's discoveries: the atlas now shows them, and
        // the table sparkles until the atlas has caught up.
        public static void Record(MapTable table)
        {
            Player player = Player.m_localPlayer;
            Minimap minimap = Minimap.instance;
            if (!player || !minimap || !ZNet.instance) return;
            s_snapshot = Snapshot.Take(minimap);
            s_snapshot.Save(player, ZNet.instance.GetWorldUID());
            s_fogDirty = true;
            s_pinsDirty = true;
            // The snapshot is still saved for the next session, but nothing would end the sparkles.
            if (!s_stopped) s_sparkles.Begin(table);
        }

        private static void Open(Player player)
        {
            if (s_snapshot == null)
            {
                player.Message(MessageHud.MessageType.Center, "$atlas_blank");
                return;
            }
            if (!s_ink || !s_paper || !s_fog || s_fogMarks != s_seaMarks)
            {
                player.Message(MessageHud.MessageType.Center, s_inkFailed ? "$atlas_failed" : "$atlas_drawing");
                return;
            }
            s_view.Show(s_paper, s_ink, s_fog, s_dirt, s_snapshot, s_density, s_source);
        }

        private static bool OpenPressed() => ZInput.GetButtonDown("Map") || JoyMapPressed();

        // The gamepad map button as vanilla reads it: not while both triggers are held or with the
        // alt-keys modifier, which turn it into other commands.
        public static bool JoyMapPressed() =>
            ZInput.GetButtonDown("JoyMap") && (!ZInput.GetButton("JoyLTrigger") || !ZInput.GetButton("JoyLBumper"))
            && !ZInput.GetButton("JoyAltKeys");

        // The same UI checks vanilla makes before taking player input, plus the menus that sit on top.
        private static bool CanOpen(Player player)
        {
            return !player.IsDead() && !player.InCutscene()
                && (!Chat.instance || !Chat.instance.HasFocus()) && !Console.IsVisible() && !TextInput.IsVisible()
                && !Menu.IsVisible() && !InventoryGui.IsVisible() && !StoreGui.IsVisible() && !Minimap.IsOpen()
                && (!TextViewer.instance || !TextViewer.instance.IsVisible()) && !UnifiedPopup.IsVisible()
                && !PlayerCustomizaton.IsBarberGuiVisible() && !Hud.IsPieceSelectionVisible() && !Hud.InRadial();
        }

        // The sheet comes from the disk cache when there is one, so the atlas is ready at once. The
        // world is still painted in the background once the atlas is first opened, because only
        // painting yields the record that detail tiles are drawn from; without a cached sheet,
        // painting starts at once and also provides the sheet.
        private static void KeepInk(long worldUid)
        {
            if (s_inkFailed) return;
            if (!s_cacheStarted)
            {
                s_cacheStarted = true;
                int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
                s_cacheTask = Workers.Run(() =>
                {
                    byte[] ink = InkCache.Load(worldUid, seed, out SeaMark[] marks, out long key, out Vector4[] trees);
                    return ink == null ? null : new CachedSheet { m_sheet = ink, m_marks = marks, m_trees = trees, m_density = InkDensity.FromSheet(ink), m_key = key };
                });
            }
            if (s_cacheTask != null && s_cacheTask.IsCompleted)
            {
                if (s_cacheTask.IsFaulted) Jotunn.Logger.LogWarning($"The cached atlas could not be loaded: {s_cacheTask.Exception?.GetBaseException()}");
                else if (s_cacheTask.Result != null && s_sheet == null)
                {
                    CachedSheet cached = s_cacheTask.Result;
                    SetSheet(cached.m_sheet, cached.m_marks, cached.m_trees, cached.m_density, cached.m_key);
                    s_cachedKey = cached.m_key;
                    s_hasCachedKey = true;
                }
                s_cacheTask = null;
                // Painting failed while the cache was loading, and the cache had no sheet either.
                if (s_sheet == null && s_paintError != null) Fail(s_paintError);
            }
            KeepMips();

            if (s_paintTask == null)
            {
                if (s_decodeFailed) return;
                if (s_source == null)
                {
                    if (s_decodeTask == null)
                    {
                        if (!WorldSource.IsReady(Minimap.instance)) return;
                        WorldSource captured = WorldSource.Capture(Minimap.instance);
                        s_decodeTask = Workers.Run(() =>
                        {
                            captured.Decode();
                            return captured;
                        });
                        return;
                    }
                    if (!s_decodeTask.IsCompleted) return;
                    Task<WorldSource> decoded = s_decodeTask;
                    s_decodeTask = null;
                    if (decoded.IsFaulted)
                    {
                        s_washFailed = true;
                        s_paintError = decoded.Exception;
                        if (s_sheet == null && s_cacheTask == null) Fail(decoded.Exception);
                        else Jotunn.Logger.LogWarning($"The atlas could not read the world: {decoded.Exception?.GetBaseException()}");
                        s_decodeFailed = true;
                        return;
                    }
                    WorldSource decodedSource = decoded.Result;
                    s_source = decodedSource;
                    WorldGenerator generator = WorldGenerator.instance;
                    s_samples = new SampleCache(generator != null ? new GeneratorSampler(generator, decodedSource) : (IWorldSampler)new MinimapSampler(decodedSource));
                    s_washTask = Workers.Then(Parchment(decodedSource.m_seed), parchment => Paper.Washed(parchment, decodedSource));
                }

                // The sheet is painted when there is none, when its pins are not the current ones
                // (pins are drawn into it), and, behind a cached sheet with the right pins, once the
                // cache is loaded, for the record close-ups are drawn from: started only when the
                // atlas was first opened, close-ups came half a minute after joining (the user's
                // call: about 15 s of background work each session for close-ups ready at once).
                if (s_cacheTask != null) return;
                bool stale = s_sheet == null || s_rawKey != s_pinsKey;
                if (!stale && s_record != null) return;
                if (s_hasFailedKey && s_failedKey == s_pinsKey) return;
                WorldSource source = s_source;
                CancellationToken cancel = s_cancel.Token;
                SheetPin[] pins = s_pins;
                long key = s_pinsKey, cachedKey = s_cachedKey;
                bool hasCache = s_hasCachedKey;
                s_paintKey = key;
                s_paintTask = Workers.Run(() =>
                {
                    var record = new InkRecord();
                    byte[] ink = InkPainter.Paint(source, cancel, record, pins);
                    // A sheet drawn without the alt biomes' groves is not cached: the cache is keyed
                    // by seed, so it would stand in for the variants in every later session.
                    bool saved = false;
                    if ((!hasCache || cachedKey != key) && source.m_grove != null)
                    {
                        InkCache.Save(worldUid, source.m_seed, ink, record.m_seaMarks, key, record.m_trees);
                        saved = true;
                    }
                    return new PaintResult { m_ink = ink, m_record = record, m_density = InkDensity.FromSheet(ink), m_key = key, m_saved = saved };
                });
                return;
            }
            if (!s_paintTask.IsCompleted) return;
            Task<PaintResult> painted = s_paintTask;
            s_paintTask = null;
            if (painted.IsFaulted)
            {
                // Without a sheet there is nothing to show; with one, only its new pins and the
                // detail tiles are lost. The same pins are not tried again.
                s_paintError = painted.Exception;
                s_hasFailedKey = true;
                s_failedKey = s_paintKey;
                if (s_sheet == null && s_cacheTask == null) Fail(painted.Exception);
                else Jotunn.Logger.LogWarning($"The atlas could not be drawn again: {painted.Exception?.GetBaseException()}");
                return;
            }
            PaintResult result = painted.Result;
            if (result.m_saved)
            {
                s_cachedKey = result.m_key;
                s_hasCachedKey = true;
            }
            // Close-ups are drawn from the new record from now on.
            s_record = result.m_record;
            s_tiles.Clear();
            if (s_sheet != null && result.m_key == s_rawKey) return;
            SetSheet(result.m_ink, result.m_record.m_seaMarks.ToArray(), result.m_record.m_trees.ToArray(), result.m_density, result.m_key);
        }

        // A painted (or cached) sheet's planes, kept for the session so its ink texture can be
        // laid again when the glimpse changes (Paper.InkWithMips), with its sea illustrations,
        // density and pins key. The fog is laid again for its sea illustrations.
        private static void SetSheet(byte[] sheet, SeaMark[] marks, Vector4[] trees, InkDensity density, long key)
        {
            s_sheet = sheet;
            s_trees = trees;
            s_sheetMarks = marks;
            s_sheetDensity = density;
            s_rawKey = key;
            s_seaMarks = marks;
            s_fogDirty = true;
        }

        // Lays the sheet's ink texture from its planes whenever the sheet or the glimpse changed.
        // The first is laid only once the fog has laid its glimpse (or failed), so the atlas is not
        // laid twice after login.
        private static void KeepMips()
        {
            if (s_mipsTask != null)
            {
                if (!s_mipsTask.IsCompleted) return;
                Task<byte[]> done = s_mipsTask;
                s_mipsTask = null;
                if (done.IsFaulted)
                {
                    s_mipsFailedSheet = s_mipsSheet;
                    if (!s_ink) Fail(done.Exception);
                    else Jotunn.Logger.LogWarning($"The atlas could not lay its ink: {done.Exception?.GetBaseException()}");
                    return;
                }
                s_inkSheet = s_mipsSheet;
                s_inkGlimpse = s_mipsGlimpse;
                UploadInk(done.Result, s_mipsDensity, s_mipsKey);
                return;
            }
            byte[] sheet = s_sheet;
            if (sheet == null || sheet == s_mipsFailedSheet) return;
            if (sheet == s_inkSheet && s_inkGlimpse == s_glimpseSerial) return;
            // Laid only from the fog laid for this sheet (its sea illustrations), or without one when
            // the fog failed: laid from an earlier fog it was laid twice after login.
            if (s_fogMarks != s_sheetMarks && !s_fogFailed) return;
            byte[] glimpse = s_glimpseSerial == 0 ? null : s_glimpseCopy;
            SeaMark[] marks = s_sheetMarks;
            s_mipsSheet = sheet;
            s_mipsGlimpse = s_glimpseSerial;
            s_mipsDensity = s_sheetDensity;
            s_mipsKey = s_rawKey;
            s_mipsTask = Workers.Run(() => Paper.InkWithMips(sheet, marks, glimpse));
        }

        // The sheet's planes (SetSheet), and the planes and glimpse (s_glimpseSerial) the
        // uploaded ink and the ink being laid were made from.
        private static byte[] s_sheet;
        private static SeaMark[] s_sheetMarks;
        // The sheet's trees' footprints, so the fog hides those its torn edge would cut.
        private static Vector4[] s_trees;
        private static InkDensity s_sheetDensity;
        private static long s_rawKey;
        private static byte[] s_inkSheet;
        private static int s_inkGlimpse;
        private static byte[] s_mipsSheet;
        private static byte[] s_mipsFailedSheet;
        private static int s_mipsGlimpse;
        // Counts the glimpses the fog laid (0 for none yet), with a copy of the last one that
        // workers may read while the next is being laid.
        private static int s_glimpseSerial;
        private static byte[] s_glimpseCopy;
        private static bool s_fogFailed;

        private sealed class PaintResult
        {
            public byte[] m_ink;
            public InkRecord m_record;
            public InkDensity m_density;
            public long m_key;
            public bool m_saved;
        }

        private sealed class CachedSheet
        {
            public byte[] m_sheet;
            public SeaMark[] m_marks;
            public Vector4[] m_trees;
            public InkDensity m_density;
            public long m_key;
        }

        // The pins drawn into the sheet (SheetPin), rebuilt when the snapshot or the pin settings
        // change: the snapshot's pins with a pen glyph that the settings show (AtlasView.InSheet).
        // The Sacrificial Stones are the game's location pin there, like any other place.
        private static void KeepPins()
        {
            if (!s_pinsDirty) return;
            Minimap minimap = Minimap.instance;
            if (!minimap) return;
            s_pinsDirty = false;
            var pins = new System.Collections.Generic.List<SheetPin>();
            var glimpses = new System.Collections.Generic.List<Vector2>();
            if (s_snapshot != null)
            {
                System.Collections.Generic.Dictionary<string, PinGlyph> drawn = AtlasView.DrawnIcons(minimap);
                foreach (Snapshot.Pin pin in s_snapshot.m_pins)
                {
                    if (!drawn.TryGetValue(pin.m_icon, out PinGlyph glyph) || !AtlasView.InSheet(s_snapshot, pin, glyph)) continue;
                    pins.Add(new SheetPin { m_x = pin.m_position.x, m_z = pin.m_position.z, m_glyph = glyph, m_checked = pin.m_checked });
                    if (AtlasView.Glimpsed(s_snapshot, pin, glyph)) glimpses.Add(new Vector2(pin.m_position.x, pin.m_position.z));
                }
            }
            s_pins = pins.ToArray();
            s_pinsKey = SheetPin.Key(s_pins);
            // The fog opens a glimpse round each of them (Paper.Fog).
            if (!SameGlimpses(glimpses))
            {
                s_glimpses = glimpses.ToArray();
                s_fogDirty = true;
            }
        }

        // The game's marks out in unexplored land, round which the fog opens a glimpse.
        private static Vector2[] s_glimpses = new Vector2[0];

        private static bool SameGlimpses(System.Collections.Generic.List<Vector2> glimpses)
        {
            if (glimpses.Count != s_glimpses.Length) return false;
            for (int i = 0; i < glimpses.Count; i++)
                if (glimpses[i] != s_glimpses[i]) return false;
            return true;
        }

        // A pin setting changed: the pins are chosen again, and the sheet painted again if they differ.
        public static void PinsChanged() => s_pinsDirty = true;

        private static void Fail(AggregateException error)
        {
            s_inkFailed = true;
            Jotunn.Logger.LogError($"The atlas could not be drawn: {error?.GetBaseException()}");
        }

        // A sheet laid again, for new pins or a new glimpse, replaces the old one, in an open
        // atlas too.
        private static void UploadInk(byte[] data, InkDensity density, long key)
        {
            s_density = density;
            s_sheetKey = key;
            Texture2D old = s_ink;
            s_ink = new Texture2D(InkPainter.CanvasSize, InkPainter.CanvasSize, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Atlas ink",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            s_ink.LoadRawTextureData(data);
            // Uploads once and drops the CPU copy; a new sheet is a new texture.
            s_ink.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            s_view.SetSheet(s_ink, s_density);
            if (old) UnityEngine.Object.Destroy(old);
        }

        private static void KeepFog()
        {
            if (s_fogTask != null)
            {
                if (!s_fogTask.IsCompleted) return;
                Task<byte[]> done = s_fogTask;
                s_fogTask = null;
                if (done.IsFaulted)
                {
                    Jotunn.Logger.LogError($"The atlas fog could not be laid: {done.Exception?.GetBaseException()}");
                    // The sheet's ink is laid without a glimpse rather than not at all.
                    s_fogFailed = true;
                    return;
                }
                s_parchment = done.Result;
                s_fogMarks = s_fogTaskMarks;
                UploadFog();
            }
            if (!s_fogDirty) return;
            // Before the sheet is there the fog would be laid for nothing: the atlas opens only
            // with a fog laid for the sheet's sea illustrations, and the sheet's arrival lays it
            // again. Without a sheet coming, it is laid at once.
            if (s_sheet == null && !s_inkFailed && !s_decodeFailed) return;
            s_fogDirty = false;
            Snapshot snapshot = s_snapshot;
            int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
            int size = Paper.FogSize;
            if (s_fogPixels == null) s_fogPixels = new byte[size * size * 4];
            if (s_glimpsePixels == null) s_glimpsePixels = new byte[size * size * 4];
            byte[] pixels = s_fogPixels, glimpse = s_glimpsePixels;
            byte[] parchment = s_parchment;
            SeaMark[] marks = s_fogTaskMarks = s_seaMarks;
            Vector2[] glimpses = s_glimpses;
            Vector4[] trees = s_trees;
            Task<byte[]> parchmentTask = Parchment(seed);
            // The task hands the parchment back instead of storing it, so a task still running
            // when the world changes cannot leave its paper behind for the next world.
            Func<byte[], byte[]> lay = paper =>
            {
                Paper.Fog(pixels, glimpse, paper, snapshot, marks, glimpses, trees);
                return paper;
            };
            // Chained rather than waiting on the parchment: a worker blocked on a task queued
            // behind it would never wake.
            s_fogTask = parchment != null ? Workers.Run(() => lay(parchment)) : Workers.Then(parchmentTask, lay);
        }

        private static Task<byte[]> Parchment(int seed)
        {
            if (s_parchmentTask == null) s_parchmentTask = Workers.Run(() => Paper.Parchment(seed));
            return s_parchmentTask;
        }

        // Uploads the sheet's paper once the wash is done, or the bare parchment if it failed.
        private static void KeepPaper()
        {
            if (s_washTask != null && s_washTask.IsCompleted)
            {
                Task<Paper.Wash> done = s_washTask;
                s_washTask = null;
                if (done.IsFaulted)
                {
                    Jotunn.Logger.LogWarning($"The atlas could not lay its colour wash: {done.Exception?.GetBaseException()}");
                    s_washFailed = true;
                }
                else s_wash = done.Result;
            }
            if (s_paper) return;
            if (s_wash == null && s_washFailed && s_parchment != null) s_wash = Paper.Bare(s_parchment);
            if (s_wash == null) return;
            int size = Paper.FogSize;
            s_paper = new Texture2D(size, size, TextureFormat.RGB24, mipChain: false)
            {
                name = "Atlas paper",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            s_paper.LoadRawTextureData(s_wash.m_sheet);
            s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        }

        // Makes the sheet's dirt once per world and uploads it; the atlas opens without it until it
        // is there, and an open atlas takes it up at once.
        private static void KeepDirt()
        {
            if (s_dirt) return;
            if (s_dirtTask == null)
            {
                int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
                s_dirtTask = Workers.Run(() => Paper.Dirt(seed));
                return;
            }
            if (!s_dirtTask.IsCompleted) return;
            if (s_dirtTask.IsFaulted)
            {
                Jotunn.Logger.LogWarning($"The atlas could not age its sheet: {s_dirtTask.Exception?.GetBaseException()}");
                s_dirtTask = Task.FromResult<byte[]>(null);
                return;
            }
            if (s_dirtTask.Result == null) return;
            int size = Paper.FogSize;
            s_dirt = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "Atlas dirt",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            s_dirt.LoadRawTextureData(s_dirtTask.Result);
            s_dirt.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            s_dirtTask = null;
            s_view.SetDirt(s_dirt);
        }

        private static void UploadFog()
        {
            int size = Paper.FogSize;
            if (!s_fog)
            {
                s_fog = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
                {
                    name = "Atlas fog",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }
            s_fog.LoadRawTextureData(s_fogPixels);
            s_fog.Apply(updateMipmaps: false);
            if (!s_glimpse)
            {
                s_glimpse = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
                {
                    name = "Atlas glimpse",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }
            s_glimpse.LoadRawTextureData(s_glimpsePixels);
            s_glimpse.Apply(updateMipmaps: false);
            s_view.SetGlimpse(s_glimpse);
            // The sheet's ink and close-ups lay the same bare parchment and greyer ink in the
            // glimpse, so they are laid again for the new fog (KeepMips).
            s_glimpseCopy = (byte[])s_glimpsePixels.Clone();
            s_glimpseSerial++;
            s_tiles.SetGlimpse(s_glimpseCopy, (byte[])s_fogPixels.Clone());
            if (s_view.IsOpen) s_view.Refresh(s_fog, s_snapshot);
        }

        // The glimpse round the explored land and the game's marks out in the unexplored:
        // bare parchment with no wash over the paper, under the ink (Paper.Fog).
        private static byte[] s_glimpsePixels;
        private static Texture2D s_glimpse;

        // Forgets everything of the last world. Tasks still running are simply dropped: their
        // results were only ever reachable through the fields cleared here.
        private static void Reset()
        {
            s_cancel.Cancel();
            s_cancel.Dispose();
            s_cancel = new CancellationTokenSource();
            s_view.Clear();
            s_worldUid = 0L;
            s_playerId = 0L;
            s_snapshot = null;
            s_cacheTask = null;
            s_cacheStarted = false;
            s_decodeTask = null;
            s_paintTask = null;
            s_mipsTask = null;
            s_washTask = null;
            s_washFailed = false;
            s_wash = null;
            s_record = null;
            s_density = null;
            s_inkFailed = false;
            s_paintError = null;
            s_pins = new SheetPin[0];
            s_pinsKey = 0L;
            s_pinsDirty = true;
            s_sheetKey = 0L;
            s_cachedKey = 0L;
            s_hasCachedKey = false;
            s_paintKey = 0L;
            s_hasFailedKey = false;
            s_mipsDensity = null;
            s_decodeFailed = false;
            s_tiles.Clear();
            s_sparkles.Clear();
            s_source = null;
            s_samples = null;
            s_fogTask = null;
            s_fogDirty = false;
            s_parchment = null;
            s_parchmentTask = null;
            s_dirtTask = null;
            s_fogPixels = null;
            s_seaMarks = null;
            s_fogMarks = null;
            s_fogTaskMarks = null;
            if (s_ink) UnityEngine.Object.Destroy(s_ink);
            if (s_paper) UnityEngine.Object.Destroy(s_paper);
            if (s_fog) UnityEngine.Object.Destroy(s_fog);
            if (s_glimpse) UnityEngine.Object.Destroy(s_glimpse);
            s_glimpse = null;
            s_glimpsePixels = null;
            s_glimpses = new Vector2[0];
            s_sheet = null;
            s_sheetMarks = null;
            s_trees = null;
            s_sheetDensity = null;
            s_rawKey = 0L;
            s_inkSheet = null;
            s_inkGlimpse = 0;
            s_mipsSheet = null;
            s_mipsFailedSheet = null;
            s_mipsGlimpse = 0;
            s_glimpseSerial = 0;
            s_glimpseCopy = null;
            s_fogFailed = false;
            if (s_dirt) UnityEngine.Object.Destroy(s_dirt);
            s_dirt = null;
            s_ink = null;
            s_paper = null;
            s_fog = null;
        }
    }
}
