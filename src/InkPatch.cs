using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The part of the ink texture a lay changed (Paper.LaidInk.m_tiles), packed on a worker into
    /// one small image, so the main thread uploads only that and copies its pieces into the shown
    /// texture on the GPU (Graphics.CopyTexture) instead of uploading all 4096² with its mips:
    /// Texture2D.Apply always uploads the whole texture (25-55 ms of a frame in game). Each
    /// changed tile goes with its levels down to WholeFrom; the levels from there on are tiny and
    /// go whole. Nothing here is checked offline: if the copy is not supported, or fails, the
    /// caller uploads the whole texture (AtlasMap.UploadInk).
    /// </summary>
    internal sealed class InkPatch
    {
        // Levels from this one on (64² and smaller) are copied whole.
        private const int WholeFrom = 6;
        // A tile's levels side by side (256 + 128 + ... + 8 = 504 texels), a strip of
        // Paper.TilePixels rows per tile, and one strip below for the whole levels.
        private const int Width = 512;
        private const int CoarseRows = 64;
        // With more changed tiles the staging image nears the whole texture's upload.
        private const int MostTiles = 24;

        private struct Piece
        {
            public int m_srcX, m_srcY, m_size, m_level, m_dstX, m_dstY;
        }

        private byte[] m_pixels;
        private int m_height;
        private readonly List<Piece> m_pieces = new List<Piece>();

        public int Tiles { get; private set; }

        // Worker thread. The changed tiles of laid and its whole coarse levels, or null when it
        // was laid whole or changed too many tiles: then the whole texture is uploaded.
        public static InkPatch Pack(Paper.LaidInk laid)
        {
            if (laid?.m_tiles == null) return null;
            var tiles = new List<int>();
            for (int t = 0; t < laid.m_tiles.Length; t++)
                if (laid.m_tiles[t]) tiles.Add(t);
            if (tiles.Count > MostTiles) return null;
            var patch = new InkPatch { Tiles = tiles.Count };
            // Nothing changed: the shown texture already holds this lay.
            if (tiles.Count == 0) return patch;
            int n = InkPainter.CanvasSize, tile = Paper.TilePixels, levels = 0;
            for (int size = n; size >= 1; size /= 2) levels++;
            var offsets = new int[levels];
            for (int k = 1; k < levels; k++) offsets[k] = offsets[k - 1] + (n >> (k - 1)) * (n >> (k - 1)) * 4;
            patch.m_height = tiles.Count * tile + CoarseRows;
            patch.m_pixels = new byte[Width * patch.m_height * 4];
            for (int i = 0; i < tiles.Count; i++)
            {
                int tx = tiles[i] % Paper.TilesAcross, ty = tiles[i] / Paper.TilesAcross, at = 0;
                for (int k = 0; k < WholeFrom; k++)
                {
                    int side = tile >> k, size = n >> k;
                    for (int r = 0; r < side; r++)
                        Buffer.BlockCopy(laid.m_data, offsets[k] + ((ty * side + r) * size + tx * side) * 4,
                            patch.m_pixels, ((i * tile + r) * Width + at) * 4, side * 4);
                    patch.m_pieces.Add(new Piece { m_srcX = at, m_srcY = i * tile, m_size = side, m_level = k, m_dstX = tx * side, m_dstY = ty * side });
                    at += side;
                }
            }
            int x = 0, y = tiles.Count * tile;
            for (int k = WholeFrom; k < levels; k++)
            {
                int size = n >> k;
                for (int r = 0; r < size; r++)
                    Buffer.BlockCopy(laid.m_data, offsets[k] + r * size * 4, patch.m_pixels, ((y + r) * Width + x) * 4, size * 4);
                patch.m_pieces.Add(new Piece { m_srcX = x, m_srcY = y, m_size = size, m_level = k, m_dstX = 0, m_dstY = 0 });
                x += size;
            }
            return patch;
        }

        // Main thread. Uploads the packed image once and copies its pieces into ink, which must
        // hold the lay this one changed. False, with ink untouched, when the device cannot copy
        // between textures or ink is not laid out as expected; an exception may leave ink half
        // patched, so the caller then uploads the whole texture.
        public bool CopyInto(Texture2D ink)
        {
            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0) return false;
            int levels = 0;
            for (int size = InkPainter.CanvasSize; size >= 1; size /= 2) levels++;
            if (!ink || ink.width != InkPainter.CanvasSize || ink.height != InkPainter.CanvasSize || ink.mipmapCount != levels) return false;
            if (m_pieces.Count == 0) return true;
            var staging = new Texture2D(Width, m_height, TextureFormat.RGBA32, mipChain: false) { name = "Atlas ink patch" };
            try
            {
                if (staging.graphicsFormat != ink.graphicsFormat) return false;
                staging.LoadRawTextureData(m_pixels);
                staging.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                foreach (Piece p in m_pieces)
                    Graphics.CopyTexture(staging, 0, 0, p.m_srcX, p.m_srcY, p.m_size, p.m_size, ink, 0, p.m_level, p.m_dstX, p.m_dstY);
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }
        }
    }
}
