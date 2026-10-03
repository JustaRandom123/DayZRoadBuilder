using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DayZRoadBuilder.Core
{
    /// <summary>All road parts of a folder (recursive), grouped by road type ("family", e.g. asf1, city, mud).</summary>
    public sealed class RoadPartLibrary
    {
        public List<RoadPart> Parts { get; private set; }
        public List<string> Warnings { get; private set; }

        private RoadPartLibrary()
        {
            Parts = new List<RoadPart>();
            Warnings = new List<string>();
        }

        public static RoadPartLibrary Load(string folder)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("Folder not found: " + folder);

            var lib = new RoadPartLibrary();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int odol = 0;

            foreach (string file in Directory.EnumerateFiles(folder, "*.p3d", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    RoadPart part = RoadPart.FromFile(file);
                    if (part == null)
                    {
                        lib.Warnings.Add("No LB/PB/LE/PE memory points: " + Path.GetFileName(file));
                        continue;
                    }
                    if (!names.Add(part.Name))
                    {
                        lib.Warnings.Add("Duplicate part name ignored: " + file);
                        continue;
                    }
                    lib.Parts.Add(part);
                }
                catch (NotSupportedException)
                {
                    odol++;
                }
                catch (Exception ex)
                {
                    lib.Warnings.Add("Error reading " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            if (odol > 0)
                lib.Warnings.Add(odol + " binarized (ODOL) P3D file(s) skipped – only unbinarized MLOD models (P:\\) can be read.");

            return lib;
        }

        /// <summary>Road types that can be built automatically (at least one straight part available).</summary>
        public List<string> GetFamilies()
        {
            return Parts
                .Where(p => p.Kind == PartKind.Straight)
                .Select(p => p.Family)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<RoadPart> GetFamily(string family)
        {
            return Parts
                .Where(p => string.Equals(p.Family, family, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => (int)p.Kind)
                .ThenByDescending(p => p.IsStraight ? p.Length : 0.0)
                .ThenBy(p => Math.Round(Math.Abs(p.TurnAngle), 1))
                .ThenBy(p => p.Radius)
                .ToList();
        }

        public RoadPart Find(string name)
        {
            return Parts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
