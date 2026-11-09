using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Ticking or unticking pins (vanilla's PinData.m_checked) without painting the world again.
    /// A tick changes nothing but how the pin's own ink is scraped (PinGlyphs.Abrade): its body,
    /// its place in the drawing order, its cover and every other glyph's placement stay as they
    /// were, so the sheet differs from a full paint only on the pin plane inside the pin's box,
    /// and the record only in that pin's call. Those pixels are redrawn by replaying the patched
    /// record over the box, which inks them exactly as the sheet did: the replay draws every call
    /// touching them in the sheet's order, the glyphs drawn in front scraping the pin's red ink
    /// as they did when the sheet was painted.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // Pixels round a pin's box in the window of the sheet it is replayed on.
        private const int RecheckMargin = 4;

        // Whether two pin lists are the same pins in the same order, at the same places with the
        // same glyphs, and differ only in ticks, at least one.
        public static bool OnlyChecksDiffer(IList<SheetPin> from, IList<SheetPin> to)
        {
            if (from == null || to == null || from.Count != to.Count) return false;
            bool ticked = false;
            for (int i = 0; i < from.Count; i++)
            {
                SheetPin a = from[i], b = to[i];
                if (a.m_x != b.m_x || a.m_z != b.m_z || a.m_glyph != b.m_glyph) return false;
                if (a.m_checked != b.m_checked) ticked = true;
            }
            return ticked;
        }

        // Worker thread. The sheet drawn with pins, from the sheet and its record drawn with the
        // record's pins (OnlyChecksDiffer of the two), and the record for the new pins
        // (rechecked), as Paint would make them; changed gets each redrawn pin's pixels. Null
        // when a ticked pin's call cannot be told apart (two pins on one place): paint instead.
        // Neither the sheet nor the record given is changed, as both may still be in use.
        public static byte[] Recheck(byte[] sheet, InkRecord record, SheetPin[] pins, long key, CancellationToken cancel,
            List<RectInt> changed, out InkRecord rechecked)
        {
            rechecked = null;
            var calls = new List<int>();
            var struck = new List<bool>();
            var boxes = new List<Vector3>();
            for (int i = 0; i < pins.Length; i++)
            {
                if (record.m_pins[i].m_checked == pins[i].m_checked) continue;
                Vector3 box = SheetPinBox(pins[i]);
                int call = record.PinCall(pins[i].m_glyph, box.x, box.y, box.z);
                if (call < 0) return null;
                calls.Add(call);
                struck.Add(pins[i].m_checked);
                boxes.Add(box);
            }
            cancel.ThrowIfCancellationRequested();
            rechecked = record.WithChecks(pins, calls, struck, key);
            var ink = (byte[])sheet.Clone();
            int n = CanvasSize, pinPlane = 5 * n * n;
            foreach (Vector3 box in boxes)
            {
                // The pixels InkCanvas.Pin inks at the sheet's scale.
                int x0 = Mathf.RoundToInt(box.x), y0 = Mathf.RoundToInt(box.y), pixels = Mathf.Max(4, Mathf.RoundToInt(box.z));
                int left = (x0 - RecheckMargin) & ~1, top = (y0 - RecheckMargin) & ~1;
                int width = x0 + pixels + RecheckMargin - left, height = y0 + pixels + RecheckMargin - top;
                // A window of the sheet: a canvas with its own origin rounds the glyphs scraping
                // the pin a hair differently.
                InkCanvas canvas = InkCanvas.Window(n, left, top, width, height);
                rechecked.Replay(canvas, x0 - 1, y0 - 1, x0 + pixels + 1, y0 + pixels + 1);
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(n, y0 + pixels); y++)
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(n, x0 + pixels); x++)
                        ink[pinPlane + y * n + x] = canvas.m_pinInk[(y - top) * width + x - left];
                changed.Add(new RectInt(x0, y0, pixels, pixels));
            }
            return ink;
        }

        // A pin's box on the sheet (left, top, size), as FindMarks places it on the sheet's
        // painter (level of detail 1) and the glyph is drawn (PinBox), to the bit.
        private static Vector3 SheetPinBox(SheetPin pin)
        {
            float x = (pin.m_x + HalfExtent) / MetresPerPixel, y = (HalfExtent - pin.m_z) / MetresPerPixel;
            float size = PinSheetSize(pin.m_glyph) / 1f;
            var g = new Glyph { m_x = x, m_y = y + PinGlyphs.Sink(pin.m_glyph) * size, m_size = size, m_kind = Kind.Pin, m_seed = (int)pin.m_glyph };
            return PinBox(g);
        }
    }
}
