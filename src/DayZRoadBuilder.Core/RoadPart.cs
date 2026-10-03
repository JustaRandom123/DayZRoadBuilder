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
    /// Ein Straßenteil (P3D), beschrieben durch seine vier Memorypunkte:
    /// LB/PB = linker/rechter Punkt am Anfang, LE/PE = linker/rechter Punkt am Ende.
    /// Alle Koordinaten sind Modellkoordinaten (X rechts, Z vorwärts).
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

        /// <summary>Mitte der Anfangskante (Mitte zwischen LB und PB).</summary>
        public Vec2 StartCenter { get; private set; }
        /// <summary>Mitte der Endkante (Mitte zwischen LE und PE).</summary>
        public Vec2 EndCenter { get; private set; }
        /// <summary>Fahrtrichtung an der Anfangskante (Kompasskurs im Modell, 0 = +Z).</summary>
        public double StartHeading { get; private set; }
        /// <summary>Fahrtrichtung an der Endkante.</summary>
        public double EndHeading { get; private set; }
        /// <summary>Richtungsänderung in Grad (+ = Rechtskurve, - = Linkskurve).</summary>
        public double TurnAngle { get; private set; }
        /// <summary>Länge der Mittellinie (Bogenlänge bei Kurven).</summary>
        public double Length { get; private set; }
        /// <summary>Radius der Mittellinie (unendlich bei Geraden).</summary>
        public double Radius { get; private set; }
        public double Width { get; private set; }
        /// <summary>Mitte der Bounding Box über alle LODs (Autocenter-Referenzpunkt der Engine).</summary>
        public Vec2 BoundingCenter { get; private set; }

        public bool IsStraight { get { return Math.Abs(TurnAngle) < 0.01; } }

        public string Description
        {
            get
            {
                string s;
                switch (Kind)
                {
                    case PartKind.Straight: s = string.Format(CultureInfo.InvariantCulture, "Gerade {0:0.##} m", Length); break;
                    case PartKind.Curve: s = string.Format(CultureInfo.InvariantCulture, "Kurve {0:0.##}° R{1:0.#} ({2:0.##} m)", Math.Abs(TurnAngle), Radius, Length); break;
                    case PartKind.EndCap: s = string.Format(CultureInfo.InvariantCulture, "Endstück {0:0.##} m", Length); break;
                    case PartKind.Crosswalk: s = string.Format(CultureInfo.InvariantCulture, "Zebrastreifen {0:0.##} m", Length); break;
                    case PartKind.Crossroad: s = "Kreuzung"; break;
                    default: s = "?"; break;
                }
                return s;
            }
        }

        public override string ToString()
        {
            return Name + "   (" + Description + ")";
        }

        /// <summary>Punkt auf der Mittellinie im Modellkoordinatensystem, t in [0,1] von Anfang bis Ende.</summary>
        public Vec2 CenterlineLocal(double t)
        {
            if (IsStraight)
                return Vec2.Lerp(StartCenter, EndCenter, t);
            double a = Math.Abs(TurnAngle) * t * Geo.Deg2Rad;
            double sg = Math.Sign(TurnAngle);
            var p = new Vec2(sg * Radius * (1.0 - Math.Cos(a)), Radius * Math.Sin(a));
            return StartCenter + Geo.Rotate(p, StartHeading);
        }

        /// <summary>Fahrtrichtung entlang der Mittellinie bei t.</summary>
        public double HeadingLocal(double t)
        {
            return StartHeading + TurnAngle * t;
        }

        /// <summary>Umriss des Teils (linke Kante vorwärts, rechte Kante rückwärts) in Modellkoordinaten.</summary>
        public Vec2[] GetLocalOutline(int segments)
        {
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
                // Ecken exakt auf die Memorypunkte setzen
                pts[0] = LB;
                pts[segments] = LE;
                pts[segments + 1] = PE;
                pts[pts.Length - 1] = PB;
            }
            return pts;
        }

        /// <summary>Lädt ein Straßenteil aus einer MLOD-P3D. Liefert null, wenn keine Straßen-Memorypunkte vorhanden sind.</summary>
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

            // Bounding Box über alle LODs mit Punkten
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
            // Vorwärtsrichtung = Richtung "links -> rechts" um 90° gegen den Uhrzeigersinn gedreht
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
                Family = "Kreuzungen";
                Suffix = Name;
                Kind = PartKind.Crossroad;
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
