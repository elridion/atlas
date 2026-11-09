using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// What a repaint for other pins (InkPainter.Repaint) changed for close-ups: everything a
    /// close-up reads from its record that differs between the old record and the new, as the
    /// sheet areas over which it is read. A close-up whose reads miss all of it draws the same
    /// from either record, so DetailTiles keeps it (NewRecord); the fog's decisions for the new
    /// marks are compared on their own when they come (FogChange). The one non-obvious part:
    /// a close-up replays every glyph group it picks whole and decides its own trees with every
    /// pin that can reach them, so a change counts over the whole group and the pin's
    /// influence, not only where its ink lies. Made on the worker that painted; the main thread
    /// only tests tiles against it.
    /// </summary>
    internal sealed class RecordChange
    {
        // The record the change was made from: tiles drawn from any other are dropped regardless.
        public InkRecord m_from;
        private int m_across;
        // Index cells whose replayed calls, groups or feet differ (InkRecord.TileHashes), and
        // those whose river strokes do.
        private bool[] m_cells;
        private bool[] m_rivers;
        // The index cells (x, y the first, width and height the count) of every group with a
        // call in a changed cell that the other record has not alike: a close-up picking any of
        // its calls replays it all.
        private readonly List<RectInt> m_groups = new List<RectInt>();
        // Sheet areas over which a close-up's region reads a changed list entry (peaks, mist
        // banks, glyph footprints, waves, sea washes), already grown by its reach.
        private readonly List<Rect> m_reads = new List<Rect>();
        // The influence (InkPainter.PinInfluence round its place) of every pin added, removed,
        // moved, ticked, reordered or given another order or relief cover: a close-up scattering
        // trees near one decides them otherwise (InkPainter.ScatterArea, LimitMarkCover).
        private readonly List<Rect> m_pins = new List<Rect>();

        public int ChangedCells { get; private set; }

        // Worker thread. The change from the sealed record from to the sealed record to, or null
        // when they cannot be compared: then every close-up is drawn again.
        public static RecordChange Between(InkRecord from, InkRecord to)
        {
            if (from == null || to == null || from.CellsAcross == 0 || from.CellsAcross != to.CellsAcross) return null;
            int across = from.CellsAcross;
            var change = new RecordChange { m_from = from, m_across = across, m_cells = new bool[across * across], m_rivers = new bool[across * across] };
            ulong[] before = from.TileHashes(out ulong[] beforeRivers), after = to.TileHashes(out ulong[] afterRivers);
            var seenFrom = new HashSet<int>();
            var seenTo = new HashSet<int>();
            var groupsFrom = new List<int>();
            var groupsTo = new List<int>();
            for (int i = 0; i < before.Length; i++)
            {
                if (beforeRivers[i] != afterRivers[i]) change.m_rivers[i] = true;
                if (before[i] == after[i]) continue;
                change.m_cells[i] = true;
                change.ChangedCells++;
                from.GroupsNear(i % across, i / across, seenFrom, groupsFrom);
                to.GroupsNear(i % across, i / across, seenTo, groupsTo);
            }
            // The groups over the changed cells that are not the same in both records (their
            // calls, in order, and foot): a close-up picking one elsewhere replays its changed
            // part too. One alike in both is replayed alike wherever it is picked; the cells it
            // shares with others are compared above.
            var hashFrom = new ulong[groupsFrom.Count];
            var hashTo = new ulong[groupsTo.Count];
            for (int k = 0; k < hashFrom.Length; k++) hashFrom[k] = from.GroupHash(groupsFrom[k]);
            for (int k = 0; k < hashTo.Length; k++) hashTo[k] = to.GroupHash(groupsTo[k]);
            Diff(hashFrom.Length, hashTo.Length, false, (a, b) => hashFrom[a] == hashTo[b], a => hashFrom[a].GetHashCode(), b => hashTo[b].GetHashCode(),
                a => from.AddGroupCells(groupsFrom[a], change.m_groups), b => to.AddGroupCells(groupsTo[b], change.m_groups));
            if (!to.AddChangedLists(from, change.m_reads)) return null;
            AddChangedPins(from, to, change.m_pins);
            return change;
        }

        // A pin as a close-up takes it: its place, glyph and tick, and its order and relief cover
        // from the record (InkPainter.FindMarks, TakeMarkOrders).
        private static void AddChangedPins(InkRecord from, InkRecord to, List<Rect> into)
        {
            SheetPin[] a = from.m_pins ?? new SheetPin[0], b = to.m_pins ?? new SheetPin[0];
            // In order: the marks share the trees they limit (LimitMarkCover), pin by pin.
            Diff(a.Length, b.Length, true, (i, j) => SamePin(a[i], b[j]) && SameOrder(from, i, to, j),
                i => PinKey(a[i]), j => PinKey(b[j]),
                i => into.Add(Influence(a[i])), j => into.Add(Influence(b[j])));
        }

        private static bool SamePin(SheetPin a, SheetPin b) =>
            a.m_x == b.m_x && a.m_z == b.m_z && a.m_glyph == b.m_glyph && a.m_checked == b.m_checked;

        // The order and cover a close-up takes for pin i, alike in both records (TakeMarkOrders
        // takes them only when there is one per pin).
        private static bool SameOrder(InkRecord a, int i, InkRecord b, int j)
        {
            bool hasA = a.m_pinOrders != null && a.m_pinCovers != null && a.m_pinOrders.Length == a.m_pins.Length;
            bool hasB = b.m_pinOrders != null && b.m_pinCovers != null && b.m_pinOrders.Length == b.m_pins.Length;
            if (hasA != hasB) return false;
            if (!hasA) return true;
            if (a.m_pinOrders[i] != b.m_pinOrders[j]) return false;
            bool[] ca = a.m_pinCovers[i], cb = b.m_pinCovers[j];
            if (ca == null || cb == null) return ca == cb;
            if (ca.Length != cb.Length) return false;
            for (int k = 0; k < ca.Length; k++)
                if (ca[k] != cb[k]) return false;
            return true;
        }

        private static int PinKey(SheetPin pin) => pin.m_x.GetHashCode() * 31 + pin.m_z.GetHashCode();

        private static Rect Influence(SheetPin pin)
        {
            float x = (pin.m_x + InkPainter.HalfExtent) / InkPainter.MetresPerPixel, y = (InkPainter.HalfExtent - pin.m_z) / InkPainter.MetresPerPixel;
            return Rect.MinMaxRect(x - InkPainter.PinInfluence, y - InkPainter.PinInfluence, x + InkPainter.PinInfluence, y + InkPainter.PinInfluence);
        }

        // Compares two lists by index (counts a and b; same tells entries alike, keyA and keyB
        // hash them) and calls changedA and changedB for the entries of either that the other
        // has not alike, matched as multisets, duplicates one for one. With ordered, an entry
        // matched out of the order of the others counts as changed too (in both lists). Lists
        // alike entry for entry cost one pass.
        internal static void Diff(int a, int b, bool ordered, Func<int, int, bool> same, Func<int, int> keyA, Func<int, int> keyB,
            Action<int> changedA, Action<int> changedB)
        {
            if (a == b)
            {
                bool all = true;
                for (int i = 0; i < a && all; i++) all = same(i, i);
                if (all) return;
            }
            var buckets = new Dictionary<int, List<int>>();
            for (int i = 0; i < a; i++)
            {
                int key = keyA(i);
                if (!buckets.TryGetValue(key, out List<int> bucket)) buckets[key] = bucket = new List<int>(1);
                bucket.Add(i);
            }
            var match = new int[a];
            for (int i = 0; i < a; i++) match[i] = -1;
            for (int j = 0; j < b; j++)
            {
                int found = -1;
                if (buckets.TryGetValue(keyB(j), out List<int> bucket))
                {
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        if (!same(bucket[k], j)) continue;
                        found = bucket[k];
                        bucket.RemoveAt(k);
                        break;
                    }
                }
                if (found < 0) changedB(j);
                else match[found] = j;
            }
            int last = -1;
            for (int i = 0; i < a; i++)
            {
                if (match[i] < 0)
                {
                    changedA(i);
                    continue;
                }
                if (ordered && match[i] < last)
                {
                    changedA(i);
                    changedB(match[i]);
                }
                else
                {
                    last = match[i];
                }
            }
        }

        private int CellOf(float coordinate) => Mathf.Clamp((int)(coordinate / InkRecord.CellSize), 0, m_across - 1);

        // Whether a close-up of region (sheet pixels) at scale, drawn from m_from, can differ
        // from one drawn from the new record (record). The cells it picks are those InkRecord's
        // queries over its region pick (its edges inclusive), wider for the river strokes; its
        // trees reach as far as the fog's reads do (DetailTile.FogReach).
        public bool Affects(InkRecord record, RectInt region, int scale)
        {
            int x0 = CellOf(region.xMin), x1 = CellOf(region.xMax), y0 = CellOf(region.yMin), y1 = CellOf(region.yMax);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (m_cells[y * m_across + x]) return true;
            foreach (RectInt g in m_groups)
                if (g.xMin <= x1 && x0 <= g.xMax - 1 && g.yMin <= y1 && y0 <= g.yMax - 1) return true;
            float river = InkPainter.TileRiverReach;
            int rx0 = CellOf(region.xMin - river), rx1 = CellOf(region.xMax + river), ry0 = CellOf(region.yMin - river), ry1 = CellOf(region.yMax + river);
            for (int y = ry0; y <= ry1; y++)
                for (int x = rx0; x <= rx1; x++)
                    if (m_rivers[y * m_across + x]) return true;
            foreach (Rect r in m_reads)
                if (r.xMin <= region.xMax && region.xMin <= r.xMax && r.yMin <= region.yMax && region.yMin <= r.yMax) return true;
            if (m_pins.Count == 0) return false;
            Rect reach = DetailTile.FogReach(record, region, scale);
            foreach (Rect r in m_pins)
                if (r.xMin <= reach.xMax && reach.xMin <= r.xMax && r.yMin <= reach.yMax && reach.yMin <= r.yMax) return true;
            return false;
        }
    }
}
