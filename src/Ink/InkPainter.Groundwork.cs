using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    internal sealed partial class InkPainter
    {
        /// <summary>
        /// The sheet's groundwork that does not depend on the pins, kept from the last paint for
        /// the session so a repaint of the same world for other pins skips it (about 3.5 s of a
        /// 6 s paint on Velen): the coast heights, the land, sea and wood masks, their distance
        /// fields and the islets (BuildCoastHeights, BuildFields), and once a paint gets that far
        /// the wide lakes and the traced water lines (MarkWideLakes, TraceWaterLines).
        /// Keyed by the WorldSource instance, which AtlasMap decodes once per world; nothing else
        /// feeds these stages. The painters never write these arrays after building them, so the
        /// distance fields and heights are shared as they are; the 0/1 masks are kept packed to
        /// save memory and unpacked into each painter's own arrays. About 128 MB for a world.
        /// </summary>
        private sealed class Groundwork
        {
            public WorldSource m_world;
            public float[] m_coast;
            // Per sheet pixel: land 1, sea 2, wood 4.
            public byte[] m_masks;
            public byte[] m_waterDistance, m_landDistance, m_woodDistance, m_majorDistance;
            public int[] m_isletCell;
            public Islet[] m_islets;
            // Set by the first paint that traces the water lines; null until then.
            public volatile Waterwork m_water;
        }

        private sealed class Waterwork
        {
            // Per sheet pixel: wide lake 1, the lake's tier times 2, line land 8.
            public byte[] m_masks;
            public int[] m_lakeCentres;
            public Vector2[][] m_lines;
            public bool[] m_closed;
        }

        private const byte LandBit = 1, SeaBit = 2, WoodBit = 4;
        private const byte WideLakeBit = 1, LineLandBit = 8;

        private static readonly object s_groundLock = new object();
        private static Groundwork s_groundwork;

        // The groundwork this painter built or took, so it keeps its water lines with the right one.
        private Groundwork m_groundwork;

        // Drops the kept groundwork (about 128 MB), when the player leaves the world.
        public static void ClearGroundwork()
        {
            lock (s_groundLock) s_groundwork = null;
        }

        // Takes the coast and the fields from the last paint of this world; false when there is none.
        private bool TakeGroundwork()
        {
            Groundwork kept;
            lock (s_groundLock) kept = s_groundwork;
            if (kept == null || kept.m_world != m_world) return false;
            m_groundwork = kept;
            m_coast = kept.m_coast;
            m_waterDistance = kept.m_waterDistance;
            m_landDistance = kept.m_landDistance;
            m_woodDistance = kept.m_woodDistance;
            m_majorDistance = kept.m_majorDistance;
            m_isletCell = kept.m_isletCell;
            m_islets.AddRange(kept.m_islets);
            int n = CanvasSize;
            m_land = new byte[n * n];
            m_sea = new byte[n * n];
            m_wood = new byte[n * n];
            byte[] masks = kept.m_masks;
            Parallel.For(0, n, Workers.LoopOptions(m_cancel), y =>
            {
                for (int i = y * n; i < (y + 1) * n; i++)
                {
                    byte bits = masks[i];
                    m_land[i] = (byte)(bits & LandBit);
                    m_sea[i] = (byte)((bits & SeaBit) >> 1);
                    m_wood[i] = (byte)((bits & WoodBit) >> 2);
                }
            });
            return true;
        }

        private void KeepGroundwork()
        {
            int n = CanvasSize;
            var masks = new byte[n * n];
            Parallel.For(0, n, Workers.LoopOptions(m_cancel), y =>
            {
                for (int i = y * n; i < (y + 1) * n; i++)
                    masks[i] = (byte)(m_land[i] | (m_sea[i] << 1) | (m_wood[i] << 2));
            });
            var kept = new Groundwork
            {
                m_world = m_world,
                m_coast = m_coast,
                m_masks = masks,
                m_waterDistance = m_waterDistance,
                m_landDistance = m_landDistance,
                m_woodDistance = m_woodDistance,
                m_majorDistance = m_majorDistance,
                m_isletCell = m_isletCell,
                m_islets = m_islets.ToArray(),
            };
            m_groundwork = kept;
            lock (s_groundLock) s_groundwork = kept;
        }

        // The wide lakes and the water lines kept with this painter's groundwork, the lines as a
        // copy of their own; null when no paint traced them yet.
        private List<ContourLines.Line> TakeWaterwork()
        {
            Waterwork water = m_groundwork?.m_water;
            if (water == null) return null;
            int n = CanvasSize;
            m_wideLake = new byte[n * n];
            m_lakeTier = new byte[n * n];
            m_lineLand = new byte[n * n];
            byte[] masks = water.m_masks;
            Parallel.For(0, n, Workers.LoopOptions(m_cancel), y =>
            {
                for (int i = y * n; i < (y + 1) * n; i++)
                {
                    byte bits = masks[i];
                    m_wideLake[i] = (byte)(bits & WideLakeBit);
                    m_lakeTier[i] = (byte)((bits >> 1) & 3);
                    m_lineLand[i] = (byte)((bits & LineLandBit) >> 3);
                }
            });
            m_lakeCentres.AddRange(water.m_lakeCentres);
            var lines = new List<ContourLines.Line>(water.m_lines.Length);
            for (int k = 0; k < water.m_lines.Length; k++)
            {
                var line = new ContourLines.Line { m_closed = water.m_closed[k] };
                line.m_points.AddRange(water.m_lines[k]);
                lines.Add(line);
            }
            return lines;
        }

        private void KeepWaterwork(List<ContourLines.Line> lines)
        {
            if (m_groundwork == null) return;
            int n = CanvasSize;
            var masks = new byte[n * n];
            Parallel.For(0, n, Workers.LoopOptions(m_cancel), y =>
            {
                for (int i = y * n; i < (y + 1) * n; i++)
                    masks[i] = (byte)(m_wideLake[i] | (m_lakeTier[i] << 1) | (m_lineLand[i] << 3));
            });
            var water = new Waterwork
            {
                m_masks = masks,
                m_lakeCentres = m_lakeCentres.ToArray(),
                m_lines = new Vector2[lines.Count][],
                m_closed = new bool[lines.Count],
            };
            for (int k = 0; k < lines.Count; k++)
            {
                water.m_lines[k] = lines[k].m_points.ToArray();
                water.m_closed[k] = lines[k].m_closed;
            }
            m_groundwork.m_water = water;
        }
    }
}
