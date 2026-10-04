using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DayZRoadBuilder.Core;
using DayZRoadBuilder.Core.Tv4p;

namespace DayZRoadBuilder.App
{
    /// <summary>Main window. The UI is built entirely in code (no designer file required).</summary>
    internal sealed class MainForm : Form
    {
        // Files
        private readonly TextBox _txtParts = new TextBox();
        private readonly TextBox _txtShp = new TextBox();
        private readonly Button _btnParts = new Button { Text = "…", Width = 32 };
        private readonly Button _btnShp = new Button { Text = "…", Width = 32 };

        // Road type & parts
        private readonly ComboBox _cboFamily = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckedListBox _lstParts = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        private readonly ComboBox _cboEndCap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _chkCapStart = new CheckBox { Text = "at start", AutoSize = true, Checked = true };
        private readonly CheckBox _chkCapEnd = new CheckBox { Text = "at end", AutoSize = true, Checked = true };
        private readonly CheckBox _chkFlipCaps = new CheckBox { Text = "flip end pieces", AutoSize = true };

        // Crossroads
        private readonly CheckBox _chkCross = new CheckBox { Text = "Place crossroads at junctions", AutoSize = true, Checked = true };
        private readonly ComboBox _cboTPart = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _cboXPart = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _chkSideType = new CheckBox { Text = "Side roads use the crossroad's side road type", AutoSize = true, Checked = true };
        private readonly NumericUpDown _numSnap = Num(0.1m, 20, 1.5m, 2, 0.5m);

        // Build options
        private readonly CheckBox _chkReverse = new CheckBox { Text = "Reverse line direction", AutoSize = true };
        private readonly NumericUpDown _numBeam = Num(1, 200, 12, 0, 1);
        private readonly NumericUpDown _numPiece = Num(0, 100, 1.0m, 2, 0.25m);
        private readonly NumericUpDown _numTurn = Num(0, 10, 0.05m, 3, 0.05m);
        private readonly NumericUpDown _numEndTol = Num(0.1m, 50, 3.5m, 2, 0.5m);
        private readonly NumericUpDown _numStartRange = Num(0, 45, 0, 1, 0.5m);

        // Export options
        private readonly ComboBox _cboRef = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _chkInvertYaw = new CheckBox { Text = "Invert yaw (counter-clockwise)", AutoSize = true };
        private readonly NumericUpDown _numYawOff = Num(-360, 360, 0, 3, 90);
        private readonly NumericUpDown _numOffX = Num(-10000000, 10000000, 0, 3, 1);
        private readonly NumericUpDown _numOffY = Num(-10000000, 10000000, 0, 3, 1);

        private readonly Button _btnBuild = new Button { Text = "Build road", Height = 34, Dock = DockStyle.Fill };
        private readonly Button _btnExport = new Button { Text = "Export as objects (.txt) …", Height = 30, Dock = DockStyle.Fill, Enabled = false };
        private readonly Button _btnExportTv4p = new Button { Text = "Write as roads into Terrain Builder project (.tv4p) …", Height = 30, Dock = DockStyle.Fill, Enabled = false };
        private readonly TextBox _txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };

        private readonly PreviewControl _preview = new PreviewControl { Dock = DockStyle.Fill };
        private readonly ToolStripStatusLabel _lblStatus = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripProgressBar _progress = new ToolStripProgressBar { Minimum = 0, Maximum = 1000, Width = 200 };

        private readonly AppSettings _settings = AppSettings.Load();
        private RoadPartLibrary _library;
        private List<ShapeRecord> _shapes;
        private NetworkResult _network;
        private CancellationTokenSource _cts;
        private bool _suppressItemCheck;
        private int _row;

