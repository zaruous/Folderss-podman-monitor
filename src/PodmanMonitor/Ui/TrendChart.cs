using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PodmanMonitor.Monitoring;

namespace PodmanMonitor.Ui
{
    public sealed class ChartSeries
    {
        public ChartSeries(string name, Color color, IReadOnlyList<Sample> points, double thickness = 2)
        {
            Name = name; Color = color; Points = points; Thickness = thickness;
        }
        public string Name { get; }
        public Color Color { get; }
        public IReadOnlyList<Sample> Points { get; }
        public double Thickness { get; }
    }

    /// <summary>시계열 선 그래프. 글자·격자 색은 Folderss 테마 리소스(PrimaryText·SecondaryText·BorderBrush)를 따른다.</summary>
    public sealed class TrendChart : FrameworkElement
    {
        private List<ChartSeries> _series = new List<ChartSeries>();

        public string Title { get; set; } = "";
        public string Unit { get; set; } = "%";
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);
        /// <summary>Y 축 최솟값 상한(값이 작아도 이 높이까지는 보여 준다).</summary>
        public double MinTop { get; set; } = 1;
        /// <summary>세로축 상한. 백분율 그래프는 100 — 90% 를 넘었다고 눈금이 200 까지 늘지 않게.</summary>
        public double MaxTop { get; set; } = 100;

        public IReadOnlyList<ChartSeries> Series => _series;

        public void SetSeries(IEnumerable<ChartSeries> series)
        {
            _series = series.ToList();
            InvalidateVisual();
        }

        private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // 마우스 적중 영역
            var plot = new Rect(44, 26, Math.Max(0, w - 44 - 10), Math.Max(0, h - 26 - 20));
            if (plot.Width < 20 || plot.Height < 20) return;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var primary = Res("PrimaryText", Brushes.Black);
            var secondary = Res("SecondaryText", Brushes.Gray);
            var border = Res("BorderBrush", Brushes.LightGray);
            var font = TryFindResource("AppFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
            var face = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            FormattedText Text(string s, Brush b, Typeface tf = null, double size = 11) =>
                new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf ?? face, size, b, dpi);

            // 제목 + 범례(현재값)
            var title = Text(Title, primary, bold, 12);
            dc.DrawText(title, new Point(6, 4));
            double lx = 6 + title.Width + 14;
            foreach (var s in _series)
            {
                var label = s.Points.Count > 0 ? $"{s.Name} {s.Points[s.Points.Count - 1].Value:0.##}{Unit}" : s.Name;
                dc.DrawRectangle(new SolidColorBrush(s.Color), null, new Rect(lx, 11, 10, 4));
                var ft = Text(label, secondary);
                dc.DrawText(ft, new Point(lx + 13, 5));
                lx += 13 + ft.Width + 12;
            }

            var end = DateTime.Now;
            var start = end - Window;
            double max = _series.SelectMany(s => s.Points).Where(p => p.Time >= start).Select(p => p.Value).DefaultIfEmpty(0).Max();
            double top = Math.Min(NiceCeiling(Math.Max(max * 1.1, MinTop)), Math.Max(MaxTop, max));

            // 격자·Y 눈금
            var gridPen = new Pen(border, 0.5) { DashStyle = DashStyles.Dot };
            const int steps = 4;
            for (int i = 0; i <= steps; i++)
            {
                double y = plot.Bottom - plot.Height * i / steps;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var ft = Text((top * i / steps).ToString("0.##"), secondary, null, 10);
                dc.DrawText(ft, new Point(plot.Left - ft.Width - 4, y - ft.Height / 2));
            }
            dc.DrawRectangle(null, new Pen(border, 1), plot);

            // X 축 시간
            dc.DrawText(Text(start.ToString("HH:mm:ss"), secondary, null, 10), new Point(plot.Left, plot.Bottom + 3));
            var endText = Text(end.ToString("HH:mm:ss"), secondary, null, 10);
            dc.DrawText(endText, new Point(plot.Right - endText.Width, plot.Bottom + 3));

            // 선
            dc.PushClip(new RectangleGeometry(plot));
            foreach (var s in _series)
            {
                var pts = s.Points.Where(p => p.Time >= start - TimeSpan.FromSeconds(30))
                    .Select(p => new Point(
                        plot.Left + (p.Time - start).TotalMilliseconds / Window.TotalMilliseconds * plot.Width,
                        plot.Bottom - p.Value / top * plot.Height))
                    .ToList();
                var brush = new SolidColorBrush(s.Color);
                if (pts.Count == 1) dc.DrawEllipse(brush, null, pts[0], 2, 2);
                if (pts.Count < 2) continue;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(pts[0], false, false);
                    ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(brush, s.Thickness) { LineJoin = PenLineJoin.Round }, geo);
            }
            dc.Pop();

            if (_series.All(s => s.Points.Count == 0))
                dc.DrawText(Text("데이터 수집 중...", secondary), new Point(plot.Left + 8, plot.Top + 8));
        }

        internal static double NiceCeiling(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
                if (m * mag >= v) return m * mag;
            return 10 * mag;
        }
    }
}
