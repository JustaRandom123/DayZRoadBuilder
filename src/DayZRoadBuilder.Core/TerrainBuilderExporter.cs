using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DayZRoadBuilder.Core
{
    public sealed class ExportSettings
    {
        /// <summary>Yaw-Vorzeichen umkehren (falls die Teile im Terrain Builder gespiegelt/verdreht erscheinen).</summary>
        public bool InvertYaw { get; set; }
        /// <summary>Wird zu jedem Yaw addiert.</summary>
        public double YawOffset { get; set; }
        /// <summary>Wird zu jeder X-Koordinate addiert (z.B. 200000, falls die Linie ohne TB-Offset vorliegt).</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        /// <summary>Relative Höhe über Terrain.</summary>
        public double RelativeZ { get; set; }
    }

    /// <summary>
    /// Schreibt die Objektliste im Import-Format des Terrain Builders
    /// (File &gt; Import &gt; Objects... bzw. Rechtsklick auf Layer &gt; Import objects):
    /// "name";X;Y;Yaw;Pitch;Roll;Scale;Z;
    /// </summary>
    public static class TerrainBuilderExporter
    {
        public static double ExportYaw(PlacedPart p, ExportSettings e)
        {
            double yaw = e.InvertYaw ? -p.Yaw : p.Yaw;
            return Geo.Norm360(yaw + e.YawOffset);
        }

        public static string FormatLine(PlacedPart p, ExportSettings e)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            return string.Format(ci, "\"{0}\";{1:F3};{2:F3};{3:F4};{4:F4};{5:F4};{6:F4};{7:F3};",
                p.Part.Name,
                p.Position.X + e.OffsetX,
                p.Position.Z + e.OffsetY,
                ExportYaw(p, e),
                0.0, 0.0, 1.0,
                e.RelativeZ);
        }

        public static void Write(string file, IEnumerable<PlacedPart> parts, ExportSettings e)
        {
            var sb = new StringBuilder();
            foreach (PlacedPart p in parts)
                sb.Append(FormatLine(p, e)).Append("\r\n");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
