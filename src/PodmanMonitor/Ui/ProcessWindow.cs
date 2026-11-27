using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;

namespace PodmanMonitor.Ui
{
    /// <summary>프로세스 표 한 줄. 정렬은 숫자 속성으로, 보이는 것은 글자 속성으로.</summary>
    public sealed class ProcessRow
    {
        public ProcessRow(ProcessInfo p, string container)
        {
            Pid = p.Pid;
            User = p.User;
            Cpu = p.CpuPercent ?? -1;
            CpuText = p.CpuPercent.HasValue ? $"{p.CpuPercent:0.0}% ({p.CpuCores:0.00}코어)" : "";
            Mem = p.MemPercent ?? -1;
            MemText = p.MemPercent.HasValue ? $"{p.MemPercent:0.0}%" : "";
            Rss = p.Rss;
            RssText = Format.Bytes(p.Rss);
            ElapsedSeconds = p.Elapsed?.TotalSeconds ?? -1;
            ElapsedText = p.Elapsed.HasValue ? Duration(p.Elapsed.Value) : "";
            Container = container;
            Command = p.Command;
        }

        public int Pid { get; }
        public string User { get; }
        public double Cpu { get; }
        public string CpuText { get; }
        public double Mem { get; }
        public string MemText { get; }
        public long Rss { get; }
        public string RssText { get; }
        public double ElapsedSeconds { get; }
        public string ElapsedText { get; }
        public string Container { get; }
        public string Command { get; }

        public static string Duration(TimeSpan t) =>
            t.TotalDays >= 1 ? $"{(int)t.TotalDays}일 {t:hh\\:mm\\:ss}" : t.ToString("hh\\:mm\\:ss");
    }

