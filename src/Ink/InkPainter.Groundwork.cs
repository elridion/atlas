using System;
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
            // Set by the first paint that gets past its pin-free stages; null until then.
            public volatile Startwork m_start;
        }

        /// <summary>
        /// Everything a paint has done when its pin-free stages end (StartPinFree: the mist, the
        /// ground fills laid before any glyph, lava, rivers, coast, and the placing of every glyph
        /// but the pins), kept with the groundwork so a repaint for other pins starts at the pins:
        /// a repaint went from 6.3 to 3.7 s in game with it. No pin is read before that point: the
        /// only test of pins while placing is ClearOfMarks in Add, which passes relief, trees and
        /// dead trees (GivesWayAsTree), so pins limit glyphs only afterwards (PlaceGlyphs), where
        /// ClearOfMarks can still take out mist banks alone. The
        /// canvas planes are kept whole (bodies that are still blank as null), the glyph and mark
        /// lists as copies, the record's calls as InkRecord.Start; the peak bases and mist banks
        /// are filed again from the record's own lists, which were added in the same order.
        /// The first paint from it also keeps what comes later but reads no pins either: the
        /// waves as laid out (m_waves) and the open-water spots the sea's marks are placed on
        /// (m_seaSpots); and every paint keeps its sea marks' placement with the ink tests it made
        /// (m_sea), which the next paint reuses where no test can come out otherwise.
        /// </summary>
        private sealed class Startwork
        {
            // The sheet's waves in drawing order (DrawWaves): their layout reads the groundwork,
            // the water lines and the start's thorns and wet spires, never a pin or the ink.
            public volatile InkRecord.Wave[] m_waves;
            // The sea marks' candidate spots in rank order (DrawIllustrations).
            public volatile OpenSpot[] m_seaSpots;
            // The last placement of the sea marks (SeaPlan), replaced by each paint that places
            // them again.
            public volatile SeaPlan m_sea;
            public byte[][] m_planes;
            public byte[] m_riverMask;
            public Glyph[] m_glyphs;
            public Glyph[] m_mist;
            public Vector4[] m_summits;
            public Vector4[] m_hillBoxes;
            public Vector3[] m_spires;
            public float[] m_massifPeaks;
            public Glyph[] m_thorns;
            public Glyph[] m_floes;
            public InkRecord.Start m_record;
        }

        private byte[][] CanvasPlanes() => new[]
        {
            m_canvas.m_ink, m_canvas.m_body, m_canvas.m_bodyNorth, m_canvas.m_bodyMist, m_canvas.m_bodyWood,
        };

        // Starts this paint where the last paint of the world left its pin-free stages; false when
        // there is none (or this paint records nothing, as the kept start includes its record).
        private bool TakeStartwork()
        {
            Startwork start = m_groundwork?.m_start;
            if (start == null || m_record == null || m_record.Count != 0) return false;
            m_start = start;
            // A record-only canvas has no planes to restore (Repaint).
            byte[][] planes = CanvasPlanes();
            for (int k = 0; k < planes.Length && !m_canvas.RecordsOnly; k++)
                if (start.m_planes[k] != null) Buffer.BlockCopy(start.m_planes[k], 0, planes[k], 0, planes[k].Length);
            m_riverMask = start.m_riverMask;
            m_glyphs.AddRange(start.m_glyphs);
            m_mist.AddRange(start.m_mist);
            m_summits.AddRange(start.m_summits);
            m_hillBoxes.AddRange(start.m_hillBoxes);
            m_spires.AddRange(start.m_spires);
            m_massifPeaks.AddRange(start.m_massifPeaks);
            m_thorns.AddRange(start.m_thorns);
            m_floes.AddRange(start.m_floes);
            start.m_record.CopyInto(m_record);
            foreach (Vector3 bank in start.m_record.m_mistBanks) AddMistBank(bank);
            foreach (Vector4 peak in start.m_record.m_peaks) AddPeakBase(peak);
            return true;
        }

        // A plain loop: Array.Exists calls a delegate per byte, about 100 ms under Mono over the
        // four body planes, which are still blank here and so scanned to the end.
        private static bool IsBlank(byte[] plane)
        {
            for (int i = 0; i < plane.Length; i++)
                if (plane[i] != 0) return false;
            return true;
        }

        private void KeepStartwork()
        {
            if (m_groundwork == null || m_record == null || m_record.CurrentGroup != -1 || m_canvas.RecordsOnly) return;
            byte[][] planes = CanvasPlanes();
            var kept = new byte[planes.Length][];
            for (int k = 0; k < planes.Length; k++)
                if (!IsBlank(planes[k])) kept[k] = (byte[])planes[k].Clone();
            m_groundwork.m_start = m_start = new Startwork
            {
                m_planes = kept,
                // Written only by DrawRivers, before this point.
                m_riverMask = m_riverMask,
                m_glyphs = m_glyphs.ToArray(),
                m_mist = m_mist.ToArray(),
                m_summits = m_summits.ToArray(),
                m_hillBoxes = m_hillBoxes.ToArray(),
                m_spires = m_spires.ToArray(),
                m_massifPeaks = m_massifPeaks.ToArray(),
                m_thorns = m_thorns.ToArray(),
                m_floes = m_floes.ToArray(),
                m_record = new InkRecord.Start(m_record),
            };
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
        // The pin-free start this painter took or kept, null when it has none (no record): what it
        // reuses and keeps after the pins goes with it.
        private Startwork m_start;

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
