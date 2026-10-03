using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DayZRoadBuilder.Core
{
    public sealed class ExportSettings
    {
        /// <summary>Invert the yaw sign (if parts appear mirrored / rotated in Terrain Builder).</summary>
        public bool InvertYaw { get; set; }
        /// <summary>Added to every yaw.</summary>
        public double YawOffset { get; set; }
        /// <summary>Added to every X coordinate (e.g. 200000 if the line has no TB easting offset).</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        /// <summary>Height relative to the terrain.</summary>
        public double RelativeZ { get; set; }
    }

    /// <summary>
    /// Writes the object list in Terrain Builder's import format (File &gt; Import &gt; Objects...):
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
