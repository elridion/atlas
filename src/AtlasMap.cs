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
        // The sheet's paper: the parchment under the biome wash, or the bare parchment if the wash
        // failed. The fog keeps the bare parchment.
        private static Task<byte[]> s_washTask;
        private static bool s_washFailed;
        private static byte[] s_paperPixels;
        private static byte[] s_fogPixels;
        private static Task<byte[]> s_fogTask;
        private static bool s_fogDirty;
        private static Texture2D s_paper;
        private static Texture2D s_fog;

        // Detail tiles: the shown tile, the one being drawn, and the view waiting to settle.
        private const float DetailDelay = 0.15f;
        private static Texture2D s_detail;
        private static RectInt s_detailRegion;
        private static int s_detailScale;
        private static Task<byte[]> s_detailTask;
        private static RectInt s_detailPending;
        private static int s_detailPendingScale;
        private static RectInt s_lastWanted;
        private static int s_lastWantedScale;
        private static float s_wantedSince;
        private static bool s_detailBroken;

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
            KeepDetail();

            if (!s_view.IsOpen && s_view.ClosedFrames > 2 && OpenPressed() && CanOpen(player)) Open(player);
            s_view.Update();
        }

        // A cartography table recorded the player's discoveries: the atlas now shows them.
        public static void Record()
        {
            Player player = Player.m_localPlayer;
            Minimap minimap = Minimap.instance;
            if (!player || !minimap || !ZNet.instance) return;
            s_snapshot = Snapshot.Take(minimap);
            s_snapshot.Save(player, ZNet.instance.GetWorldUID());
            s_fogDirty = true;
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
                CancellationToken cancel = s_cancel.Token;
                int seed = source.m_seed;
                s_washTask = Task.Run(() => Paper.Washed(Paper.Parchment(seed), source));
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

        // While the atlas is open and zoomed in, keeps a sharp detail tile over the part in view:
        // once the view has held still for DetailDelay seconds and the shown tile no longer covers
        // it at the right magnification, the visible part (with a margin) is inked again.
        private static void KeepDetail()
        {
            if (s_detailTask != null)
            {
                if (!s_detailTask.IsCompleted) return;
                Task<byte[]> done = s_detailTask;
                s_detailTask = null;
                if (done.IsFaulted)
                {
                    Jotunn.Logger.LogWarning($"An atlas close-up could not be drawn: {done.Exception?.GetBaseException()}");
                    s_detailBroken = true;
                    return;
                }
                ShowDetail(done.Result, s_detailPending, s_detailPendingScale);
            }
            if (!s_view.IsOpen || s_record == null || s_paperPixels == null || s_detailBroken) return;
            if (!s_view.DetailWanted(out RectInt visible, out int scale))
            {
                s_view.HideDetail();
                return;
            }
            if (s_detail && s_detailScale == scale && Contains(s_detailRegion, visible)) return;
            if (visible != s_lastWanted || scale != s_lastWantedScale)
            {
                s_lastWanted = visible;
                s_lastWantedScale = scale;
                s_wantedSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - s_wantedSince < DetailDelay) return;

            int margin = Mathf.Max(visible.width, visible.height) / 4;
            int left = Mathf.Max(0, visible.xMin - margin), top = Mathf.Max(0, visible.yMin - margin);
            int right = Mathf.Min(InkPainter.CanvasSize, visible.xMax + margin), bottom = Mathf.Min(InkPainter.CanvasSize, visible.yMax + margin);
            if (right <= left || bottom <= top) return;
            var region = new RectInt(left, top, right - left, bottom - top);
            InkRecord record = s_record;
            byte[] paper = s_paperPixels;
            s_detailPending = region;
            s_detailPendingScale = scale;
            s_detailTask = Task.Run(() => DetailTile.Render(record, paper, region.x, region.y, region.width, region.height, scale));
        }

        private static void ShowDetail(byte[] rgba, RectInt region, int scale)
        {
            int w = region.width * scale, h = region.height * scale;
            if (!s_detail || s_detail.width != w || s_detail.height != h)
            {
                if (s_detail) UnityEngine.Object.Destroy(s_detail);
                s_detail = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
                {
                    name = "Atlas detail",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }
            s_detail.LoadRawTextureData(rgba);
            s_detail.Apply(updateMipmaps: false);
            s_detailRegion = region;
            s_detailScale = scale;
            s_view.ShowDetail(s_detail, region);
        }

        private static bool Contains(RectInt outer, RectInt inner) =>
            inner.xMin >= outer.xMin && inner.yMin >= outer.yMin && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;

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
            // The task hands the parchment back instead of storing it, so a task still running
            // when the world changes cannot leave its paper behind for the next world.
            s_fogTask = Task.Run(() =>
            {
                byte[] paper = parchment ?? Paper.Parchment(seed);
                Paper.Fog(pixels, paper, snapshot);
                return paper;
            });
        }

        // Uploads the sheet's paper once the wash is done, or the bare parchment if it failed.
        private static void KeepPaper()
        {
            if (s_washTask != null && s_washTask.IsCompleted)
            {
                Task<byte[]> done = s_washTask;
                s_washTask = null;
                if (done.IsFaulted)
                {
                    Jotunn.Logger.LogWarning($"The atlas could not lay its colour wash: {done.Exception?.GetBaseException()}");
                    s_washFailed = true;
                }
                else s_paperPixels = done.Result;
            }
            if (s_paper) return;
            if (s_paperPixels == null && s_washFailed) s_paperPixels = s_parchment;
            if (s_paperPixels == null) return;
            int size = Paper.FogSize;
            s_paper = new Texture2D(size, size, TextureFormat.RGB24, mipChain: false)
            {
                name = "Atlas paper",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            s_paper.LoadRawTextureData(s_paperPixels);
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
            s_paperPixels = null;
            s_record = null;
            s_inkFailed = false;
            s_detailTask = null;
            s_detailBroken = false;
            s_lastWanted = default;
            s_lastWantedScale = 0;
            if (s_detail) UnityEngine.Object.Destroy(s_detail);
            s_detail = null;
            s_fogTask = null;
            s_fogDirty = false;
            s_parchment = null;
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
