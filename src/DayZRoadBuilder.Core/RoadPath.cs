using System;
using System.Collections.Generic;

namespace DayZRoadBuilder.Core
{
    public struct Projection
    {
        /// <summary>Squared distance to the line.</summary>
        public double Dist2;
        /// <summary>Arc length (chainage) of the closest point.</summary>
        public double S;
        /// <summary>Segment index.</summary>
        public int Segment;
        /// <summary>+1 = right of the line, -1 = left.</summary>
        public int Side;
    }

    /// <summary>The target line (polyline) with chainage and fast local projection.</summary>
    public sealed class RoadPath
    {
        public Vec2[] Points { get; private set; }
        public double[] Cum { get; private set; }
        public double Length { get; private set; }
        public string Name { get; set; }

        public RoadPath(IList<Vec2> pts)
        {
            var clean = new List<Vec2>();
            foreach (Vec2 p in pts)
            {
                if (clean.Count == 0 || Vec2.Distance(clean[clean.Count - 1], p) > 1e-4)
                    clean.Add(p);
            }
            if (clean.Count < 2)
                throw new ArgumentException("The line needs at least two distinct points.");

            Points = clean.ToArray();
            Cum = new double[Points.Length];
            for (int i = 1; i < Points.Length; i++)
                Cum[i] = Cum[i - 1] + Vec2.Distance(Points[i - 1], Points[i]);
            Length = Cum[Cum.Length - 1];
        }

        public Vec2 Start { get { return Points[0]; } }
        public Vec2 End { get { return Points[Points.Length - 1]; } }

        public RoadPath Reversed()
        {
            var list = new List<Vec2>(Points);
            list.Reverse();
            return new RoadPath(list) { Name = Name };
        }

        /// <summary>Index of the segment containing chainage s.</summary>
        public int SegmentAt(double s)
        {
            if (s <= 0) return 0;
            if (s >= Length) return Points.Length - 2;
            int lo = 0, hi = Cum.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (Cum[mid] <= s) lo = mid; else hi = mid;
            }
            return Math.Min(lo, Points.Length - 2);
        }

        public double SegmentHeading(int i)
        {
            return Geo.Bearing(Points[i + 1] - Points[i]);
        }

        public Vec2 PointAt(double s)
        {
            int i = SegmentAt(s);
            double segLen = Cum[i + 1] - Cum[i];
            double t = segLen > 0 ? (s - Cum[i]) / segLen : 0;
            t = Math.Max(0, Math.Min(1, t));
            return Vec2.Lerp(Points[i], Points[i + 1], t);
        }

        /// <summary>Bearing of the line averaged over the first <paramref name="dist"/> metres.</summary>
        public double StartHeading(double dist)
        {
            Vec2 p = PointAt(Math.Min(dist, Length));
            return Geo.Bearing(p - Points[0]);
        }

        /// <summary>
        /// Closest point on the line, searched only within the chainage window [s0, s1].
        /// The local window prevents jumping to other sections of tight loops.
        /// </summary>
        public Projection Project(Vec2 q, double s0, double s1)
        {
            int i0 = SegmentAt(s0);
            int i1 = SegmentAt(s1);
            var best = new Projection { Dist2 = double.MaxValue, S = 0, Segment = 0, Side = 1 };
            for (int i = i0; i <= i1; i++)
            {
                Vec2 a = Points[i];
                Vec2 b = Points[i + 1];
                double dx = b.X - a.X, dz = b.Z - a.Z;
                double l2 = dx * dx + dz * dz;
                double t = l2 > 0 ? ((q.X - a.X) * dx + (q.Z - a.Z) * dz) / l2 : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double px = a.X + dx * t, pz = a.Z + dz * t;
                double ex = q.X - px, ez = q.Z - pz;
                double d2 = ex * ex + ez * ez;
                if (d2 < best.Dist2)
                {
                    double cross = dx * (q.Z - a.Z) - dz * (q.X - a.X);
                    best.Dist2 = d2;
                    best.S = Cum[i] + t * Math.Sqrt(l2);
                    best.Segment = i;
                    best.Side = cross > 0 ? -1 : 1;
                }
            }
            return best;
        }
    }
}
