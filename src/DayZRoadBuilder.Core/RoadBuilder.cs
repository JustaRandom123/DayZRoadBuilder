using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DayZRoadBuilder.Core
{
    /// <summary>
    /// Chains road parts together without gaps so that the chain follows the target line as closely as possible.
    ///
    /// Method: beam search over the chainage of the line.
    /// A state = end of the chain so far (position + heading). From every state each allowed part
    /// (curves in both directions) is appended and scored:
    ///   cost = ∫ distance² to the line along the part centreline
    ///        + heading error² at the end of the part
    ///        + penalty per part + penalty per degree of curve.
    /// States are sorted into 0.5 m chainage bins; only the best N survive per bin.
    /// Because every part starts exactly at the exit of the previous one (memory points LB/PB -> LE/PE), there are no gaps.
    ///
    /// Crossroads: if <see cref="RoadConstraints.Junctions"/> is set, the crossroad part is inserted into the chain
    /// like a 12.5 m straight. Normal parts may not run past a junction until its crossroad has been placed, and a road
    /// only counts as finished when all its crossroads are placed.
    /// </summary>
    public static class RoadBuilder
    {
        private sealed class Node
        {
            public Vec2 P;
            public double H;
            public double S;
            public double Cost;
            public double Lat;
            public double HErr;
            public int NextJ;
            public Node Parent;
            public PartVariant Variant;
            public JunctionWaypoint Junction;
        }

        private sealed class JunctionOptions
        {
            public JunctionWaypoint Waypoint;
            public List<PartVariant> Variants = new List<PartVariant>();
            public double Half;
        }

        /// <summary>Context of one search run.</summary>
        private sealed class Search
        {
            public RoadPath Path;
            public BuildSettings S;
            public RoadConstraints C;
            public List<JunctionOptions> Junctions;
            public double BinSize;
            public double EndTol;
            public double EndWeight;
            public Node Best;
            public double BestCost = double.MaxValue;
        }

        public static BuildResult Build(RoadPath path, IList<RoadPart> parts, RoadPart endCap, BuildSettings s,
                                        IProgress<double> progress, CancellationToken ct)
        {
            return Build(path, parts, endCap, s, null, progress, ct);
        }

        public static BuildResult Build(RoadPath path, IList<RoadPart> parts, RoadPart endCap, BuildSettings s,
                                        RoadConstraints constraints, IProgress<double> progress, CancellationToken ct)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (s == null) s = new BuildSettings();
            if (constraints == null) constraints = new RoadConstraints();
            var sw = Stopwatch.StartNew();
            var result = new BuildResult { Path = path };

            var usable = parts.Where(p => p.Kind == PartKind.Straight || p.Kind == PartKind.Curve || p.Kind == PartKind.Crosswalk).ToList();
            if (usable.Count == 0)
                throw new InvalidOperationException("No road parts selected.");

            var variants = new List<PartVariant>();
            foreach (RoadPart p in usable)
            {
                variants.Add(PartVariant.Create(p, false, s.UseBoundingCenter, s.SampleStep));
                if (!p.IsStraight)
                    variants.Add(PartVariant.Create(p, true, s.UseBoundingCenter, s.SampleStep));
            }
            // long parts first (only matters for equal costs)
            variants = variants.OrderByDescending(v => v.Length).ToList();

            PartVariant startCap = null, endCapVar = null;
            if (endCap != null)
            {
                // assumption: the end edge (LE/PE) of the end piece is the "open" end of the road
                if (s.EndCapAtStart && constraints.AllowStartCap) startCap = PartVariant.Create(endCap, !s.FlipEndCaps, s.UseBoundingCenter, s.SampleStep);
                if (s.EndCapAtEnd && constraints.AllowEndCap) endCapVar = PartVariant.Create(endCap, s.FlipEndCaps, s.UseBoundingCenter, s.SampleStep);
            }

            var search = new Search
            {
                Path = path,
                S = s,
                C = constraints,
                BinSize = Math.Max(0.05, s.BinSize),
                EndTol = Math.Max(0.1, s.EndTolerance),
                EndWeight = constraints.EndIsFixed ? s.FixedEndWeight : s.EndWeight,
                Junctions = new List<JunctionOptions>()
            };
            foreach (JunctionWaypoint j in constraints.Junctions.OrderBy(j => j.S))
            {
                var jo = new JunctionOptions { Waypoint = j, Half = j.Part.Length / 2.0 };
                if (j.AllowForward) jo.Variants.Add(PartVariant.Create(j.Part, false, s.UseBoundingCenter, s.SampleStep));
                if (j.AllowReversed) jo.Variants.Add(PartVariant.Create(j.Part, true, s.UseBoundingCenter, s.SampleStep));
                if (jo.Variants.Count > 0) search.Junctions.Add(jo);
            }

            Vec2 end = path.End;
            double capLen = endCapVar != null ? endCapVar.Length : 0.0;

            // start states
            var bins = new SortedDictionary<int, List<Node>>();
            var roots = new List<Node>();
            if (constraints.StartHeading.HasValue)
            {
                roots.Add(new Node { P = path.Start, H = Geo.WrapDeg(constraints.StartHeading.Value), S = 0, Cost = 0 });
            }
            else
            {
                double h0 = path.StartHeading(Math.Min(3.0, path.Length));
                double range = Math.Max(0, s.StartHeadingRange);
                double step = Math.Max(0.05, s.StartHeadingStep);
                for (double off = -range; off <= range + 1e-9; off += step)
                {
                    roots.Add(new Node { P = path.Start, H = Geo.WrapDeg(h0 + off), S = 0, Cost = s.HeadingWeight * off * off });
                    if (range <= 0) break;
                }
            }
            foreach (Node r in roots)
            {
                Node start = r;
                if (startCap != null)
                {
                    start = Extend(search, r, startCap, null);
                    if (start == null) continue;
                }
                // a road can also start directly with a crossroad
                AddToBin(bins, start, search.BinSize);
            }

            Node furthest = null;
            int processed = 0;
            var children = new List<Node>();

            while (bins.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int key = bins.Keys.First();
                List<Node> list = bins[key];
                bins.Remove(key);

                list.Sort((a, b) => a.Cost.CompareTo(b.Cost));
                var seen = new HashSet<long>();
                int kept = 0;
                foreach (Node st in list)
                {
                    if (kept >= s.BeamWidth) break;
                    long k1 = (long)Math.Round(st.Lat / 0.2);
                    long k2 = (long)Math.Round(st.HErr / 0.5);
                    long hk = (k1 << 32) ^ (k2 & 0xFFFFFFFFL) ^ ((long)st.NextJ << 56);
                    if (!seen.Add(hk)) continue;
                    kept++;

                    children.Clear();
                    foreach (PartVariant v in variants)
                    {
                        Node c = Extend(search, st, v, null);
                        if (c != null) children.Add(c);
                    }
                    if (st.NextJ < search.Junctions.Count)
                    {
                        JunctionOptions jo = search.Junctions[st.NextJ];
                        if (st.S >= jo.Waypoint.S - jo.Half - s.CrossroadWindow)
                        {
                            foreach (PartVariant v in jo.Variants)
                            {
                                Node c = Extend(search, st, v, jo);
                                if (c != null) children.Add(c);
                            }
                        }
                    }

                    foreach (Node c in children)
                    {
                        if (furthest == null || c.NextJ > furthest.NextJ ||
                            (c.NextJ == furthest.NextJ && (c.S > furthest.S + 1e-6 || (Math.Abs(c.S - furthest.S) < 1e-6 && c.Cost < furthest.Cost))))
                            furthest = c;

                        if (endCapVar != null)
                        {
                            if (Vec2.Distance(c.P, end) <= capLen + search.EndTol + 1.0)
                            {
                                Node cc = Extend(search, c, endCapVar, null);
                                if (cc != null) TryFinish(search, cc);
                            }
                        }
                        else
                        {
                            TryFinish(search, c);
                        }

                        if (c.S < path.Length - 1e-6)
                            AddToBin(bins, c, search.BinSize);
                    }
                }

                processed++;
                if (progress != null && (processed & 31) == 0)
                    progress.Report(Math.Min(1.0, key * search.BinSize / path.Length));
            }

            Node best = search.Best;
            if (best == null)
            {
                best = furthest;
                result.ReachedEnd = false;
                result.Warnings.Add("The end of the line was not reached within the tolerance – the result stops early. Try enabling more/shorter parts or increasing the end tolerance.");
            }
            else
            {
                result.ReachedEnd = true;
            }

            // rebuild the chain
            var chain = new List<Node>();
            for (Node n = best; n != null && n.Parent != null; n = n.Parent)
                chain.Add(n);
            chain.Reverse();

            var placedJunctions = new HashSet<int>();
            foreach (Node c in chain)
            {
                Node p = c.Parent;
                PartVariant v = c.Variant;
                var pp = new PlacedPart
                {
                    Part = v.Part,
                    Reversed = v.Reversed,
                    Position = p.P + Geo.Rotate(v.Center, p.H),
                    Yaw = Geo.Norm360(p.H + v.YawOffset),
                    ModelReference = v.ModelReference,
                    Entry = p.P,
                    Exit = c.P,
                    EntryHeading = Geo.Norm360(p.H),
                    ExitHeading = Geo.Norm360(c.H)
                };
                if (c.Junction != null)
                {
                    pp.JunctionId = c.Junction.Id;
                    placedJunctions.Add(c.Junction.Id);
                }
                result.Parts.Add(pp);
            }
            foreach (JunctionOptions jo in search.Junctions)
            {
                if (!placedJunctions.Contains(jo.Waypoint.Id))
                    result.Warnings.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Crossroad {0} at {1} could not be placed (junctions too close together or line too short?).",
                        jo.Waypoint.Part.Name, jo.Waypoint.Point));
            }

            ComputeStats(result, chain);
            result.Duration = sw.Elapsed;
            if (progress != null) progress.Report(1.0);
            return result;
        }

        private static void AddToBin(SortedDictionary<int, List<Node>> bins, Node n, double binSize)
        {
            int kb = (int)Math.Floor(n.S / binSize);
            List<Node> l;
            if (!bins.TryGetValue(kb, out l))
            {
                l = new List<Node>();
                bins.Add(kb, l);
            }
            l.Add(n);
        }

        private static void TryFinish(Search x, Node n)
        {
            if (n.NextJ < x.Junctions.Count) return; // not all crossroads placed yet

            double d = Vec2.Distance(n.P, x.Path.End);
            // chainage condition so that closed lines (start = end) are not "finished" right away
            bool nearEnd = d <= x.EndTol && n.S >= x.Path.Length - x.EndTol - 2.0;
            if (nearEnd || n.S >= x.Path.Length - 1e-6)
            {
                double fc = n.Cost + x.EndWeight * d * d;
                if (x.C.EndHeading.HasValue)
                {
                    double he = Geo.WrapDeg(n.H - x.C.EndHeading.Value);
                    fc += x.S.EndHeadingWeight * he * he;
                }
                if (fc < x.BestCost)
                {
                    x.BestCost = fc;
                    x.Best = n;
                }
            }
        }

        private static Node Extend(Search x, Node st, PartVariant v, JunctionOptions placing)
        {
            RoadPath path = x.Path;
            BuildSettings s = x.S;
            double cost = st.Cost + s.PiecePenalty + s.TurnPenalty * v.AbsTurn;
            double w0 = st.S - 2.0;
            double w1 = st.S + v.Length * 1.5 + 10.0;

            Vec2[] samples = v.Samples;
            for (int i = 0; i < samples.Length; i++)
            {
                Vec2 w = st.P + Geo.Rotate(samples[i], st.H);
                Projection pr = path.Project(w, w0, w1);
                cost += pr.Dist2 * v.SampleStep;
            }

            Vec2 np = st.P + Geo.Rotate(v.Exit, st.H);
            Projection pe = path.Project(np, w0, w1);
            if (pe.S < st.S + x.BinSize && pe.S < path.Length - 1e-6)
                return null; // no progress along the line

            int nextJ = st.NextJ;
            if (placing != null)
            {
                // the crossroad should sit on the junction point
                Vec2 centre = st.P + Geo.Rotate(v.Exit * 0.5, st.H);
                cost += s.CrossroadCenterWeight * (centre - placing.Waypoint.Point).LengthSquared;
                nextJ++;
            }
            else if (nextJ < x.Junctions.Count)
            {
                // normal parts must not run past the next junction before its crossroad is placed
                JunctionOptions jo = x.Junctions[nextJ];
                if (pe.S > jo.Waypoint.S - jo.Half + s.CrossroadWindow)
                    return null;
            }

            double nh = Geo.WrapDeg(st.H + v.HeadingDelta);
            double herr = Geo.WrapDeg(nh - path.SegmentHeading(pe.Segment));
            cost += s.HeadingWeight * herr * herr;

            return new Node
            {
                P = np,
                H = nh,
                S = pe.S,
                Cost = cost,
                Lat = pe.Side * Math.Sqrt(pe.Dist2),
                HErr = herr,
                NextJ = nextJ,
                Parent = st,
                Variant = v,
                Junction = placing != null ? placing.Waypoint : null
            };
        }

        private static void ComputeStats(BuildResult r, List<Node> chain)
        {
            RoadPath path = r.Path;
            double maxDev = 0, sum2 = 0, len = 0;
            int count = 0;
            foreach (Node c in chain)
            {
                Node p = c.Parent;
                PartVariant v = c.Variant;
                len += v.Length;
                foreach (Vec2 sp in v.Samples)
                {
                    Vec2 w = p.P + Geo.Rotate(sp, p.H);
                    Projection pr = path.Project(w, p.S - 5.0, p.S + v.Length * 1.5 + 10.0);
                    double d = Math.Sqrt(pr.Dist2);
                    if (d > maxDev) maxDev = d;
                    sum2 += pr.Dist2;
                    count++;
                }
            }
            r.MaxDeviation = maxDev;
            r.RmsDeviation = count > 0 ? Math.Sqrt(sum2 / count) : 0;
            r.RoadLength = len;
            r.EndGap = r.Parts.Count > 0 ? Vec2.Distance(r.Parts[r.Parts.Count - 1].Exit, path.End) : path.Length;

            // sanity check: gaps between the edge centres of neighbouring parts in world coordinates
            double gap = 0;
            for (int i = 0; i + 1 < r.Parts.Count; i++)
            {
                PlacedPart a = r.Parts[i];
                PlacedPart b = r.Parts[i + 1];
                Vec2 aExit = a.Reversed ? a.ToWorld(a.Part.StartCenter) : a.ToWorld(a.Part.EndCenter);
                Vec2 bEntry = b.Reversed ? b.ToWorld(b.Part.EndCenter) : b.ToWorld(b.Part.StartCenter);
                gap = Math.Max(gap, Vec2.Distance(aExit, bEntry));
            }
            r.MaxJointGap = gap;
        }
    }
}
