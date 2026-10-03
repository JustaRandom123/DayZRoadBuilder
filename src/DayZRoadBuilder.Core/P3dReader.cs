using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DayZRoadBuilder.Core
{
    public struct P3dPoint
    {
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>Ein LOD aus einer unbinarisierten (MLOD) P3D-Datei.</summary>
    public sealed class P3dLod
    {
        public float Resolution;
        public List<P3dPoint> Points = new List<P3dPoint>();

        /// <summary>Benannte Selektionen (z.B. LB, PB, LE, PE) -> Indizes der enthaltenen Punkte.</summary>
        public Dictionary<string, List<int>> Selections = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Named Properties (#Property#), z.B. class=road.</summary>
        public Dictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsMemoryLod { get { return Math.Abs(Resolution - 1e15f) < 1e9f; } }
    }

    /// <summary>
    /// Minimaler Leser für das MLOD-Format (P3DM, Version 0x1C), wie es Object Builder / DayZ Tools auf P:\ ablegen.
    /// Gelesen werden nur Punkte, Selektionen und Properties – genug, um die Straßen-Memorypunkte zu bekommen.
    /// Binarisierte Modelle (ODOL) werden nicht unterstützt.
    /// </summary>
    public static class P3dReader
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static List<P3dLod> ReadMlod(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            return ReadMlod(data, path);
        }

        public static List<P3dLod> ReadMlod(byte[] data, string nameForErrors)
        {
            var r = new ByteReader(data);
            string sig = r.ReadFixedString(4);
            if (sig == "ODOL")
                throw new NotSupportedException("Binarisiertes Modell (ODOL) – bitte die unbinarisierte MLOD-Version von P:\\ verwenden: " + nameForErrors);
            if (sig != "MLOD")
                throw new InvalidDataException("Keine P3D-Datei (Signatur '" + sig + "'): " + nameForErrors);

            r.ReadInt32(); // Version
            int lodCount = r.ReadInt32();
            if (lodCount < 0 || lodCount > 10000)
                throw new InvalidDataException("Ungültige LOD-Anzahl in " + nameForErrors);

            var lods = new List<P3dLod>(lodCount);
            for (int l = 0; l < lodCount; l++)
            {
                string lodSig = r.ReadFixedString(4);
                if (lodSig != "P3DM")
                    throw new InvalidDataException("Nicht unterstütztes LOD-Format '" + lodSig + "' in " + nameForErrors);

                r.ReadInt32(); // header size (0x1C)
                r.ReadInt32(); // version (0x100)
                int nPoints = r.ReadInt32();
                int nNormals = r.ReadInt32();
                int nFaces = r.ReadInt32();
                r.ReadInt32(); // flags

                var lod = new P3dLod();
                for (int i = 0; i < nPoints; i++)
                {
                    var p = new P3dPoint();
                    p.X = r.ReadSingle();
                    p.Y = r.ReadSingle();
                    p.Z = r.ReadSingle();
                    r.ReadInt32(); // point flags
                    lod.Points.Add(p);
                }

                r.Skip(nNormals * 12);

                for (int f = 0; f < nFaces; f++)
                {
                    r.Skip(4 + 4 * 16 + 4); // vertex count, 4 x (point, normal, u, v), face flags
                    r.ReadZString();         // texture
                    r.ReadZString();         // material
                }

                string tagg = r.ReadFixedString(4);
                if (tagg != "TAGG")
                    throw new InvalidDataException("TAGG-Block fehlt in " + nameForErrors);

                while (true)
                {
                    r.ReadByte(); // active flag
                    string name = r.ReadZString();
                    int len = r.ReadInt32();
                    if (name == "#EndOfFile#")
                    {
                        r.Skip(len);
                        break;
                    }

                    int start = r.Position;
                    if (name == "#Property#" && len >= 128)
                    {
                        string key = ReadCString(data, start, 64);
                        string val = ReadCString(data, start + 64, 64);
                        lod.Properties[key] = val;
                    }
                    else if (!name.StartsWith("#", StringComparison.Ordinal))
                    {
                        // Selektion: ein Gewichts-Byte pro Punkt (danach eines pro Fläche). 0 = nicht selektiert.
                        var idx = new List<int>();
                        int n = Math.Min(nPoints, len);
                        for (int i = 0; i < n; i++)
                        {
                            if (data[start + i] != 0) idx.Add(i);
                        }
                        lod.Selections[name] = idx;
                    }
                    r.Skip(len);
                }

                lod.Resolution = r.ReadSingle();
                lods.Add(lod);
            }
            return lods;
        }

        private static string ReadCString(byte[] data, int offset, int maxLen)
        {
            int end = offset;
            int limit = Math.Min(data.Length, offset + maxLen);
            while (end < limit && data[end] != 0) end++;
            return Latin1.GetString(data, offset, end - offset);
        }

        private sealed class ByteReader
        {
            private readonly byte[] _d;
            private int _p;

            public ByteReader(byte[] d) { _d = d; _p = 0; }

            public int Position { get { return _p; } }

            private void Need(int n)
            {
                if (_p + n > _d.Length || n < 0)
                    throw new EndOfStreamException("Unerwartetes Dateiende in P3D-Datei.");
            }

            public void Skip(int n) { Need(n); _p += n; }

            public byte ReadByte() { Need(1); return _d[_p++]; }

            public int ReadInt32()
            {
                Need(4);
                int v = BitConverter.ToInt32(_d, _p);
                _p += 4;
                return v;
            }

            public float ReadSingle()
            {
                Need(4);
                float v = BitConverter.ToSingle(_d, _p);
                _p += 4;
                return v;
            }

            public string ReadFixedString(int n)
            {
                Need(n);
                string s = Latin1.GetString(_d, _p, n);
                _p += n;
                return s;
            }

            public string ReadZString()
            {
                int start = _p;
                while (true)
                {
                    Need(1);
                    if (_d[_p] == 0) break;
                    _p++;
                }
                string s = Latin1.GetString(_d, start, _p - start);
                _p++; // Nullterminator
                return s;
            }
        }
    }
}
