using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Where a pin's name goes: at one of the eight classic positions round its icon (right, top
    /// right, bottom right, left, top left, bottom left, above, below), clear of every icon and
    /// name already placed, and on the clearest ground among them. Names carry no backing, only a
    /// thin gap round their letters; placing them where the sheet holds least ink is how
    /// hand-drawn maps keep names legible (Imhof). The classic order only breaks near ties, and a
    /// name keeps its last position unless another is clearly better, so it does not hop while
    /// zooming. Canvas units, y running up. Pure math, so the preview places names the same way.
    /// </summary>
    internal static class PinLayout
    {
        public const int Places = 8;
        // Ink share added per step down the classic order, and taken off the last position.
        private const float OrderCost = 0.012f;
        private const float Stay = 0.04f;

        public static Rect Area(Vector2 at, Vector2 size, int place, float icon)
        {
            float w = size.x, h = size.y, slant = icon * 0.75f, rise = icon * 0.5f;
            switch (place)
            {
                case 0: return new Rect(at.x + icon, at.y - h * 0.5f, w, h);
                case 1: return new Rect(at.x + slant, at.y + rise, w, h);
                case 2: return new Rect(at.x + slant, at.y - rise - h, w, h);
                case 3: return new Rect(at.x - icon - w, at.y - h * 0.5f, w, h);
                case 4: return new Rect(at.x - slant - w, at.y + rise, w, h);
                case 5: return new Rect(at.x - slant - w, at.y - rise - h, w, h);
                case 6: return new Rect(at.x - w * 0.5f, at.y + icon, w, h);
                default: return new Rect(at.x - w * 0.5f, at.y - icon - h, w, h);
            }
        }

        // -1 when every position overlaps something placed. ink gives the mean ink coverage under
        // an area (0 to 1).
        public static int Choose(Vector2 at, Vector2 size, int last, float icon, float clearance, List<Rect> placed, Func<Rect, float> ink)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int place = 0; place < Places; place++)
            {
                Rect area = Area(at, size, place, icon);
                var padded = new Rect(area.x - clearance, area.y - clearance, area.width + 2f * clearance, area.height + 2f * clearance);
                bool free = true;
                foreach (Rect other in placed)
                {
                    if (!other.Overlaps(padded)) continue;
                    free = false;
                    break;
                }
                if (!free) continue;
                float score = ink(area) + OrderCost * place - (place == last ? Stay : 0f);
                if (score >= bestScore) continue;
                bestScore = score;
                best = place;
            }
            return best;
        }

        // Names on the right read from the icon, on the left they end at it.
        public static int Alignment(int place) => place <= 2 ? -1 : place <= 5 ? 1 : 0;
    }
}
