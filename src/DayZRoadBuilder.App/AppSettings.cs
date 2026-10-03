using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DayZRoadBuilder.App
{
    /// <summary>Speichert die zuletzt benutzten Einstellungen in %AppData%\DayZRoadBuilder\settings.ini.</summary>
    internal sealed class AppSettings
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DayZRoadBuilder");
                return Path.Combine(dir, "settings.ini");
            }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) s._values[line.Substring(0, i)] = line.Substring(i + 1);
                    }
                }
            }
            catch
            {
                // Einstellungen sind optional
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _values)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // ignorieren
            }
        }

        public string GetString(string key, string def)
        {
            string v;
            return _values.TryGetValue(key, out v) ? v : def;
        }

        public void SetString(string key, string value)
        {
            _values[key] = (value ?? "").Replace("\r", "").Replace("\n", "");
        }

        public decimal GetDecimal(string key, decimal def)
        {
            decimal d;
            string v;
            if (_values.TryGetValue(key, out v) && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return def;
        }

        public void SetDecimal(string key, decimal value)
        {
            _values[key] = value.ToString(CultureInfo.InvariantCulture);
        }

        public bool GetBool(string key, bool def)
        {
            string v;
            if (_values.TryGetValue(key, out v)) return v == "1";
            return def;
        }

        public void SetBool(string key, bool value)
        {
            _values[key] = value ? "1" : "0";
        }

        /// <summary>Gespeicherter Haken-Zustand eines Teils (null = noch nie geändert).</summary>
        public bool? GetPartChecked(string partName)
        {
            string v;
            if (_values.TryGetValue("part." + partName, out v)) return v == "1";
            return null;
        }

        public void SetPartChecked(string partName, bool value)
        {
            _values["part." + partName] = value ? "1" : "0";
        }
    }
}
