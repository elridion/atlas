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
        private static Task<byte[]> s_cacheTask;
        private static bool s_cacheStarted;
        private static Task<WorldSource> s_decodeTask;
        private static Task<PaintResult> s_paintTask;
        private static Task<byte[]> s_mipsTask;
        private static InkRecord s_record;
        // Cancelled when the world changes, so a half-finished drawing for the old world stops.
        private static CancellationTokenSource s_cancel = new CancellationTokenSource();
        private static bool s_inkFailed;
        private static Texture2D s_ink;

        private static byte[] s_parchment;
        // The parchment, made once per world and shared by the fog and the wash: with its stains
        // it takes a second or two, too long to make twice.
        private static Task<byte[]> s_parchmentTask;
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
        public static bool BlocksGame => s_view.IsOpen || s_view.ClosedFrames <= 2;

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
            }

            KeepInk(worldUid);
            KeepFog();
            KeepPaper();
            if (!s_tiles.Ready && s_record != null && s_wash != null && s_samples != null)
                s_tiles.SetSource(s_record, s_wash, s_source, s_samples);
            s_tiles.Update(s_view);
            // The atlas is up to date once its fog is laid and its sheet is there (or failed).
            s_sparkles.Update(s_fogDirty || s_fogTask != null || (!s_ink && !s_inkFailed));

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
            s_sparkles.Begin(table);
        }

        private static void Open(Player player)
        {
            if (s_snapshot == null)
            {
                player.Message(MessageHud.MessageType.Center, "$atlas_blank");
                return;
            }
            if (!s_ink || !s_paper || !s_fog)
            {
                player.Message(MessageHud.MessageType.Center, s_inkFailed ? "$atlas_failed" : "$atlas_drawing");
                return;
            }
            s_view.Show(s_paper, s_ink, s_fog, s_snapshot);
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
        // world is still painted once per session in the background, because only painting yields
        // the record that detail tiles are drawn from; without a cached sheet, that painting also
        // provides the sheet.
        private static void KeepInk(long worldUid)
        {
            if (s_inkFailed) return;
            if (!s_cacheStarted)
            {
                s_cacheStarted = true;
                int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
                s_cacheTask = Task.Run(() =>
                {
                    byte[] ink = InkCache.Load(worldUid, seed);
                    return ink == null ? null : Paper.InkWithMips(ink);
                });
            }
            if (s_cacheTask != null && s_cacheTask.IsCompleted)
            {
                if (s_cacheTask.IsFaulted) Jotunn.Logger.LogWarning($"The cached atlas could not be loaded: {s_cacheTask.Exception?.GetBaseException()}");
                else if (s_cacheTask.Result != null && !s_ink) UploadInk(s_cacheTask.Result);
                s_cacheTask = null;
            }
            if (s_mipsTask != null && s_mipsTask.IsCompleted)
            {
                if (s_mipsTask.IsFaulted) Fail(s_mipsTask.Exception);
                else if (!s_ink) UploadInk(s_mipsTask.Result);
                s_mipsTask = null;
            }

            if (s_paintTask == null)
            {
                if (s_decodeTask == null)
                {
                    if (s_record != null || !WorldSource.IsReady(Minimap.instance)) return;
                    WorldSource captured = WorldSource.Capture(Minimap.instance);
                    s_decodeTask = Task.Run(() =>
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
                    if (!s_ink && s_cacheTask == null) Fail(decoded.Exception);
                    else Jotunn.Logger.LogWarning($"The atlas could not read the world: {decoded.Exception?.GetBaseException()}");
                    s_paintTask = Task.FromResult<PaintResult>(null);
                    return;
                }
                WorldSource source = decoded.Result;
                s_source = source;
                WorldGenerator generator = WorldGenerator.instance;
                s_samples = new SampleCache(generator != null ? new GeneratorSampler(generator, source.m_waterLevel) : (IWorldSampler)new MinimapSampler(source));
                CancellationToken cancel = s_cancel.Token;
                int seed = source.m_seed;
                Task<byte[]> parchmentTask = Parchment(seed);
                s_washTask = Task.Run(() => Paper.Washed(parchmentTask.Result, source));
                s_paintTask = Task.Run(() =>
                {
                    var record = new InkRecord();
                    byte[] ink = InkPainter.Paint(source, cancel, record);
                    InkCache.Save(worldUid, source.m_seed, ink);
                    return new PaintResult { m_ink = ink, m_record = record };
                });
                return;
            }
            if (!s_paintTask.IsCompleted) return;
            Task<PaintResult> painted = s_paintTask;
            if (painted.IsFaulted)
            {
                // Without a sheet there is nothing to show; with one, only detail tiles are lost.
                if (!s_ink && s_cacheTask == null) Fail(painted.Exception);
                else Jotunn.Logger.LogWarning($"The atlas could not prepare its close-up detail: {painted.Exception?.GetBaseException()}");
                s_paintTask = Task.FromResult<PaintResult>(null);
                return;
            }
            if (painted.Result == null) return;
            s_record = painted.Result.m_record;
            byte[] sheet = painted.Result.m_ink;
            s_paintTask = Task.FromResult<PaintResult>(null);
            if (!s_ink && s_mipsTask == null) s_mipsTask = Task.Run(() => Paper.InkWithMips(sheet));
        }

        private sealed class PaintResult
        {
            public byte[] m_ink;
            public InkRecord m_record;
        }

        private static void Fail(AggregateException error)
        {
            s_inkFailed = true;
            Jotunn.Logger.LogError($"The atlas could not be drawn: {error?.GetBaseException()}");
        }

        private static void UploadInk(byte[] data)
        {
            s_ink = new Texture2D(InkPainter.CanvasSize, InkPainter.CanvasSize, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Atlas ink",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            s_ink.LoadRawTextureData(data);
            // Uploads once and drops the CPU copy; the sheet never changes for this world.
            s_ink.Apply(updateMipmaps: false, makeNoLongerReadable: true);
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
                    return;
                }
                s_parchment = done.Result;
                UploadFog();
            }
            if (!s_fogDirty) return;
            s_fogDirty = false;
            Snapshot snapshot = s_snapshot;
            int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
            int size = Paper.FogSize;
            if (s_fogPixels == null) s_fogPixels = new byte[size * size * 4];
            byte[] pixels = s_fogPixels;
            byte[] parchment = s_parchment;
            Task<byte[]> parchmentTask = Parchment(seed);
            // The task hands the parchment back instead of storing it, so a task still running
            // when the world changes cannot leave its paper behind for the next world.
            s_fogTask = Task.Run(() =>
            {
                byte[] paper = parchment ?? parchmentTask.Result;
                Paper.Fog(pixels, paper, snapshot);
                return paper;
            });
        }

        private static Task<byte[]> Parchment(int seed)
        {
            if (s_parchmentTask == null) s_parchmentTask = Task.Run(() => Paper.Parchment(seed));
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
            if (s_view.IsOpen) s_view.Refresh(s_fog, s_snapshot);
        }

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
            s_inkFailed = false;
            s_tiles.Clear();
            s_sparkles.Clear();
            s_source = null;
            s_samples = null;
            s_fogTask = null;
            s_fogDirty = false;
            s_parchment = null;
            s_parchmentTask = null;
            s_fogPixels = null;
            if (s_ink) UnityEngine.Object.Destroy(s_ink);
            if (s_paper) UnityEngine.Object.Destroy(s_paper);
            if (s_fog) UnityEngine.Object.Destroy(s_fog);
            s_ink = null;
            s_paper = null;
            s_fog = null;
        }
    }
}
