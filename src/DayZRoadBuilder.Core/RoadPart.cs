using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DayZRoadBuilder.Core
{
    public enum PartKind
    {
        Straight,
        Curve,
        EndCap,
        Crosswalk,
        Crossroad,
        Unknown
    }

    /// <summary>
    /// A road part (P3D), described by its four memory points:
    /// LB/PB = left/right point at the beginning, LE/PE = left/right point at the end.
    /// All coordinates are model coordinates (X = right, Z = forward).
    /// </summary>
    public sealed class RoadPart
    {
        private static readonly Regex NameRegex = new Regex(@"^(?<fam>.+?)_(?<rest>\d.*)$", RegexOptions.Compiled);

        public string Name { get; private set; }
        public string FilePath { get; private set; }
        public string Folder { get; private set; }
        public string Family { get; private set; }
        public string Suffix { get; private set; }
        public PartKind Kind { get; private set; }

        public Vec2 LB { get; private set; }
        public Vec2 PB { get; private set; }
        public Vec2 LE { get; private set; }
        public Vec2 PE { get; private set; }

        /// <summary>Centre of the start edge (midpoint of LB and PB).</summary>
        public Vec2 StartCenter { get; private set; }
        /// <summary>Centre of the end edge (midpoint of LE and PE).</summary>
        public Vec2 EndCenter { get; private set; }
        /// <summary>Driving direction at the start edge (compass bearing in the model, 0 = +Z).</summary>
        public double StartHeading { get; private set; }
        /// <summary>Driving direction at the end edge.</summary>
        public double EndHeading { get; private set; }
        /// <summary>Heading change in degrees (+ = right turn, - = left turn).</summary>
        public double TurnAngle { get; private set; }
        /// <summary>Centreline length (arc length for curves).</summary>
        public double Length { get; private set; }
        /// <summary>Centreline radius (infinite for straights).</summary>
        public double Radius { get; private set; }
        public double Width { get; private set; }
        /// <summary>Bounding box centre over all LODs (the engine's autocenter reference point).</summary>
        public Vec2 BoundingCenter { get; private set; }

        public bool IsStraight { get { return Math.Abs(TurnAngle) < 0.01; } }

        // ---- crossroads (kr_t_* / kr_x_*) ----

        /// <summary>'T' or 'X' for crossroads, otherwise '\0'.</summary>
        public char CrossType { get; private set; }
        /// <summary>Road type of the through road of a crossroad (e.g. "asf1" in kr_t_asf1_asf2).</summary>
        public string CrossMainFamily { get; private set; }
        /// <summary>Road type of the side road(s) of a crossroad (e.g. "asf2" in kr_t_asf1_asf2).</summary>
        public string CrossBranchFamily { get; private set; }
        /// <summary>Optional road type of the through road after the crossroad (e.g. "asf3" in kr_x_city_city_asf3).</summary>
        public string CrossEndFamily { get; private set; }
        /// <summary>Side road on the left (memory points LD / LH).</summary>
        public bool HasLeftArm { get; private set; }
        /// <summary>Side road on the right (memory points PD / PH).</summary>
        public bool HasRightArm { get; private set; }
        public Vec2 LD { get; private set; }
        public Vec2 LH { get; private set; }
        public Vec2 PD { get; private set; }
        public Vec2 PH { get; private set; }

        /// <summary>
        /// Exit edge of a side road of a crossroad in model coordinates: centre point and outward driving direction
        /// (compass bearing in the model; left arm = 270°, right arm = 90°).
        /// </summary>
        public void GetArm(bool left, out Vec2 center, out double heading)
        {
            if (left)
            {
                center = Vec2.Lerp(LD, LH, 0.5);
                heading = Geo.WrapDeg(Geo.Bearing(LH - LD) - 90.0);
            }
            else
            {
                center = Vec2.Lerp(PD, PH, 0.5);
                heading = Geo.WrapDeg(Geo.Bearing(PD - PH) - 90.0);
            }
        }

        /// <summary>Width of the side road edge of a crossroad.</summary>
        public double ArmWidth(bool left)
        {
            return left ? Vec2.Distance(LD, LH) : Vec2.Distance(PD, PH);
        }

        public string Description
        {
            get
            {
                string s;
                switch (Kind)
                {
                    case PartKind.Straight: s = string.Format(CultureInfo.InvariantCulture, "Straight {0:0.##} m", Length); break;
                    case PartKind.Curve: s = string.Format(CultureInfo.InvariantCulture, "Curve {0:0.##}° R{1:0.#} ({2:0.##} m)", Math.Abs(TurnAngle), Radius, Length); break;
                    case PartKind.EndCap: s = string.Format(CultureInfo.InvariantCulture, "End piece {0:0.##} m", Length); break;
                    case PartKind.Crosswalk: s = string.Format(CultureInfo.InvariantCulture, "Crosswalk {0:0.##} m", Length); break;
                    case PartKind.Crossroad:
                        s = (CrossType == 'X' ? "X-crossroad " : "T-junction ") + (CrossMainFamily ?? "?") + " / side road " + (CrossBranchFamily ?? "?");
                        if (!string.IsNullOrEmpty(CrossEndFamily)) s += " / continues as " + CrossEndFamily;
                        break;
                    default: s = "?"; break;
                }
                return s;
            }
        }

        public override string ToString()
        {
            return Name + "   (" + Description + ")";
        }

        /// <summary>Point on the centreline in model coordinates, t in [0,1] from start to end.</summary>
        public Vec2 CenterlineLocal(double t)
        {
            if (IsStraight)
                return Vec2.Lerp(StartCenter, EndCenter, t);
            double a = Math.Abs(TurnAngle) * t * Geo.Deg2Rad;
            double sg = Math.Sign(TurnAngle);
            var p = new Vec2(sg * Radius * (1.0 - Math.Cos(a)), Radius * Math.Sin(a));
            return StartCenter + Geo.Rotate(p, StartHeading);
        }

        /// <summary>Driving direction along the centreline at t.</summary>
        public double HeadingLocal(double t)
        {
            return StartHeading + TurnAngle * t;
        }

        /// <summary>Outline of the part (left edge forward, right edge backward) in model coordinates.</summary>
        public Vec2[] GetLocalOutline(int segments)
        {
            if (Kind == PartKind.Crossroad)
                return GetCrossroadOutline();
            if (IsStraight || segments < 1) segments = 1;
            var pts = new Vec2[(segments + 1) * 2];
            double half = Width / 2.0;
            for (int i = 0; i <= segments; i++)
            {
                double t = (double)i / segments;
                Vec2 c = CenterlineLocal(t);
                Vec2 right = Geo.Direction(HeadingLocal(t) + 90.0);
                pts[i] = c - right * half;
                pts[pts.Length - 1 - i] = c + right * half;
            }
            if (Kind != PartKind.Crossroad)
            {
                // snap the corners exactly onto the memory points
                pts[0] = LB;
                pts[segments] = LE;
                pts[segments + 1] = PE;
                pts[pts.Length - 1] = PB;
            }
            return pts;
        }

        private Vec2[] GetCrossroadOutline()
        {
            var pts = new List<Vec2>();
            pts.Add(LB);
            pts.Add(PB);
            if (HasRightArm)
            {
                pts.Add(new Vec2(PB.X, PD.Z));
                pts.Add(PD);
                pts.Add(PH);
                pts.Add(new Vec2(PE.X, PH.Z));
            }
            pts.Add(PE);
            pts.Add(LE);
            if (HasLeftArm)
            {
                pts.Add(new Vec2(LE.X, LH.Z));
                pts.Add(LH);
                pts.Add(LD);
                pts.Add(new Vec2(LB.X, LD.Z));
            }
            return pts.ToArray();
        }

        /// <summary>Loads a road part from an MLOD P3D. Returns null if the road memory points are missing.</summary>
        public static RoadPart FromFile(string path)
        {
            List<P3dLod> lods = P3dReader.ReadMlod(path);

            P3dLod mem = lods.FirstOrDefault(l => l.IsMemoryLod && HasRoadPoints(l)) ?? lods.FirstOrDefault(HasRoadPoints);
            if (mem == null) return null;

            var part = new RoadPart();
            part.FilePath = path;
            part.Name = Path.GetFileNameWithoutExtension(path);
            part.Folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            part.LB = SelectionCenter(mem, "LB");
            part.PB = SelectionCenter(mem, "PB");
            part.LE = SelectionCenter(mem, "LE");
            part.PE = SelectionCenter(mem, "PE");

            // bounding box over all LODs with points
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (P3dLod l in lods)
            {
                foreach (P3dPoint p in l.Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }
            }
            part.BoundingCenter = new Vec2((minX + maxX) / 2.0, (minZ + maxZ) / 2.0);

            part.HasLeftArm = HasSel(mem, "LD") && HasSel(mem, "LH");
            part.HasRightArm = HasSel(mem, "PD") && HasSel(mem, "PH");
            if (part.HasLeftArm)
            {
                part.LD = SelectionCenter(mem, "LD");
                part.LH = SelectionCenter(mem, "LH");
            }
            if (part.HasRightArm)
            {
                part.PD = SelectionCenter(mem, "PD");
                part.PH = SelectionCenter(mem, "PH");
            }

            part.ComputeGeometry();
            part.Classify();
            return part;
        }

        private static bool HasRoadPoints(P3dLod l)
        {
            return HasSel(l, "LB") && HasSel(l, "PB") && HasSel(l, "LE") && HasSel(l, "PE");
        }

        private static bool HasSel(P3dLod l, string name)
        {
            List<int> idx;
            return l.Selections.TryGetValue(name, out idx) && idx.Count > 0;
        }

        private static Vec2 SelectionCenter(P3dLod l, string name)
        {
            List<int> idx = l.Selections[name];
            double x = 0, z = 0;
            foreach (int i in idx)
            {
                x += l.Points[i].X;
                z += l.Points[i].Z;
            }
            return new Vec2(x / idx.Count, z / idx.Count);
        }

        private void ComputeGeometry()
        {
            StartCenter = Vec2.Lerp(LB, PB, 0.5);
            EndCenter = Vec2.Lerp(LE, PE, 0.5);
            // forward direction = "left -> right" direction rotated 90° counter-clockwise
            StartHeading = Geo.WrapDeg(Geo.Bearing(PB - LB) - 90.0);
            EndHeading = Geo.WrapDeg(Geo.Bearing(PE - LE) - 90.0);
            TurnAngle = Geo.WrapDeg(EndHeading - StartHeading);
            Width = Vec2.Distance(LB, PB);

            double chord = Vec2.Distance(StartCenter, EndCenter);
            if (Math.Abs(TurnAngle) < 0.01)
            {
                Length = chord;
                Radius = double.PositiveInfinity;
            }
            else
            {
                double half = Math.Abs(TurnAngle) * Geo.Deg2Rad / 2.0;
                Radius = chord / (2.0 * Math.Sin(half));
                Length = Radius * Math.Abs(TurnAngle) * Geo.Deg2Rad;
            }
        }

        private void Classify()
        {
            string lower = Name.ToLowerInvariant();
            if (lower.StartsWith("kr_", StringComparison.Ordinal))
            {
                Family = "Crossroads";
                Suffix = Name;
                Kind = PartKind.Crossroad;
                // kr_<t|x>_<through road>_<side road>[_<through road after the crossroad>]
                string[] tok = Name.Split('_');
                if (tok.Length >= 4)
                {
                    CrossType = char.ToUpperInvariant(tok[1][0]);
                    CrossMainFamily = tok[2];
                    CrossBranchFamily = tok[3];
                    CrossEndFamily = tok.Length >= 5 ? tok[4] : null;
                }
                if (!HasLeftArm && !HasRightArm) Kind = PartKind.Unknown;
                else if (HasLeftArm && HasRightArm) CrossType = 'X';
                else if (CrossType != 'X') CrossType = 'T';
                return;
            }

            Match m = NameRegex.Match(Name);
            if (m.Success)
            {
                Family = m.Groups["fam"].Value;
                Suffix = m.Groups["rest"].Value;
            }
            else
            {
                Family = Name;
                Suffix = "";
            }

            string suf = Suffix.ToLowerInvariant();
            if (suf.Contains("konec") || suf.Contains("end"))
                Kind = PartKind.EndCap;
            else if (suf.Contains("crosswalk"))
                Kind = PartKind.Crosswalk;
            else if (IsStraight)
                Kind = PartKind.Straight;
            else
                Kind = PartKind.Curve;
        }
    }
}
