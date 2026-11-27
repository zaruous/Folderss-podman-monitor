using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PodmanMonitor.Monitoring;

namespace PodmanMonitor.Ui
{
    /// <summary>
    /// 프로세스 스냅샷 위의 보드: 요약 · CPU 상위 5 · 메모리 상위 5 · 컨테이너별 합계.
    /// 막대 줄을 누르면 <see cref="FilterRequested"/>(표를 그 프로세스·컨테이너로 거른다).
    /// </summary>
    internal sealed class ProcessBoard : Grid
    {
        public const string HostGroup = "(호스트)";
        const int Top = 5;
        static readonly Color CpuColor = Color.FromRgb(214, 39, 40);
        static readonly Color MemColor = Color.FromRgb(44, 160, 44);
        static readonly Color ContainerColor = Color.FromRgb(31, 119, 180);

        public event Action<string> FilterRequested;

        private readonly StackPanel _summary = new StackPanel();
        private readonly StackPanel _cpu = new StackPanel();
        private readonly StackPanel _mem = new StackPanel();
        private readonly StackPanel _containers = new StackPanel();

        public ProcessBoard()
        {
            Margin = new Thickness(8, 0, 2, 6);
            Height = 178;
            foreach (var w in new[] { 22.0, 26, 26, 26 })
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            Add(0, "요약", _summary, "BoardSummary");
            Add(1, "CPU 상위 5", _cpu, "BoardCpu");
            Add(2, "메모리 상위 5", _mem, "BoardMem");
            Add(3, "컨테이너별 CPU·메모리", _containers, "BoardContainers");
        }

        private void Add(int col, string title, Panel body, string id)
        {
            AutomationProperties.SetAutomationId(body, id);
            var head = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            var panel = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
            DockPanel.SetDock(head, Dock.Top);
            panel.Children.Add(head);
            panel.Children.Add(body);
            var card = new Border { Child = panel, Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            SetColumn(card, col);
            Children.Add(card);
        }

        /// <summary>컨테이너별 합계(컨테이너 밖은 "(호스트)"). CPU 합계가 큰 순.</summary>
        public static List<(string Name, int Count, double Cpu, long Rss)> ByContainer(IEnumerable<ProcessRow> rows) =>
            rows.GroupBy(r => string.IsNullOrEmpty(r.Container) ? HostGroup : r.Container)
                .Select(g => (g.Key, g.Count(), g.Sum(r => Math.Max(0, r.Cpu)), g.Sum(r => r.Rss)))
                .OrderByDescending(x => x.Item3).ThenByDescending(x => x.Item4).ToList();

        public void Update(ProcessSnapshot snap, IReadOnlyList<ProcessRow> rows)
        {
            // 요약
            _summary.Children.Clear();
            double cpu = rows.Sum(r => Math.Max(0, r.Cpu));
            long rss = rows.Sum(r => r.Rss);
            int inside = rows.Count(r => r.Container.Length > 0);
            var groups = ByContainer(rows);
            var busiest = groups.FirstOrDefault(g => g.Name != HostGroup);
            Tile("프로세스", $"{rows.Count}개", $"컨테이너 안 {inside}개");
            Tile("CPU 합계", $"{cpu:0.#}%", $"{cpu / 100 * snap.Cores:0.##}코어 / {snap.Cores}코어");
            Tile("메모리 합계", snap.MemTotal > 0 ? $"{100.0 * rss / snap.MemTotal:0.#}%" : Format.Bytes(rss),
                 snap.MemTotal > 0 ? $"{Format.Bytes(rss)} / {Format.Bytes(snap.MemTotal)} (공유 메모리는 겹쳐 셈)" : "");
            if (busiest.Name != null) Tile("바쁜 컨테이너", busiest.Name, $"CPU {busiest.Cpu:0.#}% · {Format.Bytes(busiest.Rss)}");

            // 상위 5
            Fill(_cpu, rows.Where(r => r.Cpu > 0).OrderByDescending(r => r.Cpu).Take(Top)
                    .Select(r => (Label(r), r.Cpu, r.CpuText, r.Command, r.Pid.ToString())).ToList(), CpuColor, "CPU 를 쓰는 프로세스가 없습니다");
            Fill(_mem, rows.OrderByDescending(r => r.Rss).Take(Top)
                    .Select(r => (Label(r), (double)r.Rss, r.RssText + (r.Mem >= 0 ? $" ({r.Mem:0.#}%)" : ""), r.Command, r.Pid.ToString())).ToList(), MemColor, "");
            Fill(_containers, groups.Take(Top + 1)
                    .Select(g => ($"{g.Name} ({g.Count})", g.Cpu, $"{g.Cpu:0.#}% · {Format.Bytes(g.Rss)}", $"{g.Name}: 프로세스 {g.Count}개", g.Name == HostGroup ? "" : g.Name)).ToList(),
                 ContainerColor, "");
        }

        static string Label(ProcessRow r)
        {
            // 첫 낱말이 경로면 마지막 이름만: /usr/bin/dockerd -H … → dockerd -H …
            var cmd = (r.Command ?? "").Trim();
            int space = cmd.IndexOf(' ');
            var exe = space < 0 ? cmd : cmd.Substring(0, space);
            int slash = exe.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0 && slash < exe.Length - 1) cmd = cmd.Substring(slash + 1);
            return $"{r.Pid}  {cmd}";
        }

        private void Tile(string title, string value, string sub)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var head = new TextBlock { Text = title, Width = 92, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(head, Dock.Left);
            row.Children.Add(head);
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.IsNullOrEmpty(sub) ? null : sub };
            text.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(sub)) text.Inlines.Add(new System.Windows.Documents.Run("  " + sub) { FontSize = 11 });
            row.Children.Add(text);
            _summary.Children.Add(row);
        }

        /// <summary>막대 목록. 막대 길이는 목록 안 가장 큰 값 기준. filter 가 비어 있지 않으면 누를 때 그 글자로 거른다.</summary>
        private void Fill(StackPanel panel, List<(string Label, double Value, string Text, string Tip, string Filter)> items, Color color, string empty)
        {
            panel.Children.Clear();
            if (items.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = empty, Opacity = 0.6 });
                return;
            }
            double max = Math.Max(items.Max(i => i.Value), 1e-9);
            var fill = new SolidColorBrush(Color.FromArgb(170, color.R, color.G, color.B));
            foreach (var it in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, ToolTip = it.Tip };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
                var label = new TextBlock { Text = it.Label, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(label);

                var track = new Grid { Height = 18 };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(it.Value / max, 0.001), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - it.Value / max, 0.001), GridUnitType.Star) });
                var barFill = new Border { Background = fill, CornerRadius = new CornerRadius(2) };
                track.Children.Add(barFill);
                var value = new TextBlock { Text = it.Text, FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                SetColumnSpan(value, 2);
                track.Children.Add(value);
                SetColumn(track, 1);
                row.Children.Add(track);

                if (!string.IsNullOrEmpty(it.Filter))
                {
                    var f = it.Filter;
                    row.Cursor = Cursors.Hand;
                    row.MouseLeftButtonUp += (s, e) => FilterRequested?.Invoke(f);
                }
                panel.Children.Add(row);
            }
        }
    }
}
