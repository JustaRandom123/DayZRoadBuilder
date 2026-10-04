using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DayZRoadBuilder.Core;
using DayZRoadBuilder.Core.Tv4p;

namespace DayZRoadBuilder.Cli
{
    /// <summary>
    /// Command line version, e.g. for batch processing:
    /// DayZRoadBuilder.Cli --parts "C:\...\RoadParts" --shp road.shp --family asf1 --out road.txt [options]
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
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("DayZ Road Builder (CLI)");
            Console.WriteLine("  --parts <folder>      folder containing the road P3Ds (searched recursively)");
            Console.WriteLine("  --shp <file.shp>      polyline shapefile");
            Console.WriteLine("  --family <type>       road type, e.g. asf1, city, mud, asf1enoch ...");
            Console.WriteLine("  --out <file.txt>      Terrain Builder object list to write");
            Console.WriteLine("  --exclude <a,b,...>   exclude parts (by name)");
            Console.WriteLine("  --endcap <name>       end piece placed at start and end");
            Console.WriteLine("  --flip-endcaps        place end pieces reversed");
            Console.WriteLine("  --reverse             reverse the line direction");
            Console.WriteLine("  --beam <n>            search width (default 12)");
            Console.WriteLine("  --piece-penalty <x>   penalty per part (default 1.0)");
            Console.WriteLine("  --turn-penalty <x>    penalty per degree of curve (default 0.05)");
            Console.WriteLine("  --end-tol <m>         tolerance at the end of the line (default 3.5)");
            Console.WriteLine("  --model-origin        position = model origin instead of bounding box centre");
            Console.WriteLine("  --invert-yaw          invert the yaw sign");
            Console.WriteLine("  --yaw-offset <deg>    yaw offset");
            Console.WriteLine("  --offset-x <m> / --offset-y <m>   coordinate offset");
            Console.WriteLine("  --no-crossroads       do not place crossroad parts at junctions");
            Console.WriteLine("  --t-part <name>       T-junction part (default: automatic)");
            Console.WriteLine("  --x-part <name>       X-crossroad part (default: automatic)");
            Console.WriteLine("  --snap <m>            distance at which line ends are joined (default 1.5)");
            Console.WriteLine("  --same-type-side-roads  build side roads with the selected type instead of the crossroad's side road type");
            Console.WriteLine("  --tv4p <project.tv4p> --tv4p-out <new.tv4p>   also write the roads as Road Tool roads into a copy of the project");
            Console.WriteLine("  --tv4p-info <project.tv4p>   show the Road Tool road types / crossroads of a project");
            Console.WriteLine("  --list                only list the road types / parts found");
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

            string infoFile;
            if (opt.TryGetValue("--tv4p-info", out infoFile))
            {
                Tv4pProjectInfo pi = Tv4pRoadWriter.ReadInfo(infoFile);
                Console.WriteLine(pi.ExistingRoads + " Road Tool roads in the project.");
                Console.WriteLine(pi.Describe());
                return 0;
            }

            if (!opt.ContainsKey("--parts") || flags.Contains("--help"))
            {
                Usage();
                return 2;
            }

            RoadPartLibrary lib = RoadPartLibrary.Load(opt["--parts"]);
            foreach (string w in lib.Warnings) Console.WriteLine("Note: " + w);

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
            if (parts.Count == 0) throw new InvalidOperationException("No parts found for road type '" + family + "'.");

            RoadPart endCap = null;
            string capName;
            if (opt.TryGetValue("--endcap", out capName))
            {
                endCap = lib.Find(capName);
                if (endCap == null) throw new InvalidOperationException("End piece not found: " + capName);
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

            var ns = new NetworkSettings
            {
                UseCrossroads = !flags.Contains("--no-crossroads"),
                SnapDistance = GetD(opt, "--snap", 1.5),
                TJunctionPart = opt.ContainsKey("--t-part") ? opt["--t-part"] : null,
                XJunctionPart = opt.ContainsKey("--x-part") ? opt["--x-part"] : null,
                SideRoadsUseCrossroadType = !flags.Contains("--same-type-side-roads"),
                ReverseFreeRoads = flags.Contains("--reverse")
            };

            List<ShapeRecord> shapes = ShapefileReader.Read(opt["--shp"]);
            List<List<Vec2>> lines = shapes.SelectMany(s => s.Parts).ToList();
            NetworkResult net = NetworkBuilder.Build(lines, lib, family, parts, endCap, bs, ns, null, CancellationToken.None);

            foreach (JunctionInfo j in net.Junctions)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Junction {0} ({1}, {2} lines) at {3}: {4}{5}",
                    j.Id, j.Type, j.Degree, j.Point,
                    j.Placed != null ? j.Placed.Part.Name + " placed" : "no crossroad",
                    string.IsNullOrEmpty(j.Note) ? "" : " – " + j.Note));
            }
            foreach (BuildResult r in net.Roads)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: line {1:F1} m -> {2} parts, road {3:F1} m, max. deviation {4:F2} m, RMS {5:F2} m, end gap {6:F2} m, max. joint gap {7:F4} m, {8:F0} ms",
                    r.Path.Name, r.Path.Length, r.Parts.Count, r.RoadLength, r.MaxDeviation, r.RmsDeviation, r.EndGap, r.MaxJointGap, r.Duration.TotalMilliseconds));
                foreach (string w in r.Warnings) Console.WriteLine("  Warning: " + w);
            }

            List<PlacedPart> all = net.AllParts.ToList();
            TerrainBuilderExporter.Write(opt["--out"], all, es);
            Console.WriteLine(all.Count + " objects written: " + Path.GetFullPath(opt["--out"]));

            string tvIn, tvOut;
            if (opt.TryGetValue("--tv4p", out tvIn))
            {
                if (!opt.TryGetValue("--tv4p-out", out tvOut))
                    throw new InvalidOperationException("--tv4p needs --tv4p-out <new file>.");
                Tv4pExportResult tr = Tv4pRoadWriter.Write(tvIn, tvOut, net, es);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} Road Tool roads ({1} parts) added to a copy of the project ({2} roads existed): {3}",
                    tr.RoadsWritten, tr.PartsWritten, tr.ExistingRoads, Path.GetFullPath(tvOut)));
                foreach (string w in tr.Warnings) Console.WriteLine("  Warning: " + w);
            }
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