        public MainForm()
        {
            Text = "DayZ Road Builder – automatic roads from polylines";
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

            AddHeader(t, "1. Files");
            AddRow(t, "Road parts folder", _txtParts, _btnParts);
            AddRow(t, "Polyline (.shp)", _txtShp, _btnShp);

            AddHeader(t, "2. Road type and parts");
            AddRow(t, "Road type", _cboFamily, null);
            AddFull(t, _lstParts, 230);
            AddRow(t, "End piece", _cboEndCap, null);
            var capFlow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            capFlow.Controls.Add(_chkCapStart);
            capFlow.Controls.Add(_chkCapEnd);
            capFlow.Controls.Add(_chkFlipCaps);
            AddRow(t, "", capFlow, null);

            AddHeader(t, "3. Crossroads");
            AddRow(t, "", _chkCross, null);
            AddRow(t, "T-junction", _cboTPart, null);
            AddRow(t, "X-crossroad", _cboXPart, null);
            AddRow(t, "", _chkSideType, null);
            AddRow(t, "Join distance [m]", _numSnap, null);

            AddHeader(t, "4. Fitting");
            AddRow(t, "Search width", _numBeam, null);
            AddRow(t, "Penalty per part", _numPiece, null);
            AddRow(t, "Penalty per degree", _numTurn, null);
            AddRow(t, "End tolerance [m]", _numEndTol, null);
            AddRow(t, "Start angle ± [°]", _numStartRange, null);
            AddRow(t, "", _chkReverse, null);

            AddHeader(t, "5. Export (Terrain Builder)");
            AddRow(t, "Position =", _cboRef, null);
            AddRow(t, "", _chkInvertYaw, null);
            AddRow(t, "Yaw offset [°]", _numYawOff, null);
            AddRow(t, "Offset X [m]", _numOffX, null);
            AddRow(t, "Offset Y [m]", _numOffY, null);

            AddFull(t, _btnBuild, 40);
            AddFull(t, _btnExport, 36);
            AddFull(t, _btnExportTv4p, 36);
            AddHeader(t, "Log");
            AddFull(t, _txtLog, 220);

            scroll.Controls.Add(t);
            split.Panel1.Controls.Add(scroll);
            split.Panel2.Controls.Add(_preview);

            var status = new StatusStrip();
            status.Items.Add(_lblStatus);
            status.Items.Add(_progress);

            Controls.Add(split);
            Controls.Add(status);

            _cboRef.Items.Add("Bounding box centre (default)");
            _cboRef.Items.Add("Model origin [0,0,0]");
            _cboRef.SelectedIndex = 0;

            // set the panel width once the window has its size
            Load += (s, e) =>
            {
                try { split.SplitterDistance = 430; } catch (InvalidOperationException) { }
            };

            var tip = new ToolTip { AutoPopDelay = 20000 };
            tip.SetToolTip(_numBeam, "How many candidates are kept per 0.5 m of the line. Higher = more accurate but slower. 8–30 is sensible.");
            tip.SetToolTip(_numPiece, "Cost per part. Higher = fewer, longer parts (slightly more deviation).");
            tip.SetToolTip(_numTurn, "Cost per degree of curve. Higher = more straights, less wiggling.");
            tip.SetToolTip(_numEndTol, "How far from the end of the line the last part may stop.");
            tip.SetToolTip(_numStartRange, "Allows a slightly different start direction (± degrees) if the line has a kink at its start.");
            tip.SetToolTip(_cboRef, "Which model point is exported as the object position. DayZ / Terrain Builder use the bounding box centre for road parts (autocenter).");
            tip.SetToolTip(_chkInvertYaw, "Only change this if the parts appear mirrored / rotated after importing.");
            tip.SetToolTip(_chkCross, "Where 3 lines meet a T-junction part is placed, where 4 lines meet (or two lines cross) an X-crossroad part – if the road type has such parts (Chernarus types only).");
            tip.SetToolTip(_cboTPart, "Crossroad part used for T-junctions. The name says kr_t_<through road>_<side road>.");
            tip.SetToolTip(_cboXPart, "Crossroad part used for X-crossroads. The name says kr_x_<through road>_<side roads>.");
            tip.SetToolTip(_chkSideType, "Build side roads with the road type of the crossroad's side arm (e.g. asf2 for kr_t_asf1_asf2), so the widths match.");
            tip.SetToolTip(_numSnap, "Line ends closer than this are treated as connected. Lines touching or crossing another line are split there automatically.");
            tip.SetToolTip(_numOffX, "Added to all X coordinates (e.g. 200000 if the line was exported without the Terrain Builder easting offset).");
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
            _txtParts.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadLibrary(_txtParts.Text); } };
            _txtShp.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; LoadShapes(_txtShp.Text); } };
            _cboFamily.SelectedIndexChanged += (s, e) => FillPartList();
            _chkCross.CheckedChanged += (s, e) => UpdateCrossEnabled();
            _lstParts.ItemCheck += LstParts_ItemCheck;
            _btnBuild.Click += BtnBuild_Click;
            _btnExport.Click += (s, e) => Export();
            _btnExportTv4p.Click += (s, e) => ExportTv4p();
            _preview.HoverInfo += (s, info) => _lblStatus.Text = info;
            FormClosing += (s, e) => SaveUiToSettings();
        }

        // ------------------------------------------------------------------ Settings

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
            _chkCross.Checked = _settings.GetBool("crossroads", true);
            _chkSideType.Checked = _settings.GetBool("sideType", true);
            _numSnap.Value = Clamp(_numSnap, _settings.GetDecimal("snap", 1.5m));

            string parts = _settings.GetString("partsFolder", "");
            if (parts.Length == 0 || !Directory.Exists(parts))
                parts = FindBundledPartsFolder() ?? parts;
            string shp = _settings.GetString("shp", "");
            _txtParts.Text = parts;
            _txtShp.Text = shp;
            Shown += (s, e) =>
            {
                if (parts.Length > 0 && Directory.Exists(parts)) LoadLibrary(parts);
                if (shp.Length > 0 && File.Exists(shp)) LoadShapes(shp);
            };
        }

        /// <summary>Looks for the "RoadParts" folder of the repository next to the EXE or in one of its parent folders.</summary>
        private static string FindBundledPartsFolder()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir.FullName, "RoadParts");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            catch
            {
                // not important
            }
            return null;
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
            _settings.SetBool("crossroads", _chkCross.Checked);
            _settings.SetBool("sideType", _chkSideType.Checked);
            _settings.SetDecimal("snap", _numSnap.Value);
            SaveCrossChoice();
            _settings.SetString("partsFolder", _txtParts.Text);
            _settings.SetString("shp", _txtShp.Text);
            if (_cboFamily.SelectedItem != null) _settings.SetString("family", _cboFamily.SelectedItem.ToString());
            RoadPart cap = _cboEndCap.SelectedItem as RoadPart;
            _settings.SetString("endcap." + (_cboFamily.SelectedItem ?? ""), cap != null ? cap.Name : "");
            _settings.Save();
        }

        // ------------------------------------------------------------------ Loading

        private void BrowseParts()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Folder containing the road P3Ds (unbinarized, e.g. RoadParts or P:\\DZ\\structures\\roads\\parts)";
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
                dlg.Filter = "Shapefile (*.shp)|*.shp|All files (*.*)|*.*";
                dlg.Title = "Select polyline shapefile";
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
                Log(string.Format("{0} road parts loaded from {1}", _library.Parts.Count, folder));
                foreach (string w in _library.Warnings) Log("  Note: " + w);

                _cboFamily.Items.Clear();
                foreach (string f in _library.GetFamilies()) _cboFamily.Items.Add(f);
                if (_cboFamily.Items.Count == 0)
                {
                    Log("No usable road parts found.");
                    FillPartList();
                    return;
                }
                string last = _settings.GetString("family", "");
                int idx = _cboFamily.Items.IndexOf(last);
                _cboFamily.SelectedIndex = idx >= 0 ? idx : 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error loading road parts", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                Log(string.Format(CultureInfo.InvariantCulture, "{0}: {1} line(s), {2:F1} m in total", Path.GetFileName(file), lines, len));
                if (lines == 0) Log("  Warning: no lines found (only PolyLine / Polygon shapes are supported).");
                _network = null;
                _btnExport.Enabled = false;
                _btnExportTv4p.Enabled = false;
                _preview.SetData(GetLines(), null, true);
            }
            catch (Exception ex)
            {
                _shapes = null;
                MessageBox.Show(this, ex.Message, "Error reading shapefile", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        private string _crossFamily;

        private void FillPartList()
        {
            SaveCrossChoice();
            _suppressItemCheck = true;
            try
            {
                _lstParts.Items.Clear();
                _cboEndCap.Items.Clear();
                _cboEndCap.Items.Add("(no end piece)");
                _cboEndCap.SelectedIndex = 0;
                string famName = _cboFamily.SelectedItem != null ? _cboFamily.SelectedItem.ToString() : null;
                FillCrossCombo(_cboTPart, famName, 'T');
                FillCrossCombo(_cboXPart, famName, 'X');
                _crossFamily = famName;
                UpdateCrossEnabled();
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

        private void FillCrossCombo(ComboBox cbo, string fam, char type)
        {
            cbo.Items.Clear();
            List<RoadPart> list = (_library == null || fam == null) ? new List<RoadPart>() : NetworkBuilder.GetCrossroads(_library, fam, type);
            if (list.Count == 0)
            {
                cbo.Items.Add("(none for this road type)");
                cbo.SelectedIndex = 0;
                cbo.Tag = false;
                return;
            }
            cbo.Tag = true;
            cbo.Items.Add("(automatic: " + list[0].Name + ")");
            foreach (RoadPart p in list) cbo.Items.Add(p);
            cbo.SelectedIndex = 0;
            string saved = _settings.GetString("cross" + type + "." + fam, "");
            foreach (object o in cbo.Items)
            {
                RoadPart p = o as RoadPart;
                if (p != null && string.Equals(p.Name, saved, StringComparison.OrdinalIgnoreCase)) cbo.SelectedItem = p;
            }
        }

        private void SaveCrossChoice()
        {
            if (string.IsNullOrEmpty(_crossFamily)) return;
            RoadPart t = _cboTPart.SelectedItem as RoadPart;
            RoadPart x = _cboXPart.SelectedItem as RoadPart;
            _settings.SetString("crossT." + _crossFamily, t != null ? t.Name : "");
            _settings.SetString("crossX." + _crossFamily, x != null ? x.Name : "");
        }

        private void UpdateCrossEnabled()
        {
            _cboTPart.Enabled = _chkCross.Checked && Equals(_cboTPart.Tag, true);
            _cboXPart.Enabled = _chkCross.Checked && Equals(_cboXPart.Tag, true);
            _chkSideType.Enabled = _chkCross.Checked;
        }

        private void LstParts_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressItemCheck) return;
            RoadPart p = _lstParts.Items[e.Index] as RoadPart;
            if (p != null) _settings.SetPartChecked(p.Name, e.NewValue == CheckState.Checked);
        }

        // ------------------------------------------------------------------ Building

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

        private NetworkSettings ReadNetworkSettings()
        {
            RoadPart t = _cboTPart.SelectedItem as RoadPart;
            RoadPart x = _cboXPart.SelectedItem as RoadPart;
            return new NetworkSettings
            {
                UseCrossroads = _chkCross.Checked,
                SnapDistance = (double)_numSnap.Value,
                TJunctionPart = t != null ? t.Name : null,
                XJunctionPart = x != null ? x.Name : null,
                SideRoadsUseCrossroadType = _chkSideType.Checked,
                ReverseFreeRoads = _chkReverse.Checked
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
                MessageBox.Show(this, "Please load the road parts folder first.", Text);
                return;
            }
            if (_shapes == null || _shapes.Count == 0)
            {
                MessageBox.Show(this, "Please load a polyline shapefile first.", Text);
                return;
            }
            List<RoadPart> parts = _lstParts.CheckedItems.OfType<RoadPart>().ToList();
            if (!parts.Any(p => p.Kind == PartKind.Straight || p.Kind == PartKind.Curve || p.Kind == PartKind.Crosswalk))
            {
                MessageBox.Show(this, "Please tick at least one road part.", Text);
                return;
            }

            RoadPart cap = _cboEndCap.SelectedItem as RoadPart;
            BuildSettings bs = ReadBuildSettings();
            NetworkSettings ns = ReadNetworkSettings();
            string family = _cboFamily.SelectedItem.ToString();
            RoadPartLibrary lib = _library;
            List<List<Vec2>> lines = _shapes.SelectMany(r => r.Parts).ToList();

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _btnBuild.Text = "Cancel";
            _btnExport.Enabled = false;
            _btnExportTv4p.Enabled = false;
            _progress.Value = 0;
            _lblStatus.Text = "Building road(s) …";

            IProgress<double> rep = new Progress<double>(v => _progress.Value = Math.Max(0, Math.Min(1000, (int)(v * 1000))));
            try
            {
                NetworkResult net = await Task.Run(() => NetworkBuilder.Build(lines, lib, family, parts, cap, bs, ns, rep, token), token);

                _network = net;
                ReportResults(net);
                _preview.SetData(GetLines(), net.AllParts, net.Junctions, false);
                List<PlacedPart> all = net.AllParts.ToList();
                _btnExport.Enabled = all.Count > 0;
                _btnExportTv4p.Enabled = all.Count > 0;
                _lblStatus.Text = "Done: " + all.Count + " parts, " + net.Junctions.Count(j => j.Placed != null) + " crossroads";
            }
            catch (OperationCanceledException)
            {
                Log("Cancelled.");
                _lblStatus.Text = "Cancelled";
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "Error while building", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _btnBuild.Text = "Build road";
                _progress.Value = 0;
            }
        }

        private void ReportResults(NetworkResult net)
        {
            foreach (JunctionInfo j in net.Junctions)
            {
                Log(string.Format(CultureInfo.InvariantCulture, "Junction {0}{1} ({2} lines) at {3:F1} / {4:F1}: {5}{6}",
                    j.Type, j.Id, j.Degree, j.Point.X, j.Point.Z,
                    j.Placed != null ? j.Placed.Part.Name : "no crossroad",
                    string.IsNullOrEmpty(j.Note) ? "" : " – " + j.Note));
            }
            List<BuildResult> results = net.Roads;
            foreach (BuildResult r in results)
            {
                Log(string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1} parts | line {2:F1} m, road {3:F1} m | max. deviation {4:F2} m, avg {5:F2} m | end gap {6:F2} m | joints ≤ {7:F3} m | {8:F1} s",
                    r.Path.Name, r.Parts.Count, r.Path.Length, r.RoadLength, r.MaxDeviation, r.RmsDeviation, r.EndGap, r.MaxJointGap, r.Duration.TotalSeconds));
                foreach (string w in r.Warnings) Log("  Warning: " + w);
            }
            var usage = results.SelectMany(r => r.Parts)
                .GroupBy(p => p.Part.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key + " ×" + g.Count());
            Log("  Used: " + string.Join(", ", usage));
        }

        // ------------------------------------------------------------------ Export

        private void Export()
        {
            if (_network == null) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Terrain Builder object list (*.txt)|*.txt";
                dlg.Title = "Save object list";
                string shp = _txtShp.Text;
                dlg.FileName = (File.Exists(shp) ? Path.GetFileNameWithoutExtension(shp) : "road") + "_objects.txt";
                if (File.Exists(shp)) dlg.InitialDirectory = Path.GetDirectoryName(shp);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    List<PlacedPart> all = _network.AllParts.ToList();
                    TerrainBuilderExporter.Write(dlg.FileName, all, ReadExportSettings());
                    Log(all.Count + " objects exported: " + dlg.FileName);
                    _lblStatus.Text = "Exported: " + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Export error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportTv4p()
        {
            if (_network == null) return;
            string input;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Terrain Builder project (*.tv4p)|*.tv4p";
                dlg.Title = "Select your Terrain Builder project (it is not modified)";
                string last = _settings.GetString("tv4p", "");
                if (File.Exists(last)) dlg.InitialDirectory = Path.GetDirectoryName(last);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                input = dlg.FileName;
            }
            _settings.SetString("tv4p", input);

            Tv4pProjectInfo info;
            try
            {
                info = Tv4pRoadWriter.ReadInfo(input);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Cannot read project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Log(Path.GetFileName(input) + ": " + info.ExistingRoads + " Road Tool roads, road types:" + Environment.NewLine + info.Describe());

            string output;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Terrain Builder project (*.tv4p)|*.tv4p";
                dlg.Title = "Save the project WITH the new roads as a new file";
                dlg.InitialDirectory = Path.GetDirectoryName(input);
                dlg.FileName = Path.GetFileNameWithoutExtension(input) + "_roads.tv4p";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                output = dlg.FileName;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                Tv4pExportResult r = Tv4pRoadWriter.Write(input, output, _network, ReadExportSettings());
                Log(string.Format(CultureInfo.InvariantCulture, "{0} Road Tool roads ({1} parts) written to {2} (the project already had {3} roads).",
                    r.RoadsWritten, r.PartsWritten, output, r.ExistingRoads));
                foreach (string w in r.Warnings) Log("  Warning: " + w);
                _lblStatus.Text = "Project written: " + output;
                MessageBox.Show(this,
                    r.RoadsWritten + " roads were added to a copy of your project:" + Environment.NewLine + output + Environment.NewLine + Environment.NewLine +
                    "Close the project in Terrain Builder (if it is open), then open the new file. The roads appear in the Road Tool and can be edited there." +
                    Environment.NewLine + Environment.NewLine + "Your original project was not changed. Keep it as a backup.",
                    "Roads written", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Could not write the project", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void Log(string line)
        {
            _txtLog.AppendText(line + Environment.NewLine);
        }
    }
}
