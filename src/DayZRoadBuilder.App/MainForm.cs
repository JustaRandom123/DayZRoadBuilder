using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DayZRoadBuilder.Core;

namespace DayZRoadBuilder.App
{
    /// <summary>Hauptfenster. Die Oberfläche wird komplett im Code aufgebaut (kein Designer nötig).</summary>
    internal sealed class MainForm : Form
    {
        // Dateien
        private readonly TextBox _txtParts = new TextBox();
        private readonly TextBox _txtShp = new TextBox();
        private readonly Button _btnParts = new Button { Text = "…", Width = 32 };
        private readonly Button _btnShp = new Button { Text = "…", Width = 32 };

        // Straßentyp & Teile
        private readonly ComboBox _cboFamily = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckedListBox _lstParts = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        private readonly ComboBox _cboEndCap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _chkCapStart = new CheckBox { Text = "am Anfang", AutoSize = true, Checked = true };
        private readonly CheckBox _chkCapEnd = new CheckBox { Text = "am Ende", AutoSize = true, Checked = true };
        private readonly CheckBox _chkFlipCaps = new CheckBox { Text = "Endstücke umdrehen", AutoSize = true };

        // Bau-Optionen
        private readonly CheckBox _chkReverse = new CheckBox { Text = "Linienrichtung umkehren", AutoSize = true };
        private readonly NumericUpDown _numBeam = Num(1, 200, 12, 0, 1);
        private readonly NumericUpDown _numPiece = Num(0, 100, 1.0m, 2, 0.25m);
        private readonly NumericUpDown _numTurn = Num(0, 10, 0.05m, 3, 0.05m);
        private readonly NumericUpDown _numEndTol = Num(0.1m, 50, 3.5m, 2, 0.5m);
        private readonly NumericUpDown _numStartRange = Num(0, 45, 0, 1, 0.5m);

        // Export-Optionen
        private readonly ComboBox _cboRef = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _chkInvertYaw = new CheckBox { Text = "Yaw umkehren (gegen Uhrzeigersinn)", AutoSize = true };
        private readonly NumericUpDown _numYawOff = Num(-360, 360, 0, 3, 90);
        private readonly NumericUpDown _numOffX = Num(-10000000, 10000000, 0, 3, 1);
        private readonly NumericUpDown _numOffY = Num(-10000000, 10000000, 0, 3, 1);

        private readonly Button _btnBuild = new Button { Text = "Straße bauen", Height = 34, Dock = DockStyle.Fill };
        private readonly Button _btnExport = new Button { Text = "Export für Terrain Builder (.txt) …", Height = 30, Dock = DockStyle.Fill, Enabled = false };
        private readonly TextBox _txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };

