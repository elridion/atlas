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
        private static Task<byte[]> s_inkTask;
        // Cancelled when the world changes, so a half-finished drawing for the old world stops.
        private static CancellationTokenSource s_cancel = new CancellationTokenSource();
        private static bool s_inkPainting;
        private static bool s_inkFailed;
        private static Texture2D s_ink;

        private static byte[] s_parchment;
        private static byte[] s_fogPixels;
        private static Task<byte[]> s_fogTask;
        private static bool s_fogDirty;
        private static Texture2D s_paper;
        private static Texture2D s_fog;

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

        // Loads the world's ink sheet from the cache, or draws it once vanilla's minimap is ready.
        private static void KeepInk(long worldUid)
        {
            if (s_ink || s_inkFailed) return;
            if (s_inkTask == null)
            {
                int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
                s_inkTask = Task.Run(() =>
                {
                    byte[] ink = InkCache.Load(worldUid, seed);
                    return ink == null ? null : Paper.InkWithMips(ink);
                });
                return;
            }
            if (!s_inkTask.IsCompleted) return;
            if (s_inkTask.IsFaulted)
            {
                s_inkFailed = true;
                Jotunn.Logger.LogError($"The atlas could not be drawn: {s_inkTask.Exception?.GetBaseException()}");
                return;
            }
            byte[] data = s_inkTask.Result;
            if (data != null)
            {
                s_inkTask = null;
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
                return;
            }
            if (s_inkPainting)
            {
                // The painting task itself came back without a sheet.
                s_inkFailed = true;
                return;
            }
            if (!WorldSource.IsReady(Minimap.instance)) return;
            WorldSource source = WorldSource.Capture(Minimap.instance);
            CancellationToken cancel = s_cancel.Token;
            s_inkPainting = true;
            s_inkTask = Task.Run(() =>
            {
                source.Decode();
                byte[] ink = InkPainter.Paint(source, cancel);
                InkCache.Save(worldUid, source.m_seed, ink);
                return Paper.InkWithMips(ink);
            });
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
            // The task hands the parchment back instead of storing it, so a task still running
            // when the world changes cannot leave its paper behind for the next world.
            s_fogTask = Task.Run(() =>
            {
                byte[] paper = parchment ?? Paper.Parchment(seed);
                Paper.Fog(pixels, paper, snapshot);
                return paper;
            });
        }

        private static void UploadFog()
        {
            int size = Paper.FogSize;
            if (!s_paper)
            {
                s_paper = new Texture2D(size, size, TextureFormat.RGB24, mipChain: false)
                {
                    name = "Atlas paper",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                s_paper.LoadRawTextureData(s_parchment);
                s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            }
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
            s_inkTask = null;
            s_inkPainting = false;
            s_inkFailed = false;
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
