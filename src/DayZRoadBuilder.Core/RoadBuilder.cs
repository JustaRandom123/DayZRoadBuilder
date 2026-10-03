using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DayZRoadBuilder.Core
{
    /// <summary>
    /// Setzt Straßenteile lückenlos aneinander, sodass die Kette möglichst genau der Soll-Linie folgt.
    ///
    /// Verfahren: Strahlsuche (Beam Search) über die Stationierung der Linie.
    /// Ein Zustand = Ende der bisherigen Kette (Position + Fahrtrichtung). Von jedem Zustand aus wird jedes erlaubte Teil
    /// (Kurven in beide Richtungen) angehängt und bewertet:
    ///   Kosten = ∫ Abstand² zur Linie entlang der Teil-Mittellinie
    ///          + Richtungsfehler² am Teilende
    ///          + Strafe pro Teil + Strafe pro Grad Kurve.
    /// Die Zustände werden nach erreichter Stationierung in 0,5-m-Fächer sortiert; pro Fach überleben nur die besten N.
    /// Da jedes Teil exakt an das vorherige anschließt (Memorypunkte LB/PB -> LE/PE), entstehen keine Lücken.
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
            public Node Parent;
            public PartVariant Variant;
        }

        public static BuildResult Build(RoadPath path, IList<RoadPart> parts, RoadPart endCap, BuildSettings s,
                                        IProgress<double> progress, CancellationToken ct)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (s == null) s = new BuildSettings();
            var sw = Stopwatch.StartNew();
            var result = new BuildResult { Path = path };

            var usable = parts.Where(p => p.Kind == PartKind.Straight || p.Kind == PartKind.Curve || p.Kind == PartKind.Crosswalk).ToList();
            if (usable.Count == 0)
                throw new InvalidOperationException("Keine Straßenteile ausgewählt.");

            var variants = new List<PartVariant>();
            foreach (RoadPart p in usable)
            {
                variants.Add(PartVariant.Create(p, false, s.UseBoundingCenter, s.SampleStep));
                if (!p.IsStraight)
                    variants.Add(PartVariant.Create(p, true, s.UseBoundingCenter, s.SampleStep));
            }
            // Lange Teile zuerst (nur relevant bei Kostengleichstand)
            variants = variants.OrderByDescending(v => v.Length).ToList();

            PartVariant startCap = null, endCapVar = null;
            if (endCap != null)
            {
                // Annahme: die Endkante (LE/PE) des Endstücks ist das "offene" Straßenende.
                if (s.EndCapAtStart) startCap = PartVariant.Create(endCap, !s.FlipEndCaps, s.UseBoundingCenter, s.SampleStep);
                if (s.EndCapAtEnd) endCapVar = PartVariant.Create(endCap, s.FlipEndCaps, s.UseBoundingCenter, s.SampleStep);
            }

            double binSize = Math.Max(0.05, s.BinSize);
            double endTol = Math.Max(0.1, s.EndTolerance);
            Vec2 end = path.End;
            double capLen = endCapVar != null ? endCapVar.Length : 0.0;

            // Startzustände
            var bins = new SortedDictionary<int, List<Node>>();
            double h0 = path.StartHeading(Math.Min(3.0, path.Length));
            var roots = new List<Node>();
            double range = Math.Max(0, s.StartHeadingRange);
            double step = Math.Max(0.05, s.StartHeadingStep);
            for (double off = -range; off <= range + 1e-9; off += step)
            {
                roots.Add(new Node { P = path.Start, H = Geo.WrapDeg(h0 + off), S = 0, Cost = s.HeadingWeight * off * off });
                if (range <= 0) break;
            }
            foreach (Node r in roots)
            {
                Node start = r;
                if (startCap != null)
                {
                    start = Extend(path, r, startCap, s, binSize);
                    if (start == null) continue;
                }
                AddToBin(bins, start, binSize);
            }

            Node best = null;
            double bestCost = double.MaxValue;
            Node furthest = null;
            int processed = 0;

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
                    long hk = (k1 << 32) ^ (k2 & 0xFFFFFFFFL);
                    if (!seen.Add(hk)) continue;
                    kept++;

                    foreach (PartVariant v in variants)
                    {
                        Node c = Extend(path, st, v, s, binSize);
                        if (c == null) continue;

                        if (furthest == null || c.S > furthest.S + 1e-6 || (Math.Abs(c.S - furthest.S) < 1e-6 && c.Cost < furthest.Cost))
                            furthest = c;

                        if (endCapVar != null)
                        {
                            if (Vec2.Distance(c.P, end) <= capLen + endTol + 1.0)
                            {
                                Node cc = Extend(path, c, endCapVar, s, binSize);
                                if (cc != null) TryFinish(cc, path, s, endTol, ref best, ref bestCost);
                            }
                        }
                        else
                        {
                            TryFinish(c, path, s, endTol, ref best, ref bestCost);
                        }

                        if (c.S < path.Length - 1e-6)
                            AddToBin(bins, c, binSize);
                    }
                }

                processed++;
                if (progress != null && (processed & 31) == 0)
                    progress.Report(Math.Min(1.0, key * binSize / path.Length));
            }

            if (best == null)
            {
                best = furthest;
                result.ReachedEnd = false;
                result.Warnings.Add("Das Linienende wurde nicht innerhalb der Toleranz erreicht – Ergebnis endet vorher. Ggf. mehr/kleinere Teile erlauben oder Toleranz erhöhen.");
            }
            else
            {
                result.ReachedEnd = true;
            }

            // Kette rekonstruieren
            var chain = new List<Node>();
            for (Node n = best; n != null && n.Parent != null; n = n.Parent)
                chain.Add(n);
            chain.Reverse();

            foreach (Node c in chain)
            {
                Node p = c.Parent;
                PartVariant v = c.Variant;
                result.Parts.Add(new PlacedPart
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
                });
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

        private static void TryFinish(Node n, RoadPath path, BuildSettings s, double endTol, ref Node best, ref double bestCost)
        {
            double d = Vec2.Distance(n.P, path.End);
            // Stationierungsbedingung, damit geschlossene Linien (Start = Ende) nicht sofort "fertig" sind
            bool nearEnd = d <= endTol && n.S >= path.Length - endTol - 2.0;
            if (nearEnd || n.S >= path.Length - 1e-6)
            {
                double fc = n.Cost + s.EndWeight * d * d;
                if (fc < bestCost)
                {
                    bestCost = fc;
                    best = n;
                }
            }
        }

        private static Node Extend(RoadPath path, Node st, PartVariant v, BuildSettings s, double binSize)
        {
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
            if (pe.S < st.S + binSize && pe.S < path.Length - 1e-6)
                return null; // kein Fortschritt entlang der Linie

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
                Parent = st,
                Variant = v
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

            // Kontrolle: Fugen zwischen den Memorypunkten benachbarter Teile in Weltkoordinaten
            double gap = 0;
            for (int i = 0; i + 1 < r.Parts.Count; i++)
            {
                PlacedPart a = r.Parts[i];
                PlacedPart b = r.Parts[i + 1];
                Vec2 aL = a.Reversed ? a.ToWorld(a.Part.PB) : a.ToWorld(a.Part.LE);
                Vec2 aR = a.Reversed ? a.ToWorld(a.Part.LB) : a.ToWorld(a.Part.PE);
                Vec2 bL = b.Reversed ? b.ToWorld(b.Part.PE) : b.ToWorld(b.Part.LB);
                Vec2 bR = b.Reversed ? b.ToWorld(b.Part.LE) : b.ToWorld(b.Part.PB);
                gap = Math.Max(gap, Math.Max(Vec2.Distance(aL, bL), Vec2.Distance(aR, bR)));
            }
            r.MaxJointGap = gap;
        }
    }
}