        private readonly PreviewControl _preview = new PreviewControl { Dock = DockStyle.Fill };
        private readonly ToolStripStatusLabel _lblStatus = new ToolStripStatusLabel { Text = "Bereit", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripProgressBar _progress = new ToolStripProgressBar { Minimum = 0, Maximum = 1000, Width = 200 };

        private readonly AppSettings _settings = AppSettings.Load();
        private RoadPartLibrary _library;
        private List<ShapeRecord> _shapes;
        private List<BuildResult> _results;
        private CancellationTokenSource _cts;
        private bool _suppressItemCheck;
        private int _row;

        public MainForm()
        {
            Text = "DayZ Road Builder – Straßen automatisch aus Polylines";
            Width = 1400;
            Height = 900;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            BuildLayout();
            WireEvents();
            LoadSettingsToUi();
        }

        private static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal inc)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Increment = inc;
            n.Value = value;
            n.Width = 90;
            return n;
        }

        // ------------------------------------------------------------------ Layout

        private void BuildLayout()
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                Orientation = Orientation.Vertical
            };

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                Padding = new Padding(6)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddHeader(t, "1. Dateien");
            AddRow(t, "Teile-Ordner", _txtParts, _btnParts);
            AddRow(t, "Polyline (.shp)", _txtShp, _btnShp);

            AddHeader(t, "2. Straßentyp und Teile");
            AddRow(t, "Straßentyp", _cboFamily, null);
            AddFull(t, _lstParts, 230);
            AddRow(t, "Endstück", _cboEndCap, null);
            var capFlow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            capFlow.Controls.Add(_chkCapStart);
            capFlow.Controls.Add(_chkCapEnd);
            capFlow.Controls.Add(_chkFlipCaps);
            AddRow(t, "", capFlow, null);

            AddHeader(t, "3. Anpassung");
            AddRow(t, "Suchbreite", _numBeam, null);
            AddRow(t, "Strafe pro Teil", _numPiece, null);
            AddRow(t, "Strafe pro Grad", _numTurn, null);
            AddRow(t, "Endtoleranz [m]", _numEndTol, null);
            AddRow(t, "Startwinkel ± [°]", _numStartRange, null);
            AddRow(t, "", _chkReverse, null);

            AddHeader(t, "4. Export (Terrain Builder)");
            AddRow(t, "Position =", _cboRef, null);
            AddRow(t, "", _chkInvertYaw, null);
            AddRow(t, "Yaw-Versatz [°]", _numYawOff, null);
            AddRow(t, "Versatz X [m]", _numOffX, null);
            AddRow(t, "Versatz Y [m]", _numOffY, null);

            AddFull(t, _btnBuild, 40);
            AddFull(t, _btnExport, 36);
            AddHeader(t, "Protokoll");
            AddFull(t, _txtLog, 220);

            scroll.Controls.Add(t);
            split.Panel1.Controls.Add(scroll);
            split.Panel2.Controls.Add(_preview);

            var status = new StatusStrip();
            status.Items.Add(_lblStatus);
            status.Items.Add(_progress);

            Controls.Add(split);
            Controls.Add(status);

            _cboRef.Items.Add("Bounding-Box-Mitte (Standard)");
            _cboRef.Items.Add("Modell-Ursprung [0,0,0]");
            _cboRef.SelectedIndex = 0;

            // Panelbreite erst setzen, wenn das Fenster seine Größe hat
            Load += (s, e) =>
            {
                try { split.SplitterDistance = 430; } catch (InvalidOperationException) { }
            };

            var tip = new ToolTip { AutoPopDelay = 20000 };
            tip.SetToolTip(_numBeam, "Wie viele Varianten pro 0,5 m weiterverfolgt werden. Höher = genauer, aber langsamer. 8–30 ist sinnvoll.");
            tip.SetToolTip(_numPiece, "Kosten pro Teil. Höher = weniger, längere Teile (dafür etwas mehr Abweichung).");
            tip.SetToolTip(_numTurn, "Kosten pro Grad Kurve. Höher = mehr Geraden, weniger Schlängeln.");
            tip.SetToolTip(_numEndTol, "Wie weit das letzte Teil vom Linienende entfernt enden darf.");
            tip.SetToolTip(_numStartRange, "Erlaubt am Linienanfang eine etwas andere Startrichtung (± Grad), falls die Linie dort einen Knick hat.");
            tip.SetToolTip(_cboRef, "Welcher Modellpunkt als Objektposition exportiert wird. DayZ/TB benutzen bei Straßenteilen die Bounding-Box-Mitte (autocenter).");
            tip.SetToolTip(_chkInvertYaw, "Nur ändern, wenn die Teile nach dem Import gespiegelt/verdreht erscheinen.");
            tip.SetToolTip(_numOffX, "Wird auf alle X-Koordinaten addiert (z.B. 200000, falls die Linie ohne TB-Versatz exportiert wurde).");
        }

        private void AddHeader(TableLayoutPanel t, string text)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 12, 3, 4)
            };
            t.Controls.Add(l, 0, _row);
            t.SetColumnSpan(l, 3);
            _row++;
        }

        private void AddRow(TableLayoutPanel t, string label, Control c, Control extra)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            t.Controls.Add(l, 0, _row);
            if (c is TextBox || c is ComboBox) c.Dock = DockStyle.Fill;
            c.Margin = new Padding(3, 3, 3, 3);
            t.Controls.Add(c, 1, _row);
            if (extra != null)
                t.Controls.Add(extra, 2, _row);
            else
                t.SetColumnSpan(c, 2);
            _row++;
        }

        private void AddFull(TableLayoutPanel t, Control c, int height)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, _row);
            t.SetColumnSpan(c, 3);
            _row++;
        }

        private void WireEvents()
        {
            _btnParts.Click += (s, e) => BrowseParts();
            _btnShp.Click += (s, e) => BrowseShp();
            _txtParts.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) LoadLibrary(_txtParts.Text); };
            _txtShp.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) LoadShapes(_txtShp.Text); };
            _cboFamily.SelectedIndexChanged += (s, e) => FillPartList();
            _lstParts.ItemCheck += LstParts_ItemCheck;
            _btnBuild.Click += BtnBuild_Click;
            _btnExport.Click += (s, e) => Export();
            _preview.HoverInfo += (s, info) => _lblStatus.Text = info;
            FormClosing += (s, e) => SaveUiToSettings();
        }

        // ------------------------------------------------------------------ Einstellungen

        private void LoadSettingsToUi()
        {
            _numBeam.Value = Clamp(_numBeam, _settings.GetDecimal("beam", 12));
            _numPiece.Value = Clamp(_numPiece, _settings.GetDecimal("piecePenalty", 1.0m));
            _numTurn.Value = Clamp(_numTurn, _settings.GetDecimal("turnPenalty", 0.05m));
            _numEndTol.Value = Clamp(_numEndTol, _settings.GetDecimal("endTol", 3.5m));
            _numStartRange.Value = Clamp(_numStartRange, _settings.GetDecimal("startRange", 0));
            _numYawOff.Value = Clamp(_numYawOff, _settings.GetDecimal("yawOffset", 0));
            _numOffX.Value = Clamp(_numOffX, _settings.GetDecimal("offX", 0));
            _numOffY.Value = Clamp(_numOffY, _settings.GetDecimal("offY", 0));
            _chkInvertYaw.Checked = _settings.GetBool("invertYaw", false);
            _chkReverse.Checked = _settings.GetBool("reverse", false);
            _chkFlipCaps.Checked = _settings.GetBool("flipCaps", false);
            _chkCapStart.Checked = _settings.GetBool("capStart", true);
            _chkCapEnd.Checked = _settings.GetBool("capEnd", true);
            _cboRef.SelectedIndex = _settings.GetBool("modelOrigin", false) ? 1 : 0;

            string parts = _settings.GetString("partsFolder", "");
            string shp = _settings.GetString("shp", "");
            _txtParts.Text = parts;
            _txtShp.Text = shp;
            Shown += (s, e) =>
            {
                if (parts.Length > 0 && Directory.Exists(parts)) LoadLibrary(parts);
                if (shp.Length > 0 && File.Exists(shp)) LoadShapes(shp);
            };
        }

        private static decimal Clamp(NumericUpDown n, decimal v)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        private void SaveUiToSettings()
        {
            _settings.SetDecimal("beam", _numBeam.Value);
            _settings.SetDecimal("piecePenalty", _numPiece.Value);
            _settings.SetDecimal("turnPenalty", _numTurn.Value);
            _settings.SetDecimal("endTol", _numEndTol.Value);
            _settings.SetDecimal("startRange", _numStartRange.Value);
            _settings.SetDecimal("yawOffset", _numYawOff.Value);
            _settings.SetDecimal("offX", _numOffX.Value);
            _settings.SetDecimal("offY", _numOffY.Value);
            _settings.SetBool("invertYaw", _chkInvertYaw.Checked);
            _settings.SetBool("reverse", _chkReverse.Checked);
            _settings.SetBool("flipCaps", _chkFlipCaps.Checked);
            _settings.SetBool("capStart", _chkCapStart.Checked);
            _settings.SetBool("capEnd", _chkCapEnd.Checked);
            _settings.SetBool("modelOrigin", _cboRef.SelectedIndex == 1);
            _settings.SetString("partsFolder", _txtParts.Text);
            _settings.SetString("shp", _txtShp.Text);
            if (_cboFamily.SelectedItem != null) _settings.SetString("family", _cboFamily.SelectedItem.ToString());
            RoadPart cap = _cboEndCap.SelectedItem as RoadPart;
            _settings.SetString("endcap." + (_cboFamily.SelectedItem ?? ""), cap != null ? cap.Name : "");
            _settings.Save();
        }

        // ------------------------------------------------------------------ Laden

        private void BrowseParts()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Ordner mit den Straßen-P3Ds (unbinarisiert, z.B. P:\\DZ\\structures\\roads\\parts)";
                if (Directory.Exists(_txtParts.Text)) dlg.SelectedPath = _txtParts.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtParts.Text = dlg.SelectedPath;
                    LoadLibrary(dlg.SelectedPath);
                }
            }
        }

        private void BrowseShp()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Shapefile (*.shp)|*.shp|Alle Dateien (*.*)|*.*";
                dlg.Title = "Polyline-Shapefile wählen";
                if (File.Exists(_txtShp.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_txtShp.Text);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtShp.Text = dlg.FileName;
                    LoadShapes(dlg.FileName);
                }
            }
        }

        private void LoadLibrary(string folder)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _library = RoadPartLibrary.Load(folder);
                Log(string.Format("{0} Straßenteile geladen aus {1}", _library.Parts.Count, folder));
                foreach (string w in _library.Warnings) Log("  Hinweis: " + w);

                _cboFamily.Items.Clear();
                foreach (string f in _library.GetFamilies()) _cboFamily.Items.Add(f);
                if (_cboFamily.Items.Count == 0)
                {
                    Log("Keine verwendbaren Straßenteile gefunden.");
                    FillPartList();
                    return;
                }
                string last = _settings.GetString("family", "");
                int idx = _cboFamily.Items.IndexOf(last);
                _cboFamily.SelectedIndex = idx >= 0 ? idx : 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Fehler beim Laden der Teile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void LoadShapes(string file)
        {
            try
            {
                _shapes = ShapefileReader.Read(file);
                int lines = _shapes.Sum(r => r.Parts.Count);
                double len = 0;
                foreach (ShapeRecord r in _shapes)
                    foreach (List<Vec2> p in r.Parts)
                        for (int i = 1; i < p.Count; i++) len += Vec2.Distance(p[i - 1], p[i]);
                Log(string.Format(CultureInfo.InvariantCulture, "{0}: {1} Linie(n), {2:F1} m gesamt", Path.GetFileName(file), lines, len));
                if (lines == 0) Log("  Achtung: keine Linien gefunden (nur PolyLine/Polygon werden unterstützt).");
                _results = null;
                _btnExport.Enabled = false;
                _preview.SetData(GetLines(), null, true);
            }
            catch (Exception ex)
            {
                _shapes = null;
                MessageBox.Show(this, ex.Message, "Fehler beim Lesen der Shapefile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private List<Vec2[]> GetLines()
        {
            var list = new List<Vec2[]>();
            if (_shapes == null) return list;
            foreach (ShapeRecord r in _shapes)
                foreach (List<Vec2> p in r.Parts)
                    list.Add(p.ToArray());
            return list;
        }

        private void FillPartList()
        {
            _suppressItemCheck = true;
            try
            {
                _lstParts.Items.Clear();
                _cboEndCap.Items.Clear();
                _cboEndCap.Items.Add("(kein Endstück)");
                _cboEndCap.SelectedIndex = 0;
                if (_library == null || _cboFamily.SelectedItem == null) return;

                string fam = _cboFamily.SelectedItem.ToString();
                string lastCap = _settings.GetString("endcap." + fam, "");
                foreach (RoadPart p in _library.GetFamily(fam))
                {
                    if (p.Kind == PartKind.Straight || p.Kind == PartKind.Curve || p.Kind == PartKind.Crosswalk)
                    {
                        bool? saved = _settings.GetPartChecked(p.Name);
                        bool def = p.Kind != PartKind.Crosswalk;
                        _lstParts.Items.Add(p, saved.HasValue ? saved.Value : def);
                    }
                    else if (p.Kind == PartKind.EndCap)
                    {
                        _cboEndCap.Items.Add(p);
                        if (string.Equals(p.Name, lastCap, StringComparison.OrdinalIgnoreCase))
                            _cboEndCap.SelectedItem = p;
                    }
                }
            }
            finally
            {
                _suppressItemCheck = false;
            }
        }

        private void LstParts_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressItemCheck) return;
            RoadPart p = _lstParts.Items[e.Index] as RoadPart;
            if (p != null) _settings.SetPartChecked(p.Name, e.NewValue == CheckState.Checked);
        }

        // ------------------------------------------------------------------ Bauen

        private BuildSettings ReadBuildSettings()
        {
            return new BuildSettings
            {
                BeamWidth = (int)_numBeam.Value,
                PiecePenalty = (double)_numPiece.Value,
                TurnPenalty = (double)_numTurn.Value,
                EndTolerance = (double)_numEndTol.Value,
                StartHeadingRange = (double)_numStartRange.Value,
                UseBoundingCenter = _cboRef.SelectedIndex != 1,
                FlipEndCaps = _chkFlipCaps.Checked,
                EndCapAtStart = _chkCapStart.Checked,
                EndCapAtEnd = _chkCapEnd.Checked
            };
        }

        private ExportSettings ReadExportSettings()
        {
            return new ExportSettings
            {
                InvertYaw = _chkInvertYaw.Checked,
                YawOffset = (double)_numYawOff.Value,
                OffsetX = (double)_numOffX.Value,
                OffsetY = (double)_numOffY.Value
            };
        }

        private async void BtnBuild_Click(object sender, EventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }
            if (_library == null)
            {
                MessageBox.Show(this, "Bitte zuerst den Ordner mit den Straßenteilen laden.", Text);
                return;
            }
            if (_shapes == null || _shapes.Count == 0)
            {
                MessageBox.Show(this, "Bitte zuerst eine Polyline-Shapefile laden.", Text);
                return;
            }
            List<RoadPart> parts = _lstParts.CheckedItems.OfType<RoadPart>().ToList();
            if (!parts.Any(p => p.Kind == PartKind.Straight || p.Kind == PartKind.Curve || p.Kind == PartKind.Crosswalk))
            {
                MessageBox.Show(this, "Bitte mindestens ein Straßenteil anhaken.", Text);
                return;
            }

            RoadPart cap = _cboEndCap.SelectedItem as RoadPart;
            BuildSettings bs = ReadBuildSettings();
            bool reverse = _chkReverse.Checked;

            var paths = new List<RoadPath>();
            foreach (ShapeRecord r in _shapes)
            {
                for (int i = 0; i < r.Parts.Count; i++)
                {
                    try
                    {
                        var path = new RoadPath(r.Parts[i]);
                        path.Name = r.Parts.Count > 1 ? string.Format("Datensatz {0}.{1}", r.RecordNumber, i + 1) : "Datensatz " + r.RecordNumber;
                        paths.Add(reverse ? path.Reversed() : path);
                    }
                    catch (ArgumentException ex)
                    {
                        Log("Datensatz " + r.RecordNumber + " übersprungen: " + ex.Message);
                    }
                }
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _btnBuild.Text = "Abbrechen";
            _btnExport.Enabled = false;
            _progress.Value = 0;
            _lblStatus.Text = "Baue Straße(n) …";

            var overall = new Progress<double>(v => _progress.Value = Math.Max(0, Math.Min(1000, (int)(v * 1000))));
            IProgress<double> rep = overall;
            try
            {
                List<BuildResult> results = await Task.Run(() =>
                {
                    var list = new List<BuildResult>();
                    for (int i = 0; i < paths.Count; i++)
                    {
                        int idx = i;
                        var sub = new DelegateProgress(v => rep.Report((idx + v) / paths.Count));
                        BuildResult res = RoadBuilder.Build(paths[i], parts, cap, bs, sub, token);
                        foreach (PlacedPart p in res.Parts) p.RoadIndex = i;
                        list.Add(res);
                    }
                    return list;
                }, token);

                _results = results;
                ReportResults(results);
                _preview.SetData(paths.Select(p => p.Points), results.SelectMany(r => r.Parts), false);
                _btnExport.Enabled = results.Any(r => r.Parts.Count > 0);
                _lblStatus.Text = "Fertig: " + results.Sum(r => r.Parts.Count) + " Teile";
            }
            catch (OperationCanceledException)
            {
                Log("Abgebrochen.");
                _lblStatus.Text = "Abgebrochen";
            }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "Fehler beim Bauen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _btnBuild.Text = "Straße bauen";
                _progress.Value = 0;
            }
        }

        private void ReportResults(List<BuildResult> results)
        {
            foreach (BuildResult r in results)
            {
                Log(string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1} Teile | Linie {2:F1} m, Straße {3:F1} m | max. Abweichung {4:F2} m, Ø {5:F2} m | Endabstand {6:F2} m | Fugen ≤ {7:F3} m | {8:F1} s",
                    r.Path.Name, r.Parts.Count, r.Path.Length, r.RoadLength, r.MaxDeviation, r.RmsDeviation, r.EndGap, r.MaxJointGap, r.Duration.TotalSeconds));
                foreach (string w in r.Warnings) Log("  Warnung: " + w);
            }
            var usage = results.SelectMany(r => r.Parts)
                .GroupBy(p => p.Part.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key + " ×" + g.Count());
            Log("  Verwendet: " + string.Join(", ", usage));
        }

        // ------------------------------------------------------------------ Export

        private void Export()
        {
            if (_results == null) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Terrain-Builder-Objektliste (*.txt)|*.txt";
                dlg.Title = "Objektliste speichern";
                string shp = _txtShp.Text;
                dlg.FileName = (File.Exists(shp) ? Path.GetFileNameWithoutExtension(shp) : "road") + "_objects.txt";
                if (File.Exists(shp)) dlg.InitialDirectory = Path.GetDirectoryName(shp);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    List<PlacedPart> all = _results.SelectMany(r => r.Parts).ToList();
                    TerrainBuilderExporter.Write(dlg.FileName, all, ReadExportSettings());
                    Log(all.Count + " Objekte exportiert: " + dlg.FileName);
                    _lblStatus.Text = "Exportiert: " + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Fehler beim Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void Log(string line)
        {
            _txtLog.AppendText(line + Environment.NewLine);
        }

        /// <summary>IProgress, das direkt (ohne SynchronizationContext) einen Delegaten aufruft.</summary>
        private sealed class DelegateProgress : IProgress<double>
        {
            private readonly Action<double> _a;
            public DelegateProgress(Action<double> a) { _a = a; }
            public void Report(double value) { _a(value); }
        }
    }
}
