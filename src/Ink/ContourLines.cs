using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Joins the loose segments of a marching-squares contour into lines, so a contour can be
    /// smoothed and its short stubs dropped before it is inked. Neighbouring cells compute a shared
    /// end from the same two corner values, so ends are matched on a fine rounded key rather than
    /// exactly.
    /// </summary>
    internal sealed class ContourLines
    {
        public sealed class Line
        {
            public readonly List<Vector2> m_points = new List<Vector2>();
            public bool m_closed;

            public float Length()
            {
                float total = 0f;
                for (int i = 1; i < m_points.Count; i++) total += Vector2.Distance(m_points[i - 1], m_points[i]);
                return total;
            }
        }

        private readonly List<Vector2> m_from = new List<Vector2>();
        private readonly List<Vector2> m_to = new List<Vector2>();

        public void Add(Vector2 a, Vector2 b)
        {
            m_from.Add(a);
            m_to.Add(b);
        }

        private static long Key(Vector2 p) => ((long)Mathf.RoundToInt(p.x * 64f) << 32) ^ (uint)Mathf.RoundToInt(p.y * 64f);

        public List<Line> Build()
        {
            int count = m_from.Count;
            var ends = new Dictionary<long, List<int>>(count * 2);
            for (int i = 0; i < count; i++)
            {
                AddEnd(ends, Key(m_from[i]), i);
                AddEnd(ends, Key(m_to[i]), i);
            }
            var used = new bool[count];
            var lines = new List<Line>();
            var back = new List<Vector2>();
            for (int i = 0; i < count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var line = new Line();
                line.m_points.Add(m_from[i]);
                line.m_points.Add(m_to[i]);
                Extend(ends, used, line.m_points, m_to[i]);
                back.Clear();
                back.Add(m_from[i]);
                Extend(ends, used, back, m_from[i]);
                if (back.Count > 1)
                {
                    back.RemoveAt(0);
                    back.Reverse();
                    line.m_points.InsertRange(0, back);
                }
                line.m_closed = line.m_points.Count > 3 && Key(line.m_points[0]) == Key(line.m_points[line.m_points.Count - 1]);
                if (line.m_closed) line.m_points.RemoveAt(line.m_points.Count - 1);
                lines.Add(line);
            }
            return lines;
        }

        private static void AddEnd(Dictionary<long, List<int>> ends, long key, int segment)
        {
            if (!ends.TryGetValue(key, out List<int> list)) ends[key] = list = new List<int>(2);
            list.Add(segment);
        }

        // Follows unused segments from the end at, adding each far end to points.
        private void Extend(Dictionary<long, List<int>> ends, bool[] used, List<Vector2> points, Vector2 at)
        {
            while (true)
            {
                long key = Key(at);
                int next = -1;
                foreach (int s in ends[key])
                {
                    if (used[s]) continue;
                    next = s;
                    break;
                }
                if (next < 0) return;
                used[next] = true;
                at = Key(m_from[next]) == key ? m_to[next] : m_from[next];
                points.Add(at);
            }
        }

        // Two rounds of corner cutting (Chaikin), so a contour traced on a pixel grid runs as a pen
        // line rather than a stair; an open line keeps its ends.
        public static void Smooth(Line line)
        {
            List<Vector2> p = line.m_points;
            for (int round = 0; round < 2; round++)
            {
                int n = p.Count;
                if (n < 3) return;
                var smooth = new List<Vector2>(n * 2);
                if (!line.m_closed) smooth.Add(p[0]);
                int segments = line.m_closed ? n : n - 1;
                for (int i = 0; i < segments; i++)
                {
                    Vector2 a = p[i], b = p[(i + 1) % n];
                    if (line.m_closed || i > 0) smooth.Add(Vector2.Lerp(a, b, 0.25f));
                    if (line.m_closed || i < segments - 1) smooth.Add(Vector2.Lerp(a, b, 0.75f));
                }
                if (!line.m_closed) smooth.Add(p[n - 1]);
                p.Clear();
                p.AddRange(smooth);
            }
        }
    }
}
