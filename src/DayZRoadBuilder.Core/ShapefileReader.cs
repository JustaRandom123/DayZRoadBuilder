using System;
using System.Collections.Generic;
using System.IO;

namespace DayZRoadBuilder.Core
{
    public sealed class ShapeRecord
    {
        public int RecordNumber;
        public int ShapeType;
        /// <summary>Individual line strings (parts) of the record.</summary>
        public List<List<Vec2>> Parts = new List<List<Vec2>>();
    }

    /// <summary>
    /// Reads lines (PolyLine, PolyLineZ, PolyLineM) and polygons from an ESRI shapefile (.shp).
    /// Coordinates are taken as they are (Terrain Builder exports already include the 200000 easting offset).
    /// </summary>
    public static class ShapefileReader
    {
        public static List<ShapeRecord> Read(string shpPath)
        {
            byte[] b = File.ReadAllBytes(shpPath);
            if (b.Length < 100 || ReadInt32BE(b, 0) != 9994)
                throw new InvalidDataException("Not a valid shapefile: " + shpPath);

            var result = new List<ShapeRecord>();
            int o = 100;
            while (o + 8 <= b.Length)
            {
                int recNo = ReadInt32BE(b, o);
                int contentBytes = ReadInt32BE(b, o + 4) * 2;
                int c = o + 8;
                if (contentBytes < 4 || c + contentBytes > b.Length) break;

                int type = BitConverter.ToInt32(b, c);
                bool isLine = type == 3 || type == 13 || type == 23;
                bool isPoly = type == 5 || type == 15 || type == 25;
                if (isLine || isPoly)
                {
                    int numParts = BitConverter.ToInt32(b, c + 36);
                    int numPoints = BitConverter.ToInt32(b, c + 40);
                    int partsOff = c + 44;
                    int ptsOff = partsOff + 4 * numParts;
                    if (numParts > 0 && numPoints > 0 && ptsOff + 16 * numPoints <= c + contentBytes)
                    {
                        var rec = new ShapeRecord { RecordNumber = recNo, ShapeType = type };
                        for (int p = 0; p < numParts; p++)
                        {
                            int first = BitConverter.ToInt32(b, partsOff + 4 * p);
                            int last = p + 1 < numParts ? BitConverter.ToInt32(b, partsOff + 4 * (p + 1)) : numPoints;
                            var pts = new List<Vec2>();
                            for (int i = first; i < last; i++)
                            {
                                double x = BitConverter.ToDouble(b, ptsOff + 16 * i);
                                double y = BitConverter.ToDouble(b, ptsOff + 16 * i + 8);
                                pts.Add(new Vec2(x, y));
                            }
                            if (pts.Count >= 2) rec.Parts.Add(pts);
                        }
                        if (rec.Parts.Count > 0) result.Add(rec);
                    }
                }
                o = c + contentBytes;
            }
            return result;
        }

        private static int ReadInt32BE(byte[] b, int o)
        {
            return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        }
    }
}
