using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DayZRoadBuilder.Core.Tv4p
{
    /// <summary>One field of an ALB1 file (tag + type + value). Objects, lists and pairs have children.</summary>
    public sealed class AlbNode
    {
        public ushort Tag;
        public string Name;
        public byte Type;
        /// <summary>Offset of the tag.</summary>
        public int Start;
        /// <summary>Offset of the value (directly after tag and type byte).</summary>
        public int ValueOffset;
        /// <summary>Offset after the value.</summary>
        public int End;
        public AlbNode Parent;
        public List<AlbNode> Children;
        /// <summary>Objects only: class id and object id.</summary>
        public ushort ClassId;
        public string ClassName;
        public uint ObjectId;

        public AlbNode Child(string name)
        {
            if (Children == null) return null;
            foreach (AlbNode c in Children)
                if (c.Name == name) return c;
            return null;
        }

        public override string ToString()
        {
            return Name + " (type 0x" + Type.ToString("X2") + (ClassName != null ? ", " + ClassName : "") + ")";
        }
    }

    /// <summary>
    /// Minimal reader for the "ALB1" binary format used by Terrain Builder project files (.tv4p).
    /// The format is not documented by Bohemia Interactive; this reader was written from analysing real project files.
    /// Layout: "ALB1", header, a tag name table, a class name table, then one root object whose fields run to the end
    /// of the file. Every field is: u16 tag, u8 type, value.
    /// </summary>
    public sealed class AlbDocument
    {
        public static readonly Encoding TextEncoding = Encoding.GetEncoding(28591); // Latin-1, byte-transparent

        public byte[] Data { get; private set; }
        public Dictionary<ushort, string> TagNames { get; private set; }
        public Dictionary<string, ushort> TagIds { get; private set; }
        public Dictionary<ushort, string> ClassNames { get; private set; }
        public Dictionary<string, ushort> ClassIds { get; private set; }
        /// <summary>Root object (class + id + fields to end of file).</summary>
        public AlbNode Root { get; private set; }
        public uint MaxObjectId { get; private set; }

        public static AlbDocument Load(string path)
        {
            return Parse(File.ReadAllBytes(path));
        }

        public static AlbDocument Parse(byte[] data)
        {
            var d = new AlbDocument();
            d.Data = data;
            d.TagNames = new Dictionary<ushort, string>();
            d.TagIds = new Dictionary<string, ushort>(StringComparer.Ordinal);
            d.ClassNames = new Dictionary<ushort, string>();
            d.ClassIds = new Dictionary<string, ushort>(StringComparer.Ordinal);

            if (data.Length < 32 || data[0] != 'A' || data[1] != 'L' || data[2] != 'B' || data[3] != '1')
                throw new InvalidDataException("Not a Terrain Builder project (ALB1 header missing).");

            int o = 16;
            // name tables: field with type 0x0F at top level, u32 count, entries (u16 id, u16 length, text)
            int tableNo = 0;
            while (o + 7 <= data.Length && data[o + 2] == 0x0F)
            {
                uint count = BitConverter.ToUInt32(data, o + 3);
                o += 7;
                for (uint i = 0; i < count; i++)
                {
                    ushort id = BitConverter.ToUInt16(data, o);
                    int len = BitConverter.ToUInt16(data, o + 2);
                    string s = TextEncoding.GetString(data, o + 4, len);
                    o += 4 + len;
                    if (tableNo == 0)
                    {
                        d.TagNames[id] = s;
                        if (!d.TagIds.ContainsKey(s)) d.TagIds[s] = id;
                    }
                    else
                    {
                        d.ClassNames[id] = s;
                        if (!d.ClassIds.ContainsKey(s)) d.ClassIds[s] = id;
                    }
                }
                tableNo++;
            }
            if (tableNo < 2)
                throw new InvalidDataException("Unexpected project file layout (name tables not found).");

            var root = new AlbNode { Name = "<root>", Type = 0x0D, Start = o, ValueOffset = o, End = data.Length, Children = new List<AlbNode>() };
            root.ClassId = BitConverter.ToUInt16(data, o);
            root.ObjectId = BitConverter.ToUInt32(data, o + 2);
            string cn;
            root.ClassName = d.ClassNames.TryGetValue(root.ClassId, out cn) ? cn : null;
            uint maxId = root.ObjectId;
            d.ParseFields(root, o + 6, data.Length, -1, ref maxId);
            d.Root = root;
            d.MaxObjectId = maxId;
            return d;
        }

        private int ParseFields(AlbNode parent, int o, int end, int maxCount, ref uint maxId)
        {
            byte[] b = Data;
            int n = 0;
            while (maxCount < 0 ? o < end : n < maxCount)
            {
                if (o + 3 > b.Length) throw new InvalidDataException("Unexpected end of project file.");
                var f = new AlbNode { Parent = parent, Start = o, Tag = BitConverter.ToUInt16(b, o), Type = b[o + 2] };
                string nm;
                f.Name = TagNames.TryGetValue(f.Tag, out nm) ? nm : "#" + f.Tag;
                o += 3;
                f.ValueOffset = o;
                switch (f.Type)
                {
                    case 0x01: case 0x09: o += 1; break;
                    case 0x20: o += 3; break;
                    case 0x05: case 0x06: case 0x07: case 0x08: case 0x0A: o += 4; break;
                    case 0x0E: o += 6; break;
                    case 0x13: break;
                    case 0x14: o += 8; break;
                    case 0x0B: o += 2 + BitConverter.ToUInt16(b, o); break;
                    case 0x12: o += 1 + 4 * b[o]; break;
                    case 0x15: o += 1 + 8 * b[o]; break;
                    case 0x0C:
                        {
                            int len = checked((int)BitConverter.ToUInt32(b, o));
                            int count = checked((int)BitConverter.ToUInt32(b, o + 4));
                            int lend = o + 4 + len;
                            f.Children = new List<AlbNode>();
                            int got = ParseFields(f, o + 8, lend, -1, ref maxId);
                            if (got != count) throw new InvalidDataException("List size mismatch at offset " + f.Start);
                            o = lend;
                            break;
                        }
                    case 0x0D:
                        {
                            int len = checked((int)BitConverter.ToUInt32(b, o));
                            int oend = o + 4 + len;
                            f.ClassId = BitConverter.ToUInt16(b, o + 4);
                            f.ObjectId = BitConverter.ToUInt32(b, o + 6);
                            string cn;
                            f.ClassName = ClassNames.TryGetValue(f.ClassId, out cn) ? cn : null;
                            if (f.ObjectId > maxId) maxId = f.ObjectId;
                            f.Children = new List<AlbNode>();
                            ParseFields(f, o + 10, oend, -1, ref maxId);
                            o = oend;
                            break;
                        }
                    case 0x0F:
                        {
                            // key/value pair: two fields
                            f.Children = new List<AlbNode>();
                            o = ParseFieldsReturnEnd(f, o, 2, ref maxId);
                            break;
                        }
                    default:
                        throw new InvalidDataException(string.Format("Unknown field type 0x{0:X2} ('{1}') at offset {2} – this project file version is not supported.", f.Type, f.Name, f.Start));
                }
                if (o > b.Length) throw new InvalidDataException("Field exceeds the project file.");
                f.End = o;
                if (parent.Children != null) parent.Children.Add(f);
                n++;
            }
            if (maxCount < 0 && o != end) throw new InvalidDataException("Field size mismatch in project file.");
            return n;
        }

        private int ParseFieldsReturnEnd(AlbNode parent, int o, int count, ref uint maxId)
        {
            ParseFields(parent, o, int.MaxValue, count, ref maxId);
            return parent.Children[parent.Children.Count - 1].End;
        }

        // ------------------------------------------------------------------ value helpers

        public string GetString(AlbNode n)
        {
            if (n == null || n.Type != 0x0B) return null;
            int len = BitConverter.ToUInt16(Data, n.ValueOffset);
            return TextEncoding.GetString(Data, n.ValueOffset + 2, len);
        }

        public int GetInt(AlbNode n, int def)
        {
            if (n == null || n.Type != 0x05) return def;
            return BitConverter.ToInt32(Data, n.ValueOffset);
        }

        public double GetDouble(AlbNode n, double def)
        {
            if (n == null || n.Type != 0x14) return def;
            return BitConverter.ToDouble(Data, n.ValueOffset);
        }

        /// <summary>Depth-first search for the first field with the given name.</summary>
        public AlbNode Find(string name)
        {
            return Find(Root, name);
        }

        public static AlbNode Find(AlbNode start, string name)
        {
            if (start.Children == null) return null;
            foreach (AlbNode c in start.Children)
            {
                if (c.Name == name) return c;
                AlbNode r = Find(c, name);
                if (r != null) return r;
            }
            return null;
        }

        public ushort Tag(string name)
        {
            ushort id;
            if (!TagIds.TryGetValue(name, out id))
                throw new InvalidDataException("The project file has no field '" + name + "'. Use the Road Tool once in this project and save it.");
            return id;
        }

        public ushort Class(string name)
        {
            ushort id;
            if (!ClassIds.TryGetValue(name, out id))
                throw new InvalidDataException("The project file has no class '" + name + "'. Use the Road Tool once in this project and save it.");
            return id;
        }
    }
}
