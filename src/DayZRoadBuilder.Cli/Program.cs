using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DayZRoadBuilder.Core;

namespace DayZRoadBuilder.Cli
{
    /// <summary>
    /// Kommandozeilen-Variante, z.B. für Batch-Verarbeitung:
    /// DayZRoadBuilder.Cli --parts "C:\...\Road Parts" --shp road.shp --family asf1 --out road.txt [Optionen]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fehler: " + ex.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("DayZ Road Builder (CLI)");
            Console.WriteLine("  --parts <ordner>      Ordner mit den Straßen-P3Ds (rekursiv)");
            Console.WriteLine("  --shp <datei.shp>     Polyline-Shapefile");
            Console.WriteLine("  --family <typ>        Straßentyp, z.B. asf1, city, mud, asf1enoch ...");
            Console.WriteLine("  --out <datei.txt>     Terrain-Builder-Objektliste");
            Console.WriteLine("  --exclude <a,b,...>   Teile ausschließen (Namen)");
            Console.WriteLine("  --endcap <name>       Endstück-Teil an Anfang und Ende");
            Console.WriteLine("  --flip-endcaps        Endstücke umdrehen");
            Console.WriteLine("  --reverse             Linienrichtung umkehren");
            Console.WriteLine("  --beam <n>            Suchbreite (Standard 12)");
            Console.WriteLine("  --piece-penalty <x>   Strafe pro Teil (Standard 1.0)");
            Console.WriteLine("  --turn-penalty <x>    Strafe pro Grad Kurve (Standard 0.05)");
            Console.WriteLine("  --end-tol <m>         Toleranz am Linienende (Standard 3.5)");
            Console.WriteLine("  --model-origin        Position = Modellursprung statt BBox-Mitte");
            Console.WriteLine("  --invert-yaw          Yaw-Vorzeichen umkehren");
            Console.WriteLine("  --yaw-offset <deg>    Yaw-Versatz");
            Console.WriteLine("  --offset-x <m> / --offset-y <m>   Koordinatenversatz");
            Console.WriteLine("  --list                Nur gefundene Straßentypen/Teile auflisten");
        }

        private static int Run(string[] args)
        {
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    opt[a] = args[i + 1];
                    i++;
                }
                else
                {
                    flags.Add(a);
                }
            }

            if (!opt.ContainsKey("--parts") || flags.Contains("--help"))
            {
                Usage();
                return 2;
            }

            RoadPartLibrary lib = RoadPartLibrary.Load(opt["--parts"]);
            foreach (string w in lib.Warnings) Console.WriteLine("Hinweis: " + w);

            if (flags.Contains("--list"))
            {
                foreach (string fam in lib.GetFamilies())
                {
                    Console.WriteLine(fam);
                    foreach (RoadPart p in lib.GetFamily(fam))
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "   {0,-40} {1}", p.Name, p.Description));
                }
                return 0;
            }

            if (!opt.ContainsKey("--shp") || !opt.ContainsKey("--family") || !opt.ContainsKey("--out"))
            {
                Usage();
                return 2;
            }

            string family = opt["--family"];
            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ex;
            if (opt.TryGetValue("--exclude", out ex))
                foreach (string n in ex.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) exclude.Add(n.Trim());

            List<RoadPart> parts = lib.GetFamily(family)
                .Where(p => (p.Kind == PartKind.Straight || p.Kind == PartKind.Curve) && !exclude.Contains(p.Name))
                .ToList();
            if (parts.Count == 0) throw new InvalidOperationException("Keine Teile für Straßentyp '" + family + "' gefunden.");

            RoadPart endCap = null;
            string capName;
            if (opt.TryGetValue("--endcap", out capName))
            {
                endCap = lib.Find(capName);
                if (endCap == null) throw new InvalidOperationException("Endstück nicht gefunden: " + capName);
            }

            var bs = new BuildSettings
            {
                BeamWidth = GetInt(opt, "--beam", 12),
                PiecePenalty = GetD(opt, "--piece-penalty", 1.0),
                TurnPenalty = GetD(opt, "--turn-penalty", 0.05),
                EndTolerance = GetD(opt, "--end-tol", 3.5),
                UseBoundingCenter = !flags.Contains("--model-origin"),
                FlipEndCaps = flags.Contains("--flip-endcaps")
            };
            var es = new ExportSettings
            {
                InvertYaw = flags.Contains("--invert-yaw"),
                YawOffset = GetD(opt, "--yaw-offset", 0),
                OffsetX = GetD(opt, "--offset-x", 0),
                OffsetY = GetD(opt, "--offset-y", 0)
            };

            List<ShapeRecord> shapes = ShapefileReader.Read(opt["--shp"]);
            var all = new List<PlacedPart>();
            int roadNo = 0;
            foreach (ShapeRecord rec in shapes)
            {
                foreach (List<Vec2> line in rec.Parts)
                {
                    var path = new RoadPath(line) { Name = "Datensatz " + rec.RecordNumber };
                    if (flags.Contains("--reverse")) path = path.Reversed();
                    BuildResult r = RoadBuilder.Build(path, parts, endCap, bs, null, CancellationToken.None);
                    foreach (PlacedPart p in r.Parts) p.RoadIndex = roadNo;
                    all.AddRange(r.Parts);
                    roadNo++;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}: Linie {1:F1} m -> {2} Teile, Straße {3:F1} m, max. Abw. {4:F2} m, RMS {5:F2} m, Endabstand {6:F2} m, max. Fuge {7:F4} m, {8:F0} ms",
                        path.Name, path.Length, r.Parts.Count, r.RoadLength, r.MaxDeviation, r.RmsDeviation, r.EndGap, r.MaxJointGap, r.Duration.TotalMilliseconds));
                    foreach (string w in r.Warnings) Console.WriteLine("  Warnung: " + w);
                }
            }

            TerrainBuilderExporter.Write(opt["--out"], all, es);
            Console.WriteLine(all.Count + " Objekte geschrieben: " + Path.GetFullPath(opt["--out"]));
            return 0;
        }

        private static double GetD(Dictionary<string, string> o, string k, double def)
        {
            string v;
            return o.TryGetValue(k, out v) ? double.Parse(v.Replace(',', '.'), CultureInfo.InvariantCulture) : def;
        }

        private static int GetInt(Dictionary<string, string> o, string k, int def)
        {
            string v;
            return o.TryGetValue(k, out v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
        }
    }
}
