using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using DayZRoadBuilder.Core;

namespace DayZRoadBuilder.App
{
    /// <summary>
    /// Draufsicht: Soll-Linien (rot) und gesetzte Straßenteile.
    /// Mausrad = Zoom, linke/mittlere Maustaste ziehen = verschieben, Doppelklick = alles einpassen.
    /// </summary>
    internal sealed class PreviewControl : Control
    {
        private readonly List<Vec2[]> _lines = new List<Vec2[]>();
        private readonly List<PlacedPart> _parts = new List<PlacedPart>();
        private readonly List<Vec2[]> _outlines = new List<Vec2[]>();

        private double _cx, _cz;      // Weltpunkt in Bildmitte
        private double _scale = 1.0;  // Pixel pro Meter
        private bool _dragging;
        private Point _dragStart;
        private double _dragCx, _dragCz;
        private int _hover = -1;

        /// <summary>Wird bei Mausbewegung mit einem Infotext (Koordinaten / Teil unter dem Mauszeiger) ausgelöst.</summary>
        public event EventHandler<string> HoverInfo;

        public bool ShowLabels { get; set; }

        public PreviewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(28, 30, 34);
            ShowLabels = true;
        }

        public void SetData(IEnumerable<Vec2[]> lines, IEnumerable<PlacedPart> parts, bool fit)
        {
            _lines.Clear();
            _parts.Clear();
            _outlines.Clear();
            _hover = -1;
            if (lines != null) _lines.AddRange(lines);
            if (parts != null)
            {
                foreach (PlacedPart p in parts)
                {
                    _parts.Add(p);
                    _outlines.Add(p.GetWorldOutline(p.Part.IsStraight ? 1 : 12));
                }
            }
            if (fit) FitAll();
            Invalidate();
        }