    /// <summary>
    /// 서버 프로세스 스냅샷 창(비모달). 찍을 때 한 번만 조회하고, [다시 찍기]를 누르기 전에는 바뀌지 않는다.
    /// 열 머리를 눌러 정렬, 거르기 칸으로 PID·사용자·컨테이너·명령 검색, [저장…]으로 탭 구분 텍스트.
    /// </summary>
    internal sealed class ProcessWindow : Window
    {
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly SshConnection _ssh;
        private readonly string _host;
        private readonly Func<IReadOnlyDictionary<string, string>> _containerNames;
        private readonly DataGrid _grid = new DataGrid();
        private readonly TextBox _filter = new TextBox { Width = 220 };
        private readonly TextBlock _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private readonly Button _retake = new Button { Content = "다시 찍기", Padding = new Thickness(10, 2, 10, 2) };
        private readonly Button _save = new Button { Content = "저장…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        private readonly ProcessBoard _board = new ProcessBoard();
        private readonly CheckBox _showBoard = new CheckBox { Content = "보드", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        private List<ProcessRow> _rows = new List<ProcessRow>();
        private ProcessSnapshot _snap;

        public ProcessWindow(Window owner, SshConnection ssh, string host, Func<IReadOnlyDictionary<string, string>> containerNames)
        {
            _ssh = ssh; _host = host; _containerNames = containerNames;
            Title = $"프로세스 스냅샷 - {host}";
            Width = 1200; Height = 700;
            Owner = owner;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");

            Id(_grid, "ProcessGrid");
            Id(_filter, "ProcessFilter");
            Id(_summary, "ProcessSummary");
            Id(_retake, "ProcessRetake");
            Id(_save, "ProcessSave");
            _retake.Click += async (s, e) => await TakeAsync();
            _save.Click += (s, e) => Save();
            _filter.TextChanged += (s, e) => CollectionViewSource.GetDefaultView(_grid.ItemsSource)?.Refresh();
            _filter.ToolTip = "PID·사용자·컨테이너·명령에 이 글자가 들어간 것만";

            var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_retake);
            left.Children.Add(_save);
            left.Children.Add(new TextBlock { Text = "거르기", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
            left.Children.Add(_filter);
            left.Children.Add(_showBoard);
            Id(_showBoard, "ProcessBoardBox");
            _showBoard.Click += (s, e) => _board.Visibility = _showBoard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _board.FilterRequested += f => _filter.Text = f;
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);
            _summary.TextTrimming = TextTrimming.CharacterEllipsis;
            bar.Children.Add(_summary);

            BuildGrid();
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            DockPanel.SetDock(_board, Dock.Top);
            _board.Visibility = Visibility.Collapsed; // 첫 스냅샷이 들어오면 보인다
            root.Children.Add(_board);
            root.Children.Add(_grid);
            Content = root;
            Loaded += async (s, e) => await TakeAsync();
        }

        private static void Id(UIElement e, string id) => AutomationProperties.SetAutomationId(e, id);

        private void BuildGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.CanUserAddRows = false;
            _grid.SelectionMode = DataGridSelectionMode.Extended;
            _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.BorderThickness = new Thickness(1, 1, 0, 0);
            _grid.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "PanelBackground");
            _grid.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");
            _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");
            VirtualizingPanel.SetIsVirtualizing(_grid, true);

            var header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SurfaceBackground")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            _grid.ColumnHeaderStyle = header;

            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SelectionBrush")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
            cell.Triggers.Add(selected);
            _grid.CellStyle = cell;

            var row = new Style(typeof(DataGridRow));
            row.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PanelBackground")));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("RowHoverBrush")));
            row.Triggers.Add(hover);
            _grid.RowStyle = row;

            Column("PID", nameof(ProcessRow.Pid), nameof(ProcessRow.Pid), right: true);
            Column("사용자", nameof(ProcessRow.User));
            Column("CPU", nameof(ProcessRow.CpuText), nameof(ProcessRow.Cpu), right: true, descending: true);
            Column("메모리 %", nameof(ProcessRow.MemText), nameof(ProcessRow.Mem), right: true, descending: true);
            Column("메모리", nameof(ProcessRow.RssText), nameof(ProcessRow.Rss), right: true, descending: true);
            Column("실행 시간", nameof(ProcessRow.ElapsedText), nameof(ProcessRow.ElapsedSeconds), right: true, descending: true);
            Column("컨테이너", nameof(ProcessRow.Container));
            Column("명령", nameof(ProcessRow.Command), mono: true);

            // 숫자 열은 처음 누를 때 큰 값부터. 정렬 표시는 머리 글자 뒤 ▲▼(기본 화살표는 어두운 테마에서 보이지 않는다)
            _grid.Sorting += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(UpdateSortGlyphs), System.Windows.Threading.DispatcherPriority.Background);
                if (e.Column.SortDirection == null && Equals(e.Column.GetValue(DescendingFirstProperty), true))
                {
                    e.Handled = true;
                    var view = CollectionViewSource.GetDefaultView(_grid.ItemsSource);
                    foreach (var c in _grid.Columns) c.SortDirection = null;
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, ListSortDirection.Descending));
                    e.Column.SortDirection = ListSortDirection.Descending;
                }
            };
        }

        static readonly DependencyProperty BaseHeaderProperty =
            DependencyProperty.RegisterAttached("BaseHeader", typeof(string), typeof(ProcessWindow), new PropertyMetadata(null));

        /// <summary>정렬된 열 머리에 ▼(큰 값부터)·▲(작은 값부터)를 붙인다.</summary>
        internal void UpdateSortGlyphs()
        {
            foreach (var c in _grid.Columns)
            {
                var name = (string)c.GetValue(BaseHeaderProperty);
                c.Header = c.SortDirection == ListSortDirection.Descending ? name + " ▼"
                         : c.SortDirection == ListSortDirection.Ascending ? name + " ▲" : name;
            }
        }

        static readonly DependencyProperty DescendingFirstProperty =
            DependencyProperty.RegisterAttached("DescendingFirst", typeof(bool), typeof(ProcessWindow), new PropertyMetadata(false));

        private void Column(string header, string path, string sortPath = null, bool right = false, bool descending = false, bool mono = false)
        {
            var col = new DataGridTextColumn { Header = header, Binding = new Binding(path), SortMemberPath = sortPath ?? path };
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 2, 6, 2)));
            if (right) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            if (mono) style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Consolas")));
            col.ElementStyle = style;
            col.SetValue(DescendingFirstProperty, descending);
            col.SetValue(BaseHeaderProperty, header);
            _grid.Columns.Add(col);
        }

        private bool Matches(object o)
        {
            var q = _filter.Text.Trim();
            if (q.Length == 0 || !(o is ProcessRow r)) return true;
            return r.Pid.ToString() == q
                || (r.User ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.Container ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.Command ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task TakeAsync()
        {
            _retake.IsEnabled = false;
            _summary.Text = "찍는 중… (CPU 사용률을 재느라 1초 남짓 걸립니다)";
            try
            {
                var r = await _ssh.RunAsync(os => ProcessScript.For(os), Timeout);
                var snap = ProcessScript.Parse(r.Output + (string.IsNullOrWhiteSpace(r.Error) ? "" : "\n" + r.Error), DateTime.Now);
                Show(snap);
            }
            catch (ObjectDisposedException)
            {
                _summary.Text = "연결이 닫혔습니다 — 모니터 창에서 다시 연결한 뒤 이 창을 새로 여세요";
            }
            catch (Exception ex)
            {
                _summary.Text = "실패: " + ex.Message;
            }
            finally
            {
                _retake.IsEnabled = true;
            }
        }

        /// <summary>스냅샷을 표에 넣는다(시험에서 바로 부를 수 있게 분리). 정렬·거르기는 그대로 둔다.</summary>
        internal void Show(ProcessSnapshot snap)
        {
            var names = _containerNames?.Invoke() ?? new Dictionary<string, string>();
            // 이름을 모르면(표에 없는 컨테이너 — pod 의 infra 등) id 앞 12자리. 앞부분 일치도 같은 것으로 본다(짧은 id)
            string NameOf(string id)
            {
                if (string.IsNullOrEmpty(id)) return "";
                if (names.TryGetValue(id, out var n)) return n;
                var hit = names.FirstOrDefault(kv => kv.Key.Length > 0 && (id.StartsWith(kv.Key) || kv.Key.StartsWith(id)));
                return hit.Value ?? id.Substring(0, Math.Min(12, id.Length));
            }
            _snap = snap;
            _rows = snap.Processes.Select(p => new ProcessRow(p, NameOf(p.ContainerId))).ToList();

            var old = _grid.ItemsSource == null ? null : CollectionViewSource.GetDefaultView(_grid.ItemsSource);
            var sorts = old?.SortDescriptions.ToList() ?? new List<SortDescription> { new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending) };
            _grid.ItemsSource = _rows;
            var view = CollectionViewSource.GetDefaultView(_rows);
            view.Filter = Matches;
            foreach (var s in sorts) view.SortDescriptions.Add(s);
            foreach (var c in _grid.Columns)
                c.SortDirection = sorts.Where(s => s.PropertyName == c.SortMemberPath).Select(s => (ListSortDirection?)s.Direction).FirstOrDefault();
            UpdateSortGlyphs();
            _board.Update(snap, _rows);
            if (_showBoard.IsChecked == true) _board.Visibility = Visibility.Visible;

            int inContainer = _rows.Count(r => r.Container.Length > 0);
            var cpuSum = snap.Processes.Sum(p => p.CpuPercent ?? 0);
            _summary.Text = $"{snap.Time:HH:mm:ss} 찍음 · 프로세스 {_rows.Count}개(컨테이너 안 {inContainer}개) · CPU 합계 {cpuSum:0.#}% · {snap.Cores}코어"
                + (snap.MemTotal > 0 ? $" · 메모리 {Format.Bytes(snap.MemTotal)}" : "")
                + " · CPU % 는 호스트 전체 = 100%";
            _save.IsEnabled = true;
        }

        private void Save()
        {
            if (_snap == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "프로세스 스냅샷 저장",
                FileName = $"processes-{Safe(_host)}-{_snap.Time:yyyyMMdd-HHmmss}.tsv",
                Filter = "탭 구분 텍스트 (*.tsv)|*.tsv|모든 파일 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, ToText(_rows), new UTF8Encoding(true)); // BOM: 엑셀에서 한글이 안 깨지게
                _summary.Text = "저장함: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "저장하지 못했습니다: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        static string Safe(string s) => string.Concat((s ?? "host").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>탭 구분 텍스트(머리줄 포함). 숫자는 정렬·계산하기 쉽게 단위 없이.</summary>
        internal static string ToText(IEnumerable<ProcessRow> rows)
        {
            var sb = new StringBuilder("PID\t사용자\tCPU%\t메모리%\t메모리(바이트)\t실행 시간\t컨테이너\t명령\r\n");
            foreach (var r in rows)
                sb.Append(r.Pid).Append('\t').Append(r.User).Append('\t')
                  .Append(r.Cpu < 0 ? "" : r.Cpu.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.Mem < 0 ? "" : r.Mem.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.Rss).Append('\t').Append(r.ElapsedText).Append('\t').Append(r.Container).Append('\t')
                  .Append((r.Command ?? "").Replace('\t', ' ')).Append("\r\n");
            return sb.ToString();
        }
    }
}
