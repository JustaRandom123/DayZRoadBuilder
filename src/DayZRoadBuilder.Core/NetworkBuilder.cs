using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DayZRoadBuilder.Core
{
    /// <summary>Options for building a whole road network (several lines, junctions).</summary>
    public sealed class NetworkSettings
    {
        /// <summary>Place crossroad parts (kr_t_* / kr_x_*) at junctions, if the road type has them.</summary>
        public bool UseCrossroads { get; set; } = true;
        /// <summary>Line ends closer than this (m) are joined into one node.</summary>
        public double SnapDistance { get; set; } = 1.5;
        /// <summary>T-junction part for the selected road type (name), null/empty = automatic.</summary>
        public string TJunctionPart { get; set; }
        /// <summary>X-crossroad part for the selected road type (name), null/empty = automatic.</summary>
        public string XJunctionPart { get; set; }
        /// <summary>Build side roads with the side road type of the crossroad (e.g. asf2 for kr_t_asf1_asf2).</summary>
        public bool SideRoadsUseCrossroadType { get; set; } = true;
        /// <summary>Build roads without crossroad connections in the opposite direction.</summary>
        public bool ReverseFreeRoads { get; set; }
    }

    /// <summary>A node where three or more lines meet.</summary>
    public sealed class JunctionInfo
    {
        public int Id;
        public Vec2 Point;
        /// <summary>Number of lines meeting here.</summary>
        public int Degree;
        /// <summary>'T', 'X' or '?' (unsupported).</summary>
        public char Type;
        /// <summary>Chosen crossroad part (null = none).</summary>
        public RoadPart Part;
        /// <summary>The placed crossroad (null if not placed).</summary>
        public PlacedPart Placed;
        public string Note;
    }

    public sealed class NetworkResult
    {
        public List<BuildResult> Roads = new List<BuildResult>();
        public List<JunctionInfo> Junctions = new List<JunctionInfo>();
        public List<string> Warnings = new List<string>();
        public TimeSpan Duration;

        public IEnumerable<PlacedPart> AllParts
        {
            get { return Roads.SelectMany(r => r.Parts); }
        }
    }

    /// <summary>
    /// Builds a whole road network:
    /// 1. lines are split where they touch or cross each other and their ends are joined into nodes,
    /// 2. nodes with 2 lines are just a continuation, nodes with 3 lines are T-junctions, nodes with 4 lines X-crossroads,
    /// 3. at every junction the two straightest arms form the through road, the others are side roads,
    /// 4. lines are joined into continuous roads (through nodes with 2 lines and along the through roads),
    /// 5. each road is built with <see cref="RoadBuilder"/>; the crossroad is inserted into the through road and
    ///    the side roads start exactly at the side arm of the placed crossroad.
    /// </summary>
    public static class NetworkBuilder
    {
        // ------------------------------------------------------------------ graph

        private sealed class Edge
        {
            public int Id;
            public List<Vec2> Pts;
            public int A, B;
            public double Length;
            public int Source; // index of the input line
            public double SourceLength; // length of the whole input line
            public EdgeEnd StartEnd, EndEnd;
            public bool Visited;
        }

        private sealed class EdgeEnd
        {
            public Edge E; // may be re-assigned when an edge is split
            public bool AtStart;
            public int NodeId { get { return AtStart ? E.A : E.B; } }
            public EdgeEnd Other { get { return AtStart ? E.EndEnd : E.StartEnd; } }
        }

        private sealed class NetNode
        {
            public int Id;
            public Vec2 P;
            public int Count;
            public List<EdgeEnd> Ends = new List<EdgeEnd>();
            public Junc J;
        }

        private struct Arm
        {
            public Vec2 P;
            public double H; // outward driving direction
            public bool Left; // left arm of the crossroad model (LD/LH)
        }

        private sealed class Junc
        {
            public JunctionInfo Info;
            public NetNode Node;
            public EdgeEnd MainA, MainB;
            public List<EdgeEnd> Branches = new List<EdgeEnd>();
            public Dictionary<EdgeEnd, double> Dir = new Dictionary<EdgeEnd, double>();
            public RoadPart Part;
            public bool Resolved;
            public Dictionary<EdgeEnd, Arm> Arms = new Dictionary<EdgeEnd, Arm>();
            public Chain MainChain;
            public bool Active { get { return Part != null; } }
        }

        private sealed class Step
        {
            public Edge E;
            public bool Rev;
        }

        private sealed class Chain
        {
            public int Id;
            public List<Step> Steps = new List<Step>();
            public EdgeEnd FirstEnd;   // edge end at the start node of the chain
            public EdgeEnd LastEnd;    // edge end at the end node of the chain
            public List<Junc> Mains = new List<Junc>();
            public string Family;
            public double Length;
        }

        // ------------------------------------------------------------------ public entry

        public static NetworkResult Build(IList<List<Vec2>> lines, RoadPartLibrary lib, string family,
                                          IList<RoadPart> enabledParts, RoadPart endCap,
                                          BuildSettings bs, NetworkSettings ns,
                                          IProgress<double> progress, CancellationToken ct)
        {
            if (ns == null) ns = new NetworkSettings();
            if (bs == null) bs = new BuildSettings();
            var sw = Stopwatch.StartNew();
            var res = new NetworkResult();
            double snap = Math.Max(0.01, ns.SnapDistance);

            // 1. split lines and build the graph
            List<KeyValuePair<int, List<Vec2>>> pieces = SplitLines(lines, snap);
            List<NetNode> nodes;
            List<Edge> edges;
            BuildGraph(pieces, snap, out nodes, out edges);

            // 2. junctions
            int jid = 0;
            foreach (NetNode n in nodes)
            {
                if (n.Ends.Count < 3) continue;
                var info = new JunctionInfo { Id = ++jid, Point = n.P, Degree = n.Ends.Count };
                res.Junctions.Add(info);
                if (n.Ends.Count > 4)
                {
                    info.Type = '?';
                    info.Note = n.Ends.Count + " lines meet here – not supported, the roads just end at this point.";
                    res.Warnings.Add(string.Format(CultureInfo.InvariantCulture, "Junction {0} at {1}: {2}", info.Id, info.Point, info.Note));
                    continue;
                }
                var j = new Junc { Info = info, Node = n };
                info.Type = n.Ends.Count == 3 ? 'T' : 'X';
                foreach (EdgeEnd ee in n.Ends) j.Dir[ee] = ArmDirection(ee);
                PairArms(j);
                n.J = j;
            }

            // 3. chains (continuous roads)
            List<Chain> chains = BuildChains(edges, nodes);

            // 4. road type per chain and crossroad part per junction
            foreach (Chain c in chains) c.Family = family;
            foreach (NetNode n in nodes)
            {
                Junc j = n.J;
                if (j == null) continue;
                string fam = j.MainChain != null ? j.MainChain.Family : family;
                if (!ns.UseCrossroads)
                {
                    j.Info.Note = "Crossroads are switched off.";
                }
                else
                {
                    j.Part = ChooseCrossroad(lib, fam, j.Info.Type, family, ns);
                    if (j.Part == null)
                    {
                        j.Info.Note = string.Format("No {0} crossroad part for road type '{1}' – roads are built without crossroad.",
                            j.Info.Type == 'X' ? "X" : "T", fam);
                        res.Warnings.Add(string.Format(CultureInfo.InvariantCulture, "Junction {0} at {1}: {2}", j.Info.Id, j.Info.Point, j.Info.Note));
                    }
                }
                j.Info.Part = j.Part;
                j.Resolved = !j.Active;
            }
            if (ns.SideRoadsUseCrossroadType)
            {
                foreach (Chain c in chains)
                {
                    if (c.Mains.Any(m => m.Active)) continue;
                    foreach (EdgeEnd ee in new[] { c.FirstEnd, c.LastEnd })
                    {
                        Junc j = BranchJunction(nodes, ee);
                        if (j == null || !j.Active) continue;
                        string bf = j.Part.CrossBranchFamily;
                        if (!string.IsNullOrEmpty(bf) && lib.GetFamilies().Any(f => string.Equals(f, bf, StringComparison.OrdinalIgnoreCase)))
                        {
                            c.Family = lib.GetFamilies().First(f => string.Equals(f, bf, StringComparison.OrdinalIgnoreCase));
                            break;
                        }
                    }
                }
            }

            // 5. build the roads, through roads with crossroads first
            double totalLen = Math.Max(1.0, chains.Sum(c => c.Length));
            double doneLen = 0;
            var pending = new List<Chain>(chains);
            int roadNo = 0;
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                Chain c = pending.FirstOrDefault(x => UnresolvedArms(nodes, x) == 0);
                bool forced = false;
                if (c == null)
                {
                    c = pending.OrderBy(x => UnresolvedArms(nodes, x)).ThenByDescending(x => x.Mains.Count).First();
                    forced = true;
                }
                pending.Remove(c);
                roadNo++;

                double before = doneLen;
                double share = c.Length / totalLen;
                IProgress<double> sub = progress == null ? null : new ScaledProgress(progress, before / totalLen, share);

                BuildResult r = BuildChain(c, roadNo, nodes, lib, family, enabledParts, endCap, bs, ns, forced, sub, ct);
                foreach (PlacedPart p in r.Parts) p.RoadIndex = roadNo - 1;
                res.Roads.Add(r);
                doneLen += c.Length;
            }

            foreach (NetNode n in nodes)
            {
                if (n.J != null && n.J.Active && n.J.Info.Placed == null && string.IsNullOrEmpty(n.J.Info.Note))
                    n.J.Info.Note = "Crossroad could not be placed.";
            }

            res.Duration = sw.Elapsed;
            if (progress != null) progress.Report(1.0);
            return res;
        }

        // ------------------------------------------------------------------ building one chain

        private static BuildResult BuildChain(Chain c, int roadNo, List<NetNode> nodes, RoadPartLibrary lib, string mainFamily,
                                              IList<RoadPart> enabledParts, RoadPart endCap, BuildSettings bs, NetworkSettings ns,
                                              bool forced, IProgress<double> progress, CancellationToken ct)
        {
            Arm startArm, endArm;
            bool hasStartArm = TryGetPlacedArm(nodes, c.FirstEnd, out startArm);
            bool hasEndArm = TryGetPlacedArm(nodes, c.LastEnd, out endArm);

            // orientation: start at a crossroad arm whenever possible (start connections are exact)
            bool reverse = false;
            if (!hasStartArm && hasEndArm) reverse = true;
            else if (!hasStartArm && !hasEndArm && c.Mains.Count == 0 && ns.ReverseFreeRoads) reverse = true;
            if (reverse)
            {
                ReverseChain(c);
                Arm t = startArm; startArm = endArm; endArm = t;
                bool tb = hasStartArm; hasStartArm = hasEndArm; hasEndArm = tb;
            }

            // points of the chain and the vertex index of every junction passed
            var pts = new List<Vec2>();
            var junctionIndex = new List<KeyValuePair<Junc, int>>();
            for (int k = 0; k < c.Steps.Count; k++)
            {
                Step st = c.Steps[k];
                List<Vec2> ep = st.Rev ? Enumerable.Reverse(st.E.Pts).ToList() : st.E.Pts;
                if (k > 0)
                {
                    int nodeId = st.Rev ? st.E.B : st.E.A;
                    NetNode n = nodes[nodeId];
                    if (n.J != null && n.J.MainChain == c)
                        junctionIndex.Add(new KeyValuePair<Junc, int>(n.J, pts.Count - 1));
                }
                for (int i = (k == 0 ? 0 : 1); i < ep.Count; i++) pts.Add(ep[i]);
            }
            double[] cum = Cumulative(pts);
            double total = cum[cum.Length - 1];

            // trim the ends that connect to crossroad arms
            Vec2 startNode = pts[0];
            Vec2 endNode = pts[pts.Count - 1];
            var finalPts = new List<Vec2>();
            double d0 = hasStartArm ? Vec2.Distance(startNode, startArm.P) : 0;
            double d1 = hasEndArm ? Vec2.Distance(endNode, endArm.P) : 0;
            if (hasStartArm) finalPts.Add(startArm.P);
            for (int i = 0; i < pts.Count; i++)
            {
                if (hasStartArm && cum[i] <= d0 + 1.0) continue;
                if (hasEndArm && cum[i] >= total - d1 - 1.0) continue;
                finalPts.Add(pts[i]);
            }
            if (hasEndArm) finalPts.Add(endArm.P);
            if (finalPts.Count < 2)
            {
                finalPts.Clear();
                finalPts.Add(hasStartArm ? startArm.P : startNode);
                finalPts.Add(hasEndArm ? endArm.P : endNode);
            }

            RoadPath path;
            try
            {
                path = new RoadPath(finalPts);
            }
            catch (ArgumentException)
            {
                path = new RoadPath(new List<Vec2> { finalPts[0], finalPts[0] + new Vec2(0, 1) });
            }

            var constraints = new RoadConstraints();
            if (hasStartArm) constraints.StartHeading = startArm.H;
            if (hasEndArm)
            {
                constraints.EndHeading = Geo.WrapDeg(endArm.H + 180.0);
                constraints.EndIsFixed = true;
            }
            constraints.AllowStartCap = nodes[c.FirstEnd.NodeId].Ends.Count == 1;
            constraints.AllowEndCap = nodes[c.LastEnd.NodeId].Ends.Count == 1;

            var activeJunctions = new List<Junc>();
            foreach (KeyValuePair<Junc, int> kv in junctionIndex)
            {
                Junc j = kv.Key;
                if (!j.Active) continue;
                double approx = cum[kv.Value] - (hasStartArm ? d0 : 0);
                Projection pr = path.Project(j.Node.P, approx - 40.0, approx + 40.0);

                // forward direction of the road at the junction = direction of the outgoing through arm
                EdgeEnd outgoing = OutgoingMain(c, j);
                double fwd = j.Dir[outgoing];
                var wp = new JunctionWaypoint { Id = j.Info.Id, S = pr.S, Point = j.Node.P, Part = j.Part };
                if (j.Info.Type == 'T' && j.Branches.Count == 1)
                {
                    double side = Geo.WrapDeg(j.Dir[j.Branches[0]] - fwd);
                    bool branchLeft = side < 0;
                    // model: side road on the left (LD/LH) or on the right (PD/PH)
                    bool partLeft = j.Part.HasLeftArm;
                    bool forward = branchLeft == partLeft;
                    wp.AllowForward = forward;
                    wp.AllowReversed = !forward;
                }
                constraints.Junctions.Add(wp);
                activeJunctions.Add(j);
            }

            List<RoadPart> parts = PartsForFamily(lib, c.Family, mainFamily, enabledParts);
            RoadPart cap = EndCapForFamily(lib, c.Family, endCap);

            BuildResult r = RoadBuilder.Build(path, parts, cap, bs, constraints, progress, ct);
            if (hasStartArm)
            {
                Junc sj = BranchJunction(nodes, c.FirstEnd);
                if (sj != null)
                {
                    r.StartJunctionId = sj.Info.Id;
                    r.StartArmLeft = startArm.Left;
                }
            }

            string name = "Road " + roadNo + " (" + c.Family;
            if (activeJunctions.Count > 0) name += ", " + activeJunctions.Count + " crossroad" + (activeJunctions.Count > 1 ? "s" : "");
            if (hasStartArm || hasEndArm) name += ", side road";
            name += ")";
            path.Name = name;

            List<JunctionWaypoint> wps = constraints.Junctions.OrderBy(w => w.S).ToList();
            for (int i = 0; i + 1 < wps.Count; i++)
            {
                double need = (wps[i].Part.Length + wps[i + 1].Part.Length) / 2.0;
                if (wps[i + 1].S - wps[i].S < need)
                    r.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "Junctions {0} and {1} are only {2:F1} m apart, but the crossroad parts need {3:F1} m – they are shifted apart.",
                        wps[i].Id, wps[i + 1].Id, wps[i + 1].S - wps[i].S, need));
            }
            if (forced)
                r.Warnings.Add("Road depends on crossroads that could not be placed first (circular network) – check the joints at its ends.");
            if (hasEndArm && r.EndGap > 0.3)
                r.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "The road meets the crossroad arm with a gap of {0:F2} m – adjust the last part by hand or move the line slightly.", r.EndGap));

            // record placed crossroads and their side arms
            foreach (Junc j in activeJunctions)
            {
                PlacedPart pp = r.Parts.FirstOrDefault(p => p.JunctionId == j.Info.Id);
                j.Resolved = true;
                if (pp == null) continue;
                j.Info.Placed = pp;
                AssignArms(j, pp);
            }
            return r;
        }

        private static void AssignArms(Junc j, PlacedPart pp)
        {
            var arms = new List<Arm>();
            foreach (bool left in new[] { true, false })
            {
                if (left ? !pp.Part.HasLeftArm : !pp.Part.HasRightArm) continue;
                Vec2 c;
                double h;
                pp.Part.GetArm(left, out c, out h);
                arms.Add(new Arm { P = pp.ToWorld(c), H = Geo.Norm360(pp.Yaw + h), Left = left });
            }
            var free = new List<Arm>(arms);
            // each side road takes the arm pointing most closely in its direction
            foreach (EdgeEnd b in j.Branches.OrderBy(b => free.Count == 0 ? 0 : free.Min(a => Math.Abs(Geo.WrapDeg(a.H - j.Dir[b])))))
            {
                if (free.Count == 0) break;
                Arm best = free.OrderBy(a => Math.Abs(Geo.WrapDeg(a.H - j.Dir[b]))).First();
                free.Remove(best);
                j.Arms[b] = best;
            }
        }

        private static bool TryGetPlacedArm(List<NetNode> nodes, EdgeEnd ee, out Arm arm)
        {
            arm = new Arm();
            Junc j = BranchJunction(nodes, ee);
            if (j == null || !j.Active || !j.Resolved) return false;
            return j.Arms.TryGetValue(ee, out arm);
        }

        /// <summary>Junction for which this edge end is a side road, or null.</summary>
        private static Junc BranchJunction(List<NetNode> nodes, EdgeEnd ee)
        {
            Junc j = nodes[ee.NodeId].J;
            if (j == null || !j.Branches.Contains(ee)) return null;
            return j;
        }

        private static int UnresolvedArms(List<NetNode> nodes, Chain c)
        {
            int n = 0;
            foreach (EdgeEnd ee in new[] { c.FirstEnd, c.LastEnd })
            {
                Junc j = BranchJunction(nodes, ee);
                if (j != null && j.Active && !j.Resolved) n++;
            }
            return n;
        }

        private static EdgeEnd OutgoingMain(Chain c, Junc j)
        {
            // the through arm whose edge is traversed away from the junction node
            foreach (Step st in c.Steps)
            {
                EdgeEnd startOfStep = st.Rev ? st.E.EndEnd : st.E.StartEnd;
                if (startOfStep.NodeId == j.Node.Id && (startOfStep == j.MainA || startOfStep == j.MainB))
                    return startOfStep;
            }
            return j.MainB;
        }

        private static void ReverseChain(Chain c)
        {
            c.Steps.Reverse();
            foreach (Step s in c.Steps) s.Rev = !s.Rev;
            EdgeEnd t = c.FirstEnd; c.FirstEnd = c.LastEnd; c.LastEnd = t;
            c.Mains.Reverse();
        }

        private static List<RoadPart> PartsForFamily(RoadPartLibrary lib, string family, string mainFamily, IList<RoadPart> enabledParts)
        {
            if (string.Equals(family, mainFamily, StringComparison.OrdinalIgnoreCase) && enabledParts != null && enabledParts.Count > 0)
                return enabledParts.ToList();

            // other road types: use the same part sizes (suffixes) as ticked for the selected road type
            var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (enabledParts != null) foreach (RoadPart p in enabledParts) suffixes.Add(p.Suffix);
            List<RoadPart> all = lib.GetFamily(family).Where(p => p.Kind == PartKind.Straight || p.Kind == PartKind.Curve).ToList();
            List<RoadPart> sel = all.Where(p => suffixes.Contains(p.Suffix)).ToList();
            return sel.Count > 0 ? sel : all;
        }

        private static RoadPart EndCapForFamily(RoadPartLibrary lib, string family, RoadPart endCap)
        {
            if (endCap == null) return null;
            if (string.Equals(endCap.Family, family, StringComparison.OrdinalIgnoreCase)) return endCap;
            List<RoadPart> caps = lib.GetFamily(family).Where(p => p.Kind == PartKind.EndCap).ToList();
            return caps.FirstOrDefault(p => string.Equals(p.Suffix, endCap.Suffix, StringComparison.OrdinalIgnoreCase)) ?? caps.FirstOrDefault();
        }

        /// <summary>All crossroad parts whose through road is the given road type.</summary>
        public static List<RoadPart> GetCrossroads(RoadPartLibrary lib, string family, char type)
        {
            return lib.Parts
                .Where(p => p.Kind == PartKind.Crossroad && p.CrossType == type &&
                            string.Equals(p.CrossMainFamily, family, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => string.IsNullOrEmpty(p.CrossEndFamily) ? 0 : 1)
                .ThenBy(p => string.Equals(p.CrossBranchFamily, family, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static RoadPart ChooseCrossroad(RoadPartLibrary lib, string family, char type, string mainFamily, NetworkSettings ns)
        {
            List<RoadPart> list = GetCrossroads(lib, family, type);
            if (list.Count == 0) return null;
            string wanted = type == 'X' ? ns.XJunctionPart : ns.TJunctionPart;
            if (!string.IsNullOrEmpty(wanted) && string.Equals(family, mainFamily, StringComparison.OrdinalIgnoreCase))
            {
                RoadPart w = list.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (w != null) return w;
            }
            return list[0];
        }

        // ------------------------------------------------------------------ junction arms

        private static double ArmDirection(EdgeEnd ee)
        {
            List<Vec2> pts = ee.E.Pts;
            double d = Math.Min(12.0, ee.E.Length * 0.5);
            Vec2 p0 = ee.AtStart ? pts[0] : pts[pts.Count - 1];
            double acc = 0;
            Vec2 prev = p0;
            int n = pts.Count;
            for (int k = 1; k < n; k++)
            {
                Vec2 q = ee.AtStart ? pts[k] : pts[n - 1 - k];
                double seg = Vec2.Distance(prev, q);
                if (acc + seg >= d && seg > 0)
                {
                    Vec2 target = Vec2.Lerp(prev, q, (d - acc) / seg);
                    return Geo.Bearing(target - p0);
                }
                acc += seg;
                prev = q;
            }
            return Geo.Bearing(prev - p0);
        }

        private static double Opposition(double a, double b)
        {
            // 0 = exactly opposite directions (straight through), 180 = same direction
            return 180.0 - Math.Abs(Geo.WrapDeg(a - b));
        }

        /// <summary>How well two arms form a through road (lower = better): bend angle, plus a bonus for arms of the same input line.</summary>
        private static double PairScore(Junc j, EdgeEnd a, EdgeEnd b)
        {
            double o = Opposition(j.Dir[a], j.Dir[b]);
            if (a.E.Source != b.E.Source) o += 10.0;
            return o;
        }

        private static void PairArms(Junc j)
        {
            List<EdgeEnd> e = j.Node.Ends;
            if (e.Count == 3)
            {
                double best = double.MaxValue;
                int bi = 0, bk = 1;
                for (int i = 0; i < 3; i++)
                    for (int k = i + 1; k < 3; k++)
                    {
                        double o = PairScore(j, e[i], e[k]);
                        if (o < best) { best = o; bi = i; bk = k; }
                    }
                j.MainA = e[bi];
                j.MainB = e[bk];
                j.Branches.Add(e.First(x => x != e[bi] && x != e[bk]));
            }
            else
            {
                int[][] pairings = { new[] { 0, 1, 2, 3 }, new[] { 0, 2, 1, 3 }, new[] { 0, 3, 1, 2 } };
                double best = double.MaxValue;
                int[] bp = pairings[0];
                foreach (int[] p in pairings)
                {
                    double o = PairScore(j, e[p[0]], e[p[1]]) + PairScore(j, e[p[2]], e[p[3]]);
                    if (o < best) { best = o; bp = p; }
                }
                // the straighter pair is the through road; if both are about equally straight, the longer one
                double s1 = PairScore(j, e[bp[0]], e[bp[1]]), s2 = PairScore(j, e[bp[2]], e[bp[3]]);
                bool firstStraighter;
                if (Math.Abs(s1 - s2) > 5.0) firstStraighter = s1 < s2;
                else firstStraighter = e[bp[0]].E.SourceLength + e[bp[1]].E.SourceLength >= e[bp[2]].E.SourceLength + e[bp[3]].E.SourceLength;
                int a = firstStraighter ? 0 : 2, b = firstStraighter ? 2 : 0;
                j.MainA = e[bp[a]];
                j.MainB = e[bp[a + 1]];
                j.Branches.Add(e[bp[b]]);
                j.Branches.Add(e[bp[b + 1]]);
            }
        }

        // ------------------------------------------------------------------ chains

        private static EdgeEnd Continue(List<NetNode> nodes, EdgeEnd arriving)
        {
            NetNode n = nodes[arriving.NodeId];
            if (n.Ends.Count == 2)
                return n.Ends[0] == arriving ? n.Ends[1] : n.Ends[0];
            if (n.J != null)
            {
                if (arriving == n.J.MainA) return n.J.MainB;
                if (arriving == n.J.MainB) return n.J.MainA;
            }
            return null;
        }

        private static List<Chain> BuildChains(List<Edge> edges, List<NetNode> nodes)
        {
            var chains = new List<Chain>();
            foreach (Edge e0 in edges)
            {
                if (e0.Visited) continue;
                e0.Visited = true;
                var steps = new LinkedList<Step>();
                steps.AddLast(new Step { E = e0, Rev = false });

                // forward
                EdgeEnd arriving = e0.EndEnd;
                while (true)
                {
                    EdgeEnd next = Continue(nodes, arriving);
                    if (next == null || next.E.Visited) break;
                    next.E.Visited = true;
                    steps.AddLast(new Step { E = next.E, Rev = !next.AtStart });
                    arriving = next.Other;
                }
                EdgeEnd last = arriving;

                // backward
                arriving = e0.StartEnd;
                while (true)
                {
                    EdgeEnd next = Continue(nodes, arriving);
                    if (next == null || next.E.Visited) break;
                    next.E.Visited = true;
                    // traversed towards the junction: the step runs from next.Other to next
                    steps.AddFirst(new Step { E = next.E, Rev = next.AtStart });
                    arriving = next.Other;
                }
                EdgeEnd first = arriving;

                var c = new Chain { Id = chains.Count + 1, Steps = steps.ToList(), FirstEnd = first, LastEnd = last };

                // closed loop whose ends meet at a junction: open the loop in the middle of its longest edge instead,
                // so that every junction lies inside the road
                if (first.NodeId == last.NodeId && nodes[first.NodeId].J != null && Continue(nodes, last) == first)
                    OpenLoop(c, nodes);

                c.Length = c.Steps.Sum(s => s.E.Length);
                // junctions passed as through road
                for (int k = 1; k < c.Steps.Count; k++)
                {
                    Step st = c.Steps[k];
                    NetNode n = nodes[st.Rev ? st.E.B : st.E.A];
                    if (n.J != null)
                    {
                        c.Mains.Add(n.J);
                        n.J.MainChain = c;
                    }
                }
                chains.Add(c);
            }
            return chains;
        }

        /// <summary>Splits the longest edge of a closed chain at its middle and makes that point the start/end of the chain.</summary>
        private static void OpenLoop(Chain c, List<NetNode> nodes)
        {
            int k = 0;
            for (int i = 1; i < c.Steps.Count; i++)
                if (c.Steps[i].E.Length > c.Steps[k].E.Length) k = i;
            Step sk = c.Steps[k];
            Edge e = sk.E;

            // split the edge geometry at half its length
            double[] cum = Cumulative(e.Pts);
            double half = e.Length / 2.0;
            Vec2 mid = PointAt(e.Pts, cum, half);
            var p1 = new List<Vec2>();
            var p2 = new List<Vec2> { mid };
            for (int i = 0; i < e.Pts.Count; i++)
            {
                if (cum[i] < half - 1e-6) p1.Add(e.Pts[i]);
                else if (cum[i] > half + 1e-6) p2.Add(e.Pts[i]);
            }
            p1.Add(mid);

            var v = new NetNode { Id = nodes.Count, P = mid };
            nodes.Add(v);
            var e1 = new Edge { Id = -1, Pts = p1, A = e.A, B = v.Id, Length = half, Source = e.Source, SourceLength = e.SourceLength, Visited = true };
            var e2 = new Edge { Id = -1, Pts = p2, A = v.Id, B = e.B, Length = e.Length - half, Source = e.Source, SourceLength = e.SourceLength, Visited = true };
            // keep the existing edge-end objects (junctions refer to them)
            e1.StartEnd = e.StartEnd; e1.StartEnd.E = e1;
            e1.EndEnd = new EdgeEnd { E = e1, AtStart = false };
            e2.StartEnd = new EdgeEnd { E = e2, AtStart = true };
            e2.EndEnd = e.EndEnd; e2.EndEnd.E = e2;
            v.Ends.Add(e1.EndEnd);
            v.Ends.Add(e2.StartEnd);

            var steps = new List<Step>();
            if (!sk.Rev)
            {
                steps.Add(new Step { E = e2, Rev = false });
                for (int i = k + 1; i < c.Steps.Count; i++) steps.Add(c.Steps[i]);
                for (int i = 0; i < k; i++) steps.Add(c.Steps[i]);
                steps.Add(new Step { E = e1, Rev = false });
                c.FirstEnd = e2.StartEnd;
                c.LastEnd = e1.EndEnd;
            }
            else
            {
                steps.Add(new Step { E = e1, Rev = true });
                for (int i = k + 1; i < c.Steps.Count; i++) steps.Add(c.Steps[i]);
                for (int i = 0; i < k; i++) steps.Add(c.Steps[i]);
                steps.Add(new Step { E = e2, Rev = true });
                c.FirstEnd = e1.EndEnd;
                c.LastEnd = e2.StartEnd;
            }
            c.Steps = steps;
        }

        // ------------------------------------------------------------------ splitting & graph

        private static List<Vec2> Clean(IList<Vec2> pts)
        {
            var l = new List<Vec2>();
            foreach (Vec2 p in pts)
                if (l.Count == 0 || Vec2.Distance(l[l.Count - 1], p) > 1e-4) l.Add(p);
            return l;
        }

        private static double[] Cumulative(IList<Vec2> pts)
        {
            var c = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) c[i] = c[i - 1] + Vec2.Distance(pts[i - 1], pts[i]);
            return c;
        }

        private static void ProjectFull(List<Vec2> l, double[] cum, Vec2 q, out double s, out double d2)
        {
            s = 0;
            d2 = double.MaxValue;
            for (int i = 0; i + 1 < l.Count; i++)
            {
                Vec2 a = l[i], b = l[i + 1];
                Vec2 ab = b - a;
                double len2 = ab.LengthSquared;
                double t = len2 > 0 ? ((q.X - a.X) * ab.X + (q.Z - a.Z) * ab.Z) / len2 : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                Vec2 p = a + ab * t;
                double dd = (q - p).LengthSquared;
                if (dd < d2)
                {
                    d2 = dd;
                    s = cum[i] + t * Math.Sqrt(len2);
                }
            }
        }

        private static Vec2 PointAt(List<Vec2> l, double[] cum, double s)
        {
            if (s <= 0) return l[0];
            for (int i = 0; i + 1 < l.Count; i++)
            {
                if (s <= cum[i + 1])
                {
                    double seg = cum[i + 1] - cum[i];
                    return seg > 0 ? Vec2.Lerp(l[i], l[i + 1], (s - cum[i]) / seg) : l[i];
                }
            }
            return l[l.Count - 1];
        }

        private static void BBox(List<Vec2> l, out double minX, out double minZ, out double maxX, out double maxZ)
        {
            minX = minZ = double.MaxValue;
            maxX = maxZ = double.MinValue;
            foreach (Vec2 p in l)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
        }

        /// <summary>Splits lines where a line end touches another line or where two lines cross.</summary>
        private static List<KeyValuePair<int, List<Vec2>>> SplitLines(IList<List<Vec2>> input, double snap)
        {
            List<List<Vec2>> lines = input.Select(Clean).Where(l => l.Count >= 2).ToList();
            int n = lines.Count;
            var cums = lines.Select(l => Cumulative(l)).ToList();
            var splits = lines.Select(l => new List<double>()).ToList();
            var box = new double[n, 4];
            for (int i = 0; i < n; i++)
            {
                double a, b, c, d;
                BBox(lines[i], out a, out b, out c, out d);
                box[i, 0] = a - snap; box[i, 1] = b - snap; box[i, 2] = c + snap; box[i, 3] = d + snap;
            }

            for (int i = 0; i < n; i++)
            {
                double li = cums[i][cums[i].Length - 1];
                // line ends lying on another line (T-junction without shared vertex)
                foreach (Vec2 e in new[] { lines[i][0], lines[i][lines[i].Count - 1] })
                {
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        if (e.X < box[j, 0] || e.X > box[j, 2] || e.Z < box[j, 1] || e.Z > box[j, 3]) continue;
                        double s, d2;
                        ProjectFull(lines[j], cums[j], e, out s, out d2);
                        double lj = cums[j][cums[j].Length - 1];
                        if (d2 <= snap * snap && s > snap && s < lj - snap) splits[j].Add(s);
                    }
                }
                // proper crossings (X-crossroad without shared vertex)
                for (int j = i + 1; j < n; j++)
                {
                    if (box[i, 2] < box[j, 0] || box[j, 2] < box[i, 0] || box[i, 3] < box[j, 1] || box[j, 3] < box[i, 1]) continue;
                    double lj = cums[j][cums[j].Length - 1];
                    for (int a = 0; a + 1 < lines[i].Count; a++)
                    {
                        for (int b = 0; b + 1 < lines[j].Count; b++)
                        {
                            double t, u;
                            if (!SegmentIntersection(lines[i][a], lines[i][a + 1], lines[j][b], lines[j][b + 1], out t, out u)) continue;
                            double si = cums[i][a] + t * (cums[i][a + 1] - cums[i][a]);
                            double sj = cums[j][b] + u * (cums[j][b + 1] - cums[j][b]);
                            if (si > snap && si < li - snap && sj > snap && sj < lj - snap)
                            {
                                splits[i].Add(si);
                                splits[j].Add(sj);
                            }
                        }
                    }
                }
            }

            var pieces = new List<KeyValuePair<int, List<Vec2>>>();
            for (int i = 0; i < n; i++)
            {
                double li = cums[i][cums[i].Length - 1];
                var cuts = new List<double> { 0 };
                foreach (double s in splits[i].OrderBy(x => x))
                    if (s - cuts[cuts.Count - 1] > 0.5 && li - s > 0.5) cuts.Add(s);
                cuts.Add(li);
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    double s0 = cuts[k], s1 = cuts[k + 1];
                    var p = new List<Vec2> { PointAt(lines[i], cums[i], s0) };
                    for (int v = 0; v < lines[i].Count; v++)
                        if (cums[i][v] > s0 + 1e-6 && cums[i][v] < s1 - 1e-6) p.Add(lines[i][v]);
                    p.Add(PointAt(lines[i], cums[i], s1));
                    p = Clean(p);
                    if (p.Count >= 2) pieces.Add(new KeyValuePair<int, List<Vec2>>(i, p));
                }
            }
            return pieces;
        }

        private static bool SegmentIntersection(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2, out double t, out double u)
        {
            t = u = 0;
            Vec2 r = p2 - p1, s = q2 - q1;
            double den = r.X * s.Z - r.Z * s.X;
            if (Math.Abs(den) < 1e-12) return false;
            Vec2 qp = q1 - p1;
            t = (qp.X * s.Z - qp.Z * s.X) / den;
            u = (qp.X * r.Z - qp.Z * r.X) / den;
            return t > 1e-9 && t < 1 - 1e-9 && u > 1e-9 && u < 1 - 1e-9;
        }

        private static void BuildGraph(List<KeyValuePair<int, List<Vec2>>> pieces, double snap, out List<NetNode> nodes, out List<Edge> edges)
        {
            var ns = new List<NetNode>();
            var sums = new List<Vec2>();
            Func<Vec2, int> nodeFor = p =>
            {
                for (int i = 0; i < ns.Count; i++)
                    if (Vec2.Distance(ns[i].P, p) <= snap) return i;
                ns.Add(new NetNode { Id = ns.Count, P = p });
                sums.Add(Vec2.Zero);
                return ns.Count - 1;
            };

            var raw = new List<KeyValuePair<int, int>>();
            var sourceLen = new Dictionary<int, double>();
            foreach (KeyValuePair<int, List<Vec2>> kvp in pieces)
            {
                double l0;
                sourceLen.TryGetValue(kvp.Key, out l0);
                sourceLen[kvp.Key] = l0 + Cumulative(kvp.Value).Last();
            }
            foreach (KeyValuePair<int, List<Vec2>> kvp in pieces)
            {
                List<Vec2> p = kvp.Value;
                int a = nodeFor(p[0]);
                int b = nodeFor(p[p.Count - 1]);
                raw.Add(new KeyValuePair<int, int>(a, b));
                sums[a] += p[0]; ns[a].Count++;
                sums[b] += p[p.Count - 1]; ns[b].Count++;
            }
            for (int i = 0; i < ns.Count; i++)
                if (ns[i].Count > 0) ns[i].P = sums[i] * (1.0 / ns[i].Count);

            edges = new List<Edge>();
            for (int k = 0; k < pieces.Count; k++)
            {
                int a = raw[k].Key, b = raw[k].Value;
                var pts = new List<Vec2>(pieces[k].Value);
                pts[0] = ns[a].P;
                pts[pts.Count - 1] = ns[b].P;
                pts = Clean(pts);
                if (pts.Count < 2) continue;
                double len = Cumulative(pts).Last();
                if (len < 0.5) continue;
                var e = new Edge { Id = edges.Count, Pts = pts, A = a, B = b, Length = len, Source = pieces[k].Key };
                double sl;
                e.SourceLength = sourceLen.TryGetValue(e.Source, out sl) ? sl : len;
                e.StartEnd = new EdgeEnd { E = e, AtStart = true };
                e.EndEnd = new EdgeEnd { E = e, AtStart = false };
                ns[a].Ends.Add(e.StartEnd);
                ns[b].Ends.Add(e.EndEnd);
                edges.Add(e);
            }

            // remove short dangling overshoots at junctions (a line drawn slightly across another line)
            double dangle = Math.Max(5.0, 3.0 * snap);
            foreach (Edge e in edges.ToList())
            {
                int da = ns[e.A].Ends.Count, db = ns[e.B].Ends.Count;
                bool dangling = (da == 1 && db >= 3) || (db == 1 && da >= 3);
                if (dangling && e.Length < dangle)
                {
                    ns[e.A].Ends.Remove(e.StartEnd);
                    ns[e.B].Ends.Remove(e.EndEnd);
                    edges.Remove(e);
                }
            }
            nodes = ns;
        }

        private sealed class ScaledProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            private readonly double _offset, _scale;
            public ScaledProgress(IProgress<double> inner, double offset, double scale) { _inner = inner; _offset = offset; _scale = scale; }
            public void Report(double value) { _inner.Report(_offset + value * _scale); }
        }
    }
}
