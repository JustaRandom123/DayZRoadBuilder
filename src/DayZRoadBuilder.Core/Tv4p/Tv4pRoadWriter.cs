using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DayZRoadBuilder.Core.Tv4p
{
    /// <summary>A part of a Road Tool road type definition in the project.</summary>
    public sealed class Tv4pPartRef
    {
        public int DefIndex;
        public string DefName;
        /// <summary>"straight", "curve", "special" or "terminator".</summary>
        public string Kind;
        public int Index;
        public string Path;
    }

    /// <summary>A crossroad definition of the Road Tool in the project.</summary>
    public sealed class Tv4pCrossDef
    {
        public int Index;
        public string Name;
        public string Path;
        /// <summary>Road type index for the connections a (end), b (start), c (left), d (right); -1 = none.</summary>
        public int[] Connect = { -1, -1, -1, -1 };
    }

    /// <summary>What the project's Road Tool knows: road types and crossroads.</summary>
    public sealed class Tv4pProjectInfo
    {
        public List<string> RoadTypes = new List<string>();
        public Dictionary<string, Tv4pPartRef> Parts = new Dictionary<string, Tv4pPartRef>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Tv4pCrossDef> Crossroads = new Dictionary<string, Tv4pCrossDef>(StringComparer.OrdinalIgnoreCase);
        public int ExistingRoads;

        public string Describe()
        {
            var lines = new List<string>();
            for (int i = 0; i < RoadTypes.Count; i++)
            {
                int n = Parts.Values.Count(p => p.DefIndex == i);
                lines.Add(string.Format(CultureInfo.InvariantCulture, "  Road type {0}: \"{1}\" ({2} parts)", i, RoadTypes[i], n));
            }
            foreach (Tv4pCrossDef c in Crossroads.Values)
                lines.Add("  Crossroad: " + c.Name);
            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class Tv4pExportResult
    {
        public string OutputPath;
        public int RoadsWritten;
        public int PartsWritten;
        public int ExistingRoads;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Writes the built roads as real Road Tool roads into a copy of a Terrain Builder project (.tv4p).
    ///
    /// A Road Tool road (SKeyRoadElement) is one key part with position (model origin) and orientation (degrees, clockwise),
    /// plus four chains of parts: directiona continues from the key's end edge (LE/PE), directionb from its start edge
    /// (LB/PB), directionc from the left side arm (LD/LH) and directiond from the right side arm (PD/PH) of a crossroad.
    /// Chain elements (SRoadElement) store type (3 straight, 8 curve, 7 curve entered from its end = opposite turn,
    /// 6 terminator, 2 = crossroad as key), the index of the part in the road type definition and the model path.
    /// The format is undocumented; it was derived from real project files.
    /// </summary>
    public static class Tv4pRoadWriter
    {
        private const int TypeCrossroad = 2;
        private const int TypeStraight = 3;
        private const int TypeTerminator = 6;
        private const int TypeCurveReversed = 7;
        private const int TypeCurve = 8;

        // ------------------------------------------------------------------ reading the definitions

        public static Tv4pProjectInfo ReadInfo(string tv4pPath)
        {
            AlbDocument doc = AlbDocument.Load(tv4pPath);
            return ReadInfo(doc);
        }

        public static Tv4pProjectInfo ReadInfo(AlbDocument doc)
        {
            var info = new Tv4pProjectInfo();
            AlbNode roadDefs = doc.Find("mroad");
            if (roadDefs == null || roadDefs.Type != 0x0C)
                throw new InvalidDataException("No Road Tool road types found in the project. Define your road types in Terrain Builder's Road Tool first.");

            int defIndex = 0;
            foreach (AlbNode def in roadDefs.Children)
            {
                string defName = doc.GetString(def.Child("name")) ?? ("type " + defIndex);
                info.RoadTypes.Add(defName);
                foreach (string kind in new[] { "straight", "curve", "special", "terminator" })
                {
                    AlbNode list = def.Child(kind);
                    if (list == null || list.Children == null) continue;
                    for (int i = 0; i < list.Children.Count; i++)
                    {
                        AlbNode item = list.Children[i];
                        string path = doc.GetString(item.Child("objectfilefilename")) ?? "";
                        string name = doc.GetString(item.Child("name"));
                        var pr = new Tv4pPartRef { DefIndex = defIndex, DefName = defName, Kind = kind, Index = i, Path = path };
                        foreach (string key in new[] { name, FileStem(path) })
                            if (!string.IsNullOrEmpty(key) && !info.Parts.ContainsKey(key)) info.Parts[key] = pr;
                    }
                }
                defIndex++;
            }

            AlbNode crossDefs = doc.Find("mcross");
            if (crossDefs != null && crossDefs.Children != null)
            {
                int ci = 0;
                foreach (AlbNode cd in crossDefs.Children)
                {
                    var c = new Tv4pCrossDef
                    {
                        Index = ci++,
                        Name = doc.GetString(cd.Child("name")) ?? "",
                        Path = doc.GetString(cd.Child("objectfilefilename")) ?? ""
                    };
                    string[] ports = { "connecta", "connectb", "connectc", "connectd" };
                    for (int k = 0; k < 4; k++) c.Connect[k] = doc.GetInt(cd.Child(ports[k]), -1);
                    foreach (string key in new[] { c.Name, FileStem(c.Path) })
                        if (!string.IsNullOrEmpty(key) && !info.Crossroads.ContainsKey(key)) info.Crossroads[key] = c;
                }
            }

            AlbNode roads = doc.Find("mroadGetRoads");
            info.ExistingRoads = roads != null && roads.Children != null ? roads.Children.Count : 0;
            return info;
        }

        private static string FileStem(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace('/', '\\');
            int i = p.LastIndexOf('\\');
            string f = i >= 0 ? p.Substring(i + 1) : p;
            return f.EndsWith(".p3d", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;
        }

        // ------------------------------------------------------------------ mapping the network to Road Tool roads

        private sealed class TbElement
        {
            public PlacedPart Part;
            /// <summary>The chain enters this part through its end edge (LE/PE).</summary>
            public bool EnterAtEnd;
        }

        private sealed class TbRoad
        {
            public PlacedPart Key;
            public List<TbElement> A = new List<TbElement>();
            public List<TbElement> B = new List<TbElement>();
            public List<TbElement> C = new List<TbElement>();
            public List<TbElement> D = new List<TbElement>();
            public int PartCount { get { return 1 + A.Count + B.Count + C.Count + D.Count; } }
        }

        private static List<TbRoad> MapNetwork(NetworkResult net, List<string> warnings)
        {
            var roads = new List<TbRoad>();
            var junctionRoads = new Dictionary<int, TbRoad>();

            // every placed crossroad becomes the key part of its own Road Tool road
            foreach (JunctionInfo j in net.Junctions)
            {
                if (j.Placed == null) continue;
                var t = new TbRoad { Key = j.Placed };
                junctionRoads[j.Id] = t;
                roads.Add(t);
            }

            foreach (BuildResult r in net.Roads)
            {
                List<PlacedPart> parts = r.Parts;
                if (parts.Count == 0) continue;
                var cross = new List<int>();
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i].JunctionId >= 0 && junctionRoads.ContainsKey(parts[i].JunctionId)) cross.Add(i);

                int firstCross = cross.Count > 0 ? cross[0] : parts.Count;
                List<PlacedPart> s0 = parts.GetRange(0, firstCross);
                bool done = s0.Count == 0;

                // side road: hang it on the arm of its crossroad
                TbRoad armRoad;
                if (!done && r.StartJunctionId >= 0 && junctionRoads.TryGetValue(r.StartJunctionId, out armRoad))
                {
                    List<TbElement> arm = r.StartArmLeft ? armRoad.C : armRoad.D;
                    if (arm.Count == 0)
                    {
                        arm.AddRange(Outward(s0));
                        done = true;
                    }
                }
                if (!done)
                {
                    if (cross.Count > 0) AttachBefore(junctionRoads[parts[cross[0]].JunctionId], s0, roads, warnings);
                    else roads.Add(Standalone(s0, warnings));
                }

                for (int k = 0; k < cross.Count; k++)
                {
                    int from = cross[k] + 1;
                    int to = k + 1 < cross.Count ? cross[k + 1] : parts.Count;
                    if (to > from)
                        AttachAfter(junctionRoads[parts[cross[k]].JunctionId], parts.GetRange(from, to - from), roads, warnings);
                }
            }
            return roads;
        }

        /// <summary>Elements for parts listed in driving order, going away from the attachment point.</summary>
        private static IEnumerable<TbElement> Outward(List<PlacedPart> seg)
        {
            foreach (PlacedPart p in seg)
                yield return new TbElement { Part = p, EnterAtEnd = p.Reversed };
        }

        /// <summary>Elements for parts that lie before the key (driving order), listed going away from the key.</summary>
        private static IEnumerable<TbElement> Backward(List<PlacedPart> seg)
        {
            for (int i = seg.Count - 1; i >= 0; i--)
                yield return new TbElement { Part = seg[i], EnterAtEnd = !seg[i].Reversed };
        }

        private static void AttachAfter(TbRoad t, List<PlacedPart> seg, List<TbRoad> roads, List<string> warnings)
        {
            // the road leaves the key through the edge it exits in driving direction
            List<TbElement> list = t.Key.Reversed ? t.B : t.A;
            if (list.Count == 0) list.AddRange(Outward(seg));
            else roads.Add(Standalone(seg, warnings));
        }

        private static void AttachBefore(TbRoad t, List<PlacedPart> seg, List<TbRoad> roads, List<string> warnings)
        {
            List<TbElement> list = t.Key.Reversed ? t.A : t.B;
            if (list.Count == 0) list.AddRange(Backward(seg));
            else roads.Add(Standalone(seg, warnings));
        }

        private static TbRoad Standalone(List<PlacedPart> seg, List<string> warnings)
        {
            // key = first straight part (or the first part)
            int k = seg.FindIndex(p => p.Part.Kind == PartKind.Straight || p.Part.Kind == PartKind.Crosswalk);
            if (k < 0) k = 0;
            var t = new TbRoad { Key = seg[k] };
            List<PlacedPart> before = seg.GetRange(0, k);
            List<PlacedPart> after = seg.GetRange(k + 1, seg.Count - k - 1);
            if (after.Count > 0) (t.Key.Reversed ? t.B : t.A).AddRange(Outward(after));
            if (before.Count > 0) (t.Key.Reversed ? t.A : t.B).AddRange(Backward(before));
            return t;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Copies <paramref name="inputTv4p"/> to <paramref name="outputTv4p"/> and adds the roads of the network as Road Tool roads.
        /// The input file is never modified.
        /// </summary>
        public static Tv4pExportResult Write(string inputTv4p, string outputTv4p, NetworkResult net, ExportSettings es)
        {
            if (string.Equals(Path.GetFullPath(inputTv4p), Path.GetFullPath(outputTv4p), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Please write to a new file – the original project must not be overwritten.");
            if (es == null) es = new ExportSettings();

            AlbDocument doc = AlbDocument.Load(inputTv4p);
            Tv4pProjectInfo info = ReadInfo(doc);
            var result = new Tv4pExportResult { OutputPath = outputTv4p, ExistingRoads = info.ExistingRoads };

            List<TbRoad> roads = MapNetwork(net, result.Warnings);

            // check that every part is known to the project's Road Tool
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TbRoad t in roads)
            {
                foreach (PlacedPart p in AllParts(t))
                {
                    if (p.Part.Kind == PartKind.Crossroad)
                    {
                        if (!info.Crossroads.ContainsKey(p.Part.Name)) missing.Add(p.Part.Name + " (crossroad)");
                    }
                    else
                    {
                        Tv4pPartRef pr;
                        if (!info.Parts.TryGetValue(p.Part.Name, out pr)) missing.Add(p.Part.Name);
                        else if (pr.Kind == "special") missing.Add(p.Part.Name + " (defined as 'special' part – not supported)");
                    }
                }
            }
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "These parts are not defined in the project's Road Tool, so Terrain Builder could not use them:" + Environment.NewLine +
                    "  " + string.Join(", ", missing) + Environment.NewLine +
                    "Add them to a road type / crossroad in Terrain Builder's Road Tool, or untick them and build again.");

            ushort tItem = doc.Tag("item");
            ushort cKey = doc.Class("SKeyRoadElement");
            ushort cElem = doc.Class("SRoadElement");
            var tags = new RoadTags(doc);

            AlbNode list = doc.Find("mroadGetRoads");
            if (list == null || list.Type != 0x0C)
                throw new InvalidDataException("The project contains no Road Tool road list. Draw one road with the Road Tool, save, and try again.");

            ulong nextId = (ulong)doc.MaxObjectId + 128UL;
            var ms = new MemoryStream();
            foreach (TbRoad t in roads)
            {
                byte[] item = KeyItem(t, info, tags, tItem, cKey, cElem, es, ref nextId);
                ms.Write(item, 0, item.Length);
                result.RoadsWritten++;
                result.PartsWritten += t.PartCount;
            }
            byte[] insert = ms.ToArray();

            // splice into the road list and fix the sizes of the list and of all enclosing objects/lists
            byte[] data = doc.Data;
            var output = new byte[data.Length + insert.Length];
            Buffer.BlockCopy(data, 0, output, 0, list.End);
            Buffer.BlockCopy(insert, 0, output, list.End, insert.Length);
            Buffer.BlockCopy(data, list.End, output, list.End + insert.Length, data.Length - list.End);

            AddU32(output, list.ValueOffset, (uint)insert.Length);
            AddU32(output, list.ValueOffset + 4, (uint)roads.Count);
            for (AlbNode p = list.Parent; p != null && p != doc.Root; p = p.Parent)
            {
                if (p.Type == 0x0D || p.Type == 0x0C)
                    AddU32(output, p.ValueOffset, (uint)insert.Length);
            }

            // safety check: the new file must parse and contain all roads
            AlbDocument check = AlbDocument.Parse(output);
            AlbNode newList = check.Find("mroadGetRoads");
            if (newList == null || newList.Children.Count != info.ExistingRoads + roads.Count)
                throw new InvalidDataException("Internal check failed – the project file was not written.");

            File.WriteAllBytes(outputTv4p, output);
            return result;
        }

        private static IEnumerable<PlacedPart> AllParts(TbRoad t)
        {
            yield return t.Key;
            foreach (TbElement e in t.A.Concat(t.B).Concat(t.C).Concat(t.D)) yield return e.Part;
        }

        private static void AddU32(byte[] b, int offset, uint add)
        {
            uint v = BitConverter.ToUInt32(b, offset);
            checked { v += add; }
            byte[] nb = BitConverter.GetBytes(v);
            Buffer.BlockCopy(nb, 0, b, offset, 4);
        }

        private sealed class RoadTags
        {
            public ushort Orientation, Elevation, Position, RoadId, KeyType, KeyName, DirA, DirB, DirC, DirD, Type, Id, Name;
            public RoadTags(AlbDocument d)
            {
                Orientation = d.Tag("orientation");
                Elevation = d.Tag("elevation");
                Position = d.Tag("mposition");
                RoadId = d.Tag("roadid");
                KeyType = d.Tag("keypointtype");
                KeyName = d.Tag("keypointname");
                DirA = d.Tag("directiona");
                DirB = d.Tag("directionb");
                DirC = d.Tag("directionc");
                DirD = d.Tag("directiond");
                Type = d.Tag("type");
                Id = d.Tag("id");
                Name = d.Tag("name");
            }
        }

        private static uint NewId(ref ulong next)
        {
            if (next > uint.MaxValue) throw new InvalidDataException("No free object ids left in the project file.");
            uint id = (uint)next;
            next += 128;
            return id;
        }

        private static byte[] KeyItem(TbRoad t, Tv4pProjectInfo info, RoadTags tg, ushort tItem, ushort cKey, ushort cElem,
                                      ExportSettings es, ref ulong nextId)
        {
            uint keyId = NewId(ref nextId);
            PlacedPart k = t.Key;
            int keyType, roadId;
            string keyPath;
            if (k.Part.Kind == PartKind.Crossroad)
            {
                Tv4pCrossDef cd = info.Crossroads[k.Part.Name];
                keyType = TypeCrossroad;
                keyPath = cd.Path;
                roadId = cd.Connect[0] >= 0 ? cd.Connect[0] : Math.Max(0, cd.Connect[1]);
            }
            else
            {
                Tv4pPartRef pr = info.Parts[k.Part.Name];
                keyPath = pr.Path;
                roadId = pr.DefIndex;
                keyType = ElementType(pr, k.Reversed);
            }

            Vec2 origin = k.ToWorld(Vec2.Zero);
            var f = new MemoryStream();
            WriteDouble(f, tg.Orientation, Geo.WrapDeg(k.Yaw));
            WriteDouble(f, tg.Elevation, 0.0);
            WriteDoubleArray(f, tg.Position, new[] { origin.X + es.OffsetX, origin.Z + es.OffsetY });
            WriteInt(f, tg.RoadId, roadId);
            WriteInt(f, tg.KeyType, keyType);
            WriteString(f, tg.KeyName, keyPath);
            WriteChain(f, tg.DirA, t.A, info, tg, tItem, cElem, ref nextId);
            WriteChain(f, tg.DirB, t.B, info, tg, tItem, cElem, ref nextId);
            WriteChain(f, tg.DirC, t.C, info, tg, tItem, cElem, ref nextId);
            WriteChain(f, tg.DirD, t.D, info, tg, tItem, cElem, ref nextId);
            return ObjectItem(tItem, cKey, keyId, f.ToArray());
        }

        private static int ElementType(Tv4pPartRef pr, bool enterAtEnd)
        {
            switch (pr.Kind)
            {
                case "curve": return enterAtEnd ? TypeCurveReversed : TypeCurve;
                case "terminator": return TypeTerminator;
                default: return TypeStraight;
            }
        }

        private static void WriteChain(MemoryStream f, ushort tag, List<TbElement> chain, Tv4pProjectInfo info, RoadTags tg,
                                       ushort tItem, ushort cElem, ref ulong nextId)
        {
            var items = new MemoryStream();
            foreach (TbElement e in chain)
            {
                Tv4pPartRef pr = info.Parts[e.Part.Part.Name];
                var ef = new MemoryStream();
                WriteInt(ef, tg.Type, ElementType(pr, e.EnterAtEnd));
                WriteInt(ef, tg.Id, pr.Index);
                WriteString(ef, tg.Name, pr.Path);
                byte[] item = ObjectItem(tItem, cElem, NewId(ref nextId), ef.ToArray());
                items.Write(item, 0, item.Length);
            }
            byte[] body = items.ToArray();
            WriteU16(f, tag);
            f.WriteByte(0x0C);
            WriteU32(f, (uint)(4 + body.Length));
            WriteU32(f, (uint)chain.Count);
            f.Write(body, 0, body.Length);
        }

        private static byte[] ObjectItem(ushort itemTag, ushort classId, uint objectId, byte[] fields)
        {
            var m = new MemoryStream();
            WriteU16(m, itemTag);
            m.WriteByte(0x0D);
            WriteU32(m, (uint)(6 + fields.Length));
            WriteU16(m, classId);
            WriteU32(m, objectId);
            m.Write(fields, 0, fields.Length);
            return m.ToArray();
        }

        private static void WriteU16(MemoryStream m, ushort v) { m.Write(BitConverter.GetBytes(v), 0, 2); }
        private static void WriteU32(MemoryStream m, uint v) { m.Write(BitConverter.GetBytes(v), 0, 4); }

        private static void WriteInt(MemoryStream m, ushort tag, int v)
        {
            WriteU16(m, tag);
            m.WriteByte(0x05);
            m.Write(BitConverter.GetBytes(v), 0, 4);
        }

        private static void WriteDouble(MemoryStream m, ushort tag, double v)
        {
            WriteU16(m, tag);
            m.WriteByte(0x14);
            m.Write(BitConverter.GetBytes(v), 0, 8);
        }

        private static void WriteDoubleArray(MemoryStream m, ushort tag, double[] v)
        {
            WriteU16(m, tag);
            m.WriteByte(0x15);
            m.WriteByte((byte)v.Length);
            foreach (double d in v) m.Write(BitConverter.GetBytes(d), 0, 8);
        }

        private static void WriteString(MemoryStream m, ushort tag, string s)
        {
            byte[] b = AlbDocument.TextEncoding.GetBytes(s ?? "");
            WriteU16(m, tag);
            m.WriteByte(0x0B);
            WriteU16(m, (ushort)b.Length);
            m.Write(b, 0, b.Length);
        }
    }
}