        public void FitAll()
        {
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            Action<Vec2> grow = p =>
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            };
            foreach (Vec2[] l in _lines) foreach (Vec2 p in l) grow(p);
            foreach (Vec2[] o in _outlines) foreach (Vec2 p in o) grow(p);
            if (minX > maxX)
            {
                _cx = 0; _cz = 0; _scale = 1;
            }
            else
            {
                _cx = (minX + maxX) / 2;
                _cz = (minZ + maxZ) / 2;
                double w = Math.Max(1.0, maxX - minX), h = Math.Max(1.0, maxZ - minZ);
                _scale = Math.Min(Math.Max(50, Width - 40) / w, Math.Max(50, Height - 40) / h);
            }
            Invalidate();
        }

        private PointF ToScreen(Vec2 p)
        {
            return new PointF((float)((p.X - _cx) * _scale + Width / 2.0), (float)(Height / 2.0 - (p.Z - _cz) * _scale));
        }

        private Vec2 ToWorld(Point p)
        {
            return new Vec2((p.X - Width / 2.0) / _scale + _cx, (Height / 2.0 - p.Y) / _scale + _cz);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            DrawScaleBar(g);

            if (_lines.Count == 0 && _parts.Count == 0)
            {
                using (var b = new SolidBrush(Color.Gray))
                    g.DrawString("Teile-Ordner und SHP-Datei laden, dann \"Straße bauen\".", Font, b, 10, 10);
                return;
            }

            Color[] fills =
            {
                Color.FromArgb(110, 70, 130, 200),
                Color.FromArgb(110, 90, 160, 230)
            };
            using (var outline = new Pen(Color.FromArgb(220, 160, 200, 255), 1f))
            using (var hoverPen = new Pen(Color.Yellow, 2f))
            using (var labelBrush = new SolidBrush(Color.White))
            using (var labelFont = new Font("Segoe UI", 7f))
            {
                for (int i = 0; i < _outlines.Count; i++)
                {
                    PointF[] pts = Array.ConvertAll(_outlines[i], ToScreen);
                    using (var b = new SolidBrush(_parts[i].Part.Kind == PartKind.EndCap ? Color.FromArgb(120, 200, 140, 60) : fills[i % 2]))
                        g.FillPolygon(b, pts);
                    g.DrawPolygon(i == _hover ? hoverPen : outline, pts);
                }

                if (ShowLabels && _scale > 3.0)
                {
                    for (int i = 0; i < _parts.Count; i++)
                    {
                        PointF c = ToScreen(_parts[i].Position);
                        string s = _parts[i].Part.Name + (_parts[i].Reversed ? " ↺" : "");
                        SizeF sz = g.MeasureString(s, labelFont);
                        g.DrawString(s, labelFont, labelBrush, c.X - sz.Width / 2, c.Y - sz.Height / 2);
                    }
                }
            }

            using (var linePen = new Pen(Color.FromArgb(255, 80, 80), 1.5f))
            using (var startBrush = new SolidBrush(Color.LimeGreen))
            using (var endBrush = new SolidBrush(Color.OrangeRed))
            {
                foreach (Vec2[] l in _lines)
                {
                    if (l.Length < 2) continue;
                    PointF[] pts = Array.ConvertAll(l, ToScreen);
                    g.DrawLines(linePen, pts);
                    PointF s = pts[0], en = pts[pts.Length - 1];
                    g.FillEllipse(startBrush, s.X - 4, s.Y - 4, 8, 8);
                    g.FillEllipse(endBrush, en.X - 4, en.Y - 4, 8, 8);
                }
            }
        }

        private void DrawScaleBar(Graphics g)
        {
            double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };
            double meters = steps[steps.Length - 1];
            foreach (double s in steps)
            {
                if (s * _scale >= 60)
                {
                    meters = s;
                    break;
                }
            }
            float len = (float)(meters * _scale);
            float x = 10, y = Height - 14;
            using (var p = new Pen(Color.Silver, 2f))
            using (var b = new SolidBrush(Color.Silver))
            {
                g.DrawLine(p, x, y, x + len, y);
                g.DrawLine(p, x, y - 4, x, y + 4);
                g.DrawLine(p, x + len, y - 4, x + len, y + 4);
                g.DrawString(meters.ToString(CultureInfo.InvariantCulture) + " m", Font, b, x + len + 6, y - 8);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Vec2 before = ToWorld(e.Location);
            double f = e.Delta > 0 ? 1.25 : 0.8;
            _scale = Math.Max(0.001, Math.Min(500.0, _scale * f));
            Vec2 after = ToWorld(e.Location);
            _cx += before.X - after.X;
            _cz += before.Z - after.Z;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                _dragging = true;
                _dragStart = e.Location;
                _dragCx = _cx;
                _dragCz = _cz;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            FitAll();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                _cx = _dragCx - (e.X - _dragStart.X) / _scale;
                _cz = _dragCz + (e.Y - _dragStart.Y) / _scale;
                Invalidate();
                return;
            }

            Vec2 w = ToWorld(e.Location);
            int hit = -1;
            for (int i = _outlines.Count - 1; i >= 0; i--)
            {
                if (Geo.PointInPolygon(_outlines[i], w))
                {
                    hit = i;
                    break;
                }
            }
            if (hit != _hover)
            {
                _hover = hit;
                Invalidate();
            }

            string info = string.Format(CultureInfo.InvariantCulture, "X {0:F2}   Y {1:F2}", w.X, w.Z);
            if (hit >= 0)
            {
                PlacedPart p = _parts[hit];
                info += string.Format(CultureInfo.InvariantCulture, "   |   #{0} {1}{2}  –  Pos {3:F3} / {4:F3}, Yaw {5:F3}°",
                    hit + 1, p.Part.Name, p.Reversed ? " (gedreht)" : "", p.Position.X, p.Position.Z, p.Yaw);
            }
            EventHandler<string> h = HoverInfo;
            if (h != null) h(this, info);
        }
    }
}
