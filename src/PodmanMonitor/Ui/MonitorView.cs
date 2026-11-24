using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;
using Folderss.Plugins;

namespace PodmanMonitor.Ui
{
    /// <summary>
    /// 플러그인 팝업 화면. 창마다 SSH 연결 하나를 유지하고 주기적으로 상태를 모은다.
    /// 창이 닫히면(Unloaded) 타이머를 멈추고 연결을 끊는다.
    /// </summary>
    public sealed class MonitorView : DockPanel
    {
        static readonly Color HostCpuColor = Color.FromRgb(214, 39, 40);
        static readonly Color IoWaitColor = Color.FromRgb(255, 127, 14);
        static readonly Color HostMemColor = Color.FromRgb(44, 160, 44);
        static readonly Color TotalColor = Color.FromRgb(150, 150, 150);
        static readonly Color SelectedColor = Color.FromRgb(31, 119, 180);

        private readonly IPluginManager _manager;
        private readonly ComboBox _hostCombo = new ComboBox { Width = 180, DisplayMemberPath = nameof(HostEntry.Name) };
        private System.Collections.Generic.List<HostEntry> _hosts = new System.Collections.Generic.List<HostEntry>();
        private bool _fillingHosts;
        private readonly ComboBox _interval = new ComboBox { Width = 70 };
        private readonly CheckBox _auto = new CheckBox { Content = "자동 갱신", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _window = new ComboBox { Width = 70 };
        private readonly DataGrid _grid = new DataGrid();
        private readonly TrendChart _hostCpuChart = new TrendChart { Title = "호스트 CPU %" };
        private readonly TrendChart _hostMemChart = new TrendChart { Title = "호스트 메모리 %" };
        private readonly TrendChart _cpuChart = new TrendChart { Title = "컨테이너 CPU % (호스트 전체=100%)" };
        private readonly TrendChart _memChart = new TrendChart { Title = "컨테이너 메모리 %" };
        private readonly StackPanel _disks = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        private readonly TextBlock _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(12, 0, 0, 0) };
        private readonly ObservableCollection<ContainerRow> _rows = new ObservableCollection<ContainerRow>();
        private readonly ProjectGroups _groups = new ProjectGroups();
        private readonly CheckBox _groupBox = new CheckBox { Content = "프로젝트별 묶기", VerticalAlignment = VerticalAlignment.Center };
        private Snapshot _lastSnap;
        private readonly Border _alertBar = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 4), Padding = new Thickness(8, 3, 4, 3), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        private readonly TextBlock _alertText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button _alertList = new Button { Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        private readonly System.Collections.Generic.List<Alert> _alerts = new System.Collections.Generic.List<Alert>();
        /// <summary>사용자가 방금 조작한 컨테이너 id → 조작 시각. 잠시 동안 알림에서 뺀다.</summary>
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _actedOn = new System.Collections.Generic.Dictionary<string, DateTime>();
        const int MaxAlerts = 200;
        static readonly TimeSpan ActedOnQuiet = TimeSpan.FromMinutes(3);
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        private SshConnection _ssh;
        private Collector _collector = new Collector();
        private string _selected;
        private SshConnection _busyWith;

        public MonitorView(IPluginManager manager)
        {
            _manager = manager;
            LastChildFill = true;
            SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            TextElement.SetFontFamily(this, new FontFamily("Segoe UI"));
            SetResourceReference(TextElement.FontFamilyProperty, "AppFontFamily");
            SetResourceReference(TextElement.ForegroundProperty, "PrimaryText");

            Children.Add(BuildToolbar());
            Children.Add(BuildAlertBar());
            Children.Add(BuildStatusBar());
            Children.Add(BuildBody());

            _timer.Tick += async (s, e) => await RefreshAsync();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ---------------- 화면 구성 ----------------

        private UIElement BuildToolbar()
        {
            var bar = new WrapPanel { Margin = new Thickness(8, 8, 8, 4) };
            SetDock(bar, Dock.Top);

            Label(bar, "SSH 호스트");
            _hostCombo.ToolTip = "호스트 설정에서 등록한 서버. 고르면 바로 연결합니다.";
            _hostCombo.SelectionChanged += async (s, e) => { if (!_fillingHosts) await ConnectAsync(); };
            Id(_hostCombo, "HostCombo");
            bar.Children.Add(_hostCombo);

            var settings = new Button { Content = "호스트 설정…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(4, 0, 0, 0) };
            settings.Click += async (s, e) => await OpenHostSettingsAsync();
            Id(settings, "HostSettingsButton");
            bar.Children.Add(settings);

            Label(bar, "주기", 16);
            foreach (var sec in new[] { 2, 3, 5, 10, 30 }) _interval.Items.Add(sec + "초");
            _interval.SelectedItem = (Setting("interval") ?? "3") + "초";
            if (_interval.SelectedIndex < 0) _interval.SelectedIndex = 1;
            _interval.SelectionChanged += (s, e) =>
            {
                _timer.Interval = TimeSpan.FromSeconds(IntervalSeconds);
                SaveSetting("interval", IntervalSeconds.ToString());
            };
            Id(_interval, "IntervalBox");
            bar.Children.Add(_interval);

            _auto.Margin = new Thickness(12, 0, 0, 0);
            _auto.Click += (s, e) => UpdateTimer();
            Id(_auto, "AutoRefreshBox");
            bar.Children.Add(_auto);

            var refresh = new Button { Content = "새로고침", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 0, 0, 0) };
            refresh.Click += async (s, e) => await RefreshAsync();
            Id(refresh, "RefreshButton");
            bar.Children.Add(refresh);

            _groupBox.Margin = new Thickness(12, 0, 0, 0);
            _groupBox.ToolTip = "compose 프로젝트별로 묶어 보여 줍니다. 머리줄을 누르면 접고 펍니다.";
            _groupBox.IsChecked = Setting("groupByProject") != "0";
            _groupBox.Click += (s, e) =>
            {
                SaveSetting("groupByProject", _groupBox.IsChecked == true ? "1" : "0");
                ApplyGrouping();
            };
            Id(_groupBox, "GroupByProjectBox");
            bar.Children.Add(_groupBox);

            Label(bar, "그래프 구간", 16);
            foreach (var m in new[] { 5, 10, 30, 60 }) _window.Items.Add(m + "분");
            _window.SelectedIndex = 1;
            _window.SelectionChanged += (s, e) =>
            {
                var span = TimeSpan.FromMinutes(int.Parse(((string)_window.SelectedItem).TrimEnd('분')));
                foreach (var c in new[] { _hostCpuChart, _hostMemChart, _cpuChart, _memChart }) c.Window = span;
                UpdateCharts();
            };
            Id(_window, "WindowBox");
            bar.Children.Add(_window);
            return bar;
        }

        /// <summary>도구 막대 아래 알림 줄: 가장 최근 알림 + [기록 (n)] [지우기]. 알림이 없으면 숨긴다.</summary>
        private UIElement BuildAlertBar()
        {
            SetDock(_alertBar, Dock.Top);
            Id(_alertBar, "AlertBar");
            Id(_alertText, "AlertText");
            Id(_alertList, "AlertListButton");
            _alertText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
            _alertBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var panel = new DockPanel();
            var clear = new Button { Content = "지우기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
            clear.Click += (s, e) => { _alerts.Clear(); ShowAlerts(); };
            Id(clear, "AlertClearButton");
            _alertList.Click += (s, e) =>
            {
                try
                {
                    var text = string.Join("\n", Enumerable.Reverse(_alerts).Select(a => a.ToString()));
                    LogWindow.Show(Window.GetWindow(this), "알림", text, "Docker 모니터 - 알림 기록");
                }
                catch (Exception ex) { _status.Text = "알림 기록 열기 실패: " + ex.Message; }
            };
            SetDock(clear, Dock.Right);
            SetDock(_alertList, Dock.Right);
            panel.Children.Add(clear);
            panel.Children.Add(_alertList);
            panel.Children.Add(_alertText);
            _alertBar.Child = panel;
            return _alertBar;
        }

        private void AddAlerts(System.Collections.Generic.List<Alert> alerts)
        {
            if (alerts.Count == 0) return;
            _alerts.AddRange(alerts);
            if (_alerts.Count > MaxAlerts) _alerts.RemoveRange(0, _alerts.Count - MaxAlerts);
            ShowAlerts();
            if (alerts.Any(a => a.Level != AlertLevel.Info)) WindowFlash.IfInactive(Window.GetWindow(this));
        }

        private void ShowAlerts()
        {
            if (_alerts.Count == 0) { _alertBar.Visibility = Visibility.Collapsed; return; }
            var last = _alerts[_alerts.Count - 1];
            _alertBar.Visibility = Visibility.Visible;
            _alertText.Text = last.ToString();
            _alertList.Content = $"기록 ({_alerts.Count})";
            // 아직 남아 있는 알림 중 가장 심각한 것으로 색을 정한다
            var worst = _alerts.Max(a => a.Level);
            var color = worst == AlertLevel.Critical ? Color.FromArgb(70, 214, 39, 40)
                      : worst == AlertLevel.Warning ? Color.FromArgb(70, 255, 140, 0)
                      : Color.FromArgb(50, 31, 119, 180);
            _alertBar.Background = new SolidColorBrush(color);
        }

        private System.Collections.Generic.HashSet<string> RecentlyActedOn()
        {
            var now = DateTime.Now;
            foreach (var id in _actedOn.Where(kv => now - kv.Value > ActedOnQuiet).Select(kv => kv.Key).ToList()) _actedOn.Remove(id);
            return new System.Collections.Generic.HashSet<string>(_actedOn.Keys);
        }

        private UIElement BuildStatusBar()
        {
            var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 6) };
            SetDock(bar, Dock.Bottom);
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            Id(_status, "StatusText");
            Id(_summary, "SummaryText");
            SetDock(_summary, Dock.Right);
            bar.Children.Add(_summary);
            bar.Children.Add(_status);
            return bar;
        }

        private UIElement BuildBody()
        {
            // 위: 대시보드(그래프·디스크), 아래: 컨테이너 표. 가운데 막대를 끌어 높이를 바꾼다
            var body = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star), MinHeight = 160 });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });

            BuildGrid();
            Grid.SetRow(_grid, 2);
            body.Children.Add(_grid);

            var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetRow(splitter, 1);
            body.Children.Add(splitter);

            var charts = new Grid();
            Grid.SetRow(charts, 0);
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24, GridUnitType.Star), MinWidth = 180 });
            charts.RowDefinitions.Add(new RowDefinition());
            charts.RowDefinitions.Add(new RowDefinition());
            AddCard(charts, _hostCpuChart, 0, 0, "HostCpuChart");
            AddCard(charts, _hostMemChart, 0, 1, "HostMemChart");
            AddCard(charts, _cpuChart, 1, 0, "ContainerCpuChart");
            AddCard(charts, _memChart, 1, 1, "ContainerMemChart");

            var diskTitle = new TextBlock { Text = "디스크 사용량", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 2) };
            var diskPanel = new DockPanel();
            DockPanel.SetDock(diskTitle, Dock.Top);
            diskPanel.Children.Add(diskTitle);
            diskPanel.Children.Add(new ScrollViewer { Content = _disks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Id(_disks, "DiskList");
            var diskCard = Card(diskPanel);
            Grid.SetColumn(diskCard, 2);
            Grid.SetRowSpan(diskCard, 2);
            charts.Children.Add(diskCard);
            body.Children.Add(charts);
            return body;
        }

        private void BuildGrid()
        {
            Id(_grid, "ContainerGrid");
            _grid.ItemsSource = _rows;
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.CanUserAddRows = false;
            _grid.SelectionMode = DataGridSelectionMode.Extended; // Ctrl·Shift 로 여러 개 골라 한 번에 조작
            _grid.GroupStyle.Add(_groups.CreateStyle());
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(_grid, true);
            ApplyGrouping();
            _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.BorderThickness = new Thickness(1);
            // Folderss 테마에는 DataGrid 스타일이 없어서 직접 연결해야 어두운 테마에서 흰 배경이 되지 않는다
            _grid.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "PanelBackground");
            _grid.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");
            _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");

            var header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SurfaceBackground")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            _grid.ColumnHeaderStyle = header;

            var cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            cell.Setters.Add(new Setter(Control.TemplateProperty, CellTemplate()));
            var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SelectionBrush")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
            cell.Triggers.Add(selected);
            _grid.CellStyle = cell;

            var row = new Style(typeof(DataGridRow));
            row.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PanelBackground")));
            var unhealthy = new DataTrigger { Binding = new Binding(nameof(ContainerRow.Unhealthy)), Value = true };
            unhealthy.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(70, 214, 39, 40))));
            row.Triggers.Add(unhealthy);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("RowHoverBrush")));
            row.Triggers.Add(hover);
            _grid.RowStyle = row;

            Column("이름", nameof(ContainerRow.Name));
            Column("프로젝트", nameof(ContainerRow.Project));
            Column("상태", nameof(ContainerRow.State), StateStyle());
            Column("상세", nameof(ContainerRow.Status));
            Column("CPU", nameof(ContainerRow.Cpu), HotStyle(nameof(ContainerRow.CpuHot)));
            Column("메모리", nameof(ContainerRow.Memory));
            Column("메모리 %", nameof(ContainerRow.MemPercent), HotStyle(nameof(ContainerRow.MemHot)));
            Column("네트워크 수신/송신", nameof(ContainerRow.Net));
            Column("디스크 읽기/쓰기", nameof(ContainerRow.Block));
            Column("이미지", nameof(ContainerRow.Image));
            Column("포트", nameof(ContainerRow.Ports));

            _grid.SelectionChanged += (s, e) =>
            {
                _selected = (_grid.SelectedItem as ContainerRow)?.Name;
                UpdateCharts();
            };
            _grid.MouseDoubleClick += async (s, e) =>
            {
                if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow)
                    await ShowLogsAsync();
            };

            // 메뉴는 열 때마다 고른 줄 수·compose 프로젝트에 맞춰 다시 만든다
            var menu = new ContextMenu();
            _grid.ContextMenu = menu;
            _grid.ContextMenuOpening += (s, e) =>
            {
                if (SelectedRows().Count == 0) e.Handled = true; // 빈 곳 우클릭
            };
            menu.Opened += (s, e) =>
            {
                menu.Items.Clear();
                var rows = SelectedRows();
                if (rows.Count == 0) { menu.IsOpen = false; return; }
                BuildContainerMenu(menu, rows);
            };
            _grid.PreviewMouseRightButtonDown += (s, e) =>
            {
                // 이미 고른 줄을 우클릭하면 선택을 그대로 두고, 아니면 그 줄만 고른다
                if (ItemsControl.ContainerFromElement(_grid, e.OriginalSource as DependencyObject) is DataGridRow r && !r.IsSelected)
                {
                    _grid.SelectedItems.Clear();
                    r.IsSelected = true;
                }
            };
        }

        /// <summary>기본 셀 템플릿은 패딩을 무시하므로 Border+ContentPresenter 로 바꾼다.</summary>
        private static ControlTemplate CellTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            return new ControlTemplate(typeof(DataGridCell)) { VisualTree = border };
        }

        private static Style StateStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(255, 140, 0))));
            foreach (var (state, color) in new[]
            {
                ("running", Color.FromRgb(46, 170, 70)), ("exited", Color.FromRgb(214, 39, 40)), ("dead", Color.FromRgb(214, 39, 40)),
            })
            {
                var t = new DataTrigger { Binding = new Binding(nameof(ContainerRow.State)), Value = state };
                t.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(color)));
                style.Triggers.Add(t);
            }
            return style;
        }

        private static Style HotStyle(string flag)
        {
            var style = new Style(typeof(TextBlock));
            var t = new DataTrigger { Binding = new Binding(flag), Value = true };
            t.Setters.Add(new Setter(TextBlock.BackgroundProperty, new SolidColorBrush(Color.FromArgb(120, 255, 99, 71))));
            t.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            style.Triggers.Add(t);
            return style;
        }

        private void Column(string header, string path, Style element = null)
        {
            var col = new DataGridTextColumn { Header = header, Binding = new Binding(path) };
            if (element != null) col.ElementStyle = element;
            _grid.Columns.Add(col);
        }

        private System.Collections.Generic.List<ContainerRow> SelectedRows() =>
            _grid.SelectedItems.OfType<ContainerRow>().ToList();

        private static readonly (string Action, string Label)[] ContainerActions =
            { ("start", "시작"), ("stop", "중지"), ("restart", "재시작") };

        private void BuildContainerMenu(ContextMenu menu, System.Collections.Generic.List<ContainerRow> rows)
        {
            if (rows.Count == 1)
            {
                menu.Items.Add(MenuItem("로그 보기 (최근 200줄)", async () => await ShowLogsAsync()));
                menu.Items.Add(new Separator());
            }
            var suffix = rows.Count == 1 ? "" : $" — 고른 {rows.Count}개";
            foreach (var (action, label) in ContainerActions)
                menu.Items.Add(MenuItem($"{label} ({action}){suffix}", async () => await ContainerActionAsync(rows, action, label)));

            // 고른 줄이 모두 같은 compose 프로젝트면 그 프로젝트 전체 조작을 보탠다
            var project = rows[0].Project;
            if (string.IsNullOrEmpty(project) || rows.Any(r => r.Project != project)) return;
            var members = _rows.Where(r => r.Project == project).ToList();
            if (members.Count <= rows.Count) return; // 이미 전부 고름
            menu.Items.Add(new Separator());
            foreach (var (action, label) in ContainerActions)
                menu.Items.Add(MenuItem($"'{project}' 프로젝트 전체 {label} ({members.Count}개)", async () => await ContainerActionAsync(members, action, label)));
        }

        private static MenuItem MenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => onClick();
            return item;
        }

        private static void AddCard(Grid grid, TrendChart chart, int row, int col, string id)
        {
            Id(chart, id);
            var card = Card(chart);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        private static Border Card(UIElement child)
        {
            var b = new Border { Child = child, Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            b.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return b;
        }

        private static void Label(Panel bar, string text, double left = 0) =>
            bar.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) });

        private static void Id(UIElement e, string id) => AutomationProperties.SetAutomationId(e, id);

        // ---------------- 동작 ----------------

        private int IntervalSeconds => int.Parse(((string)_interval.SelectedItem ?? "3초").TrimEnd('초'));

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _timer.Interval = TimeSpan.FromSeconds(IntervalSeconds);
                FillHosts(Setting(HostStore.SelectedKey));
                if (_hostCombo.SelectedItem != null) await ConnectAsync();
                else ShowNoHostGuide();
            }
            catch (Exception ex)
            {
                _status.Text = "오류: " + ex.Message; // async void — 예외를 밖으로 내보내면 Folderss 가 종료될 수 있다
            }
        }

        private void ShowNoHostGuide() =>
            _status.Text = "등록된 SSH 호스트가 없습니다. [호스트 설정…]에서 서버를 추가하세요.";

        /// <summary>설정에서 호스트 목록을 읽어 콤보에 채운다(이때는 연결하지 않음).</summary>
        private void FillHosts(string select)
        {
            _fillingHosts = true;
            try
            {
                _hosts = HostStore.Load(Setting);
                _hostCombo.ItemsSource = _hosts;
                _hostCombo.SelectedItem = _hosts.FirstOrDefault(h => h.Name == select) ?? _hosts.FirstOrDefault();
            }
            finally
            {
                _fillingHosts = false;
            }
        }

        private async Task OpenHostSettingsAsync()
        {
            try
            {
                var current = _hostCombo.SelectedItem as HostEntry;
                var dlg = new HostSettingsWindow(_hosts, current?.Name) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true) return;

                HostStore.Save(SaveSetting, dlg.Result);
                FillHosts(current?.Name);
                var now = _hostCombo.SelectedItem as HostEntry;
                // 연결 중인 호스트의 설정이 바뀌었거나 지워졌으면 다시 연결한다
                if (now == null) { Disconnect(); ShowNoHostGuide(); }
                else if (current == null || now.Name != current.Name || HostStore.Serialize(new[] { now }) != HostStore.Serialize(new[] { current }))
                    await ConnectAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "호스트 설정 오류: " + Describe(ex);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _ssh?.Dispose();
            _ssh = null;
        }

        /// <summary>콤보에서 고른 호스트로 새로 연결한다. 이전 서버의 표·그래프·요약은 모두 지운다.</summary>
        private async Task ConnectAsync()
        {
            try
            {
                Disconnect();
                if (!(_hostCombo.SelectedItem is HostEntry entry)) { ShowNoHostGuide(); return; }
                var error = entry.Validate();
                if (error != null) { _status.Text = $"[{entry.Name}] {error} — [호스트 설정…]에서 고치세요."; return; }
                _ssh = new SshConnection(entry);
                SaveSetting(HostStore.SelectedKey, entry.Name);
                await RefreshAsync();
                UpdateTimer();
            }
            catch (Exception ex)
            {
                _status.Text = "연결 오류: " + Describe(ex);
            }
        }

        private void Disconnect()
        {
            _timer.Stop();
            _ssh?.Dispose();
            _ssh = null;
            _collector = new Collector();
            _rows.Clear();
            _groups.Clear();
            _lastSnap = null;
            _alerts.Clear();
            _actedOn.Clear();
            ShowAlerts();
            _disks.Children.Clear();
            _summary.Text = "";
            _selected = null;
            UpdateCharts();
        }

        private void UpdateTimer()
        {
            if (_auto.IsChecked == true && _ssh != null) _timer.Start();
            else _timer.Stop();
        }

        /// <summary>한 번 갱신. 모든 예외는 여기서 잡아 상태줄에 보여 준다(타이머·이벤트에서 부르므로).</summary>
        internal async Task RefreshAsync()
        {
            var ssh = _ssh;
            if (ssh == null || _busyWith == ssh) return; // 같은 연결로 이미 조회 중(호스트를 바꾸면 바로 새로 조회)
            _busyWith = ssh;
            try
            {
                if (!ssh.IsConnected) _status.Text = $"{ssh.Alias} 연결 중...";
                var snap = await _collector.CollectAsync(ssh);
                if (ssh != _ssh) return; // 그사이 다른 호스트로 바뀜
                ShowSnapshot(snap);
                _status.Text = $"{ssh.Alias} ({ssh.Target}) · 마지막 갱신 {snap.Time:HH:mm:ss} · 조회 {snap.ElapsedMs}ms · 연결 {ssh.ConnectCount}회";
            }
            catch (Exception ex)
            {
                if (ssh == _ssh) _status.Text = "오류: " + Describe(ex);
            }
            finally
            {
                if (_busyWith == ssh) _busyWith = null;
            }
        }

        /// <summary>표본 하나를 화면에 반영하고 직전 표본과 비교해 알림을 띄운다(화면 시험에서 예시 데이터를 넣을 때도 쓴다).</summary>
        internal void ShowSnapshot(Snapshot snap)
        {
            var prev = _lastSnap;
            Apply(snap);
            AddAlerts(AlertDetector.Detect(prev, snap, RecentlyActedOn()));
        }

        private static string Describe(Exception ex)
        {
            var msg = ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                if (!msg.Contains(inner.Message)) msg += " — " + inner.Message;
            return msg;
        }

        private bool Grouped => _groupBox.IsChecked == true;

        /// <summary>묶기를 켜고 끈다. 줄 순서도 바뀌므로 마지막 표본으로 다시 채운다.</summary>
        private void ApplyGrouping()
        {
            var view = CollectionViewSource.GetDefaultView(_rows);
            view.GroupDescriptions.Clear();
            // 줄을 먼저 묶을 순서(프로젝트 → compose 아님)로 늘어놓은 뒤 묶는다 — 묶음 순서는 묶는 순간의 줄 순서로 정해진다
            if (_lastSnap != null) Apply(_lastSnap);
            if (Grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ContainerRow.Project)));
        }

        private void Apply(Snapshot snap)
        {
            _lastSnap = snap;
            // 표: 같은 이름의 줄은 객체를 유지한 채 값만 갱신. 묶을 때는 프로젝트(compose 아닌 것은 맨 뒤) → 실행 중 → 이름 순
            var ordered = (Grouped
                    ? snap.Containers.OrderBy(c => string.IsNullOrEmpty(c.Project)).ThenBy(c => c.Project, StringComparer.OrdinalIgnoreCase)
                    : snap.Containers.OrderBy(c => 0))
                .ThenBy(c => c.State != "running").ThenBy(c => c.Name).ToList();
            _groups.Update(snap.Containers);
            for (int i = 0; i < ordered.Count; i++)
            {
                var existing = _rows.FirstOrDefault(r => r.Name == ordered[i].Name);
                if (existing != null && existing.Project != (ordered[i].Project ?? ""))
                {
                    _rows.Remove(existing); // 프로젝트가 바뀌면(다시 만든 컨테이너) 묶음을 새로 정하게 한다
                    existing = null;
                }
                if (existing == null)
                {
                    // 값을 채운 뒤 넣는다 — 묶음(프로젝트)은 넣는 순간의 값으로 정해진다
                    existing = new ContainerRow();
                    existing.Update(ordered[i]);
                    _rows.Insert(Math.Min(i, _rows.Count), existing);
                }
                else
                {
                    int at = _rows.IndexOf(existing);
                    if (at != i) _rows.Move(at, Math.Min(i, _rows.Count - 1));
                }
                existing.Update(ordered[i]);
            }
            while (_rows.Count > ordered.Count) _rows.RemoveAt(_rows.Count - 1);

            // 디스크
            _disks.Children.Clear();
            foreach (var d in snap.Disks) _disks.Children.Add(DiskItem(d));

            // 상태줄 요약
            var h = snap.Host;
            var parts = new System.Collections.Generic.List<string>();
            if (h.CpuPercent.HasValue) parts.Add($"호스트 CPU {h.CpuPercent:0.#}%");
            if (h.MemTotal > 0) parts.Add($"메모리 {h.MemPercent:0.#}% ({Format.Bytes(h.MemTotal - h.MemAvailable)}/{Format.Bytes(h.MemTotal)})");
            if (!string.IsNullOrEmpty(h.LoadAverage)) parts.Add($"load {h.LoadAverage} ({h.Cores}코어)");
            parts.Add($"컨테이너 실행 {snap.Containers.Count(c => c.State == "running")} / 전체 {snap.Containers.Count}");
            _summary.Text = string.Join(" · ", parts);

            UpdateCharts();
        }

        private static UIElement DiskItem(DiskInfo d)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            var head = new DockPanel();
            var dev = new TextBlock { Text = d.Device, FontSize = 11 };
            dev.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            DockPanel.SetDock(dev, Dock.Right);
            head.Children.Add(dev);
            head.Children.Add(new TextBlock { Text = d.Mount, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(head);

            var color = d.Percent >= 90 ? Color.FromRgb(214, 39, 40) : d.Percent >= 75 ? Color.FromRgb(255, 140, 0) : HostMemColor;
            var bar = new ProgressBar { Height = 12, Maximum = 100, Value = d.Percent, Margin = new Thickness(0, 3, 0, 3), Foreground = new SolidColorBrush(color) };
            bar.SetResourceReference(ProgressBar.BackgroundProperty, "ControlBackground");
            panel.Children.Add(bar);

            var detail = new TextBlock
            {
                Text = $"{d.Percent:0.#}% 사용 · {Format.Bytes(d.Used)} / {Format.Bytes(d.Size)} · 남은 공간 {Format.Bytes(d.Avail)}",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            panel.Children.Add(detail);
            return panel;
        }

        private void UpdateCharts()
        {
            var c = _collector;
            _hostCpuChart.SetSeries(new[]
            {
                new ChartSeries("사용률", HostCpuColor, c.HostCpu),
                new ChartSeries("iowait", IoWaitColor, c.HostIoWait, 1.5),
            });
            _hostMemChart.SetSeries(new[] { new ChartSeries("사용", HostMemColor, c.HostMem) });

            var cpu = new System.Collections.Generic.List<ChartSeries> { new ChartSeries("전체 합계", TotalColor, c.TotalCpu, 1.5) };
            var mem = new System.Collections.Generic.List<ChartSeries> { new ChartSeries("전체 합계", TotalColor, c.TotalMem, 1.5) };
            if (_selected != null && c.PerContainer.TryGetValue(_selected, out var h))
            {
                cpu.Add(new ChartSeries(_selected, SelectedColor, h.Cpu));
                mem.Add(new ChartSeries(_selected, SelectedColor, h.Mem));
            }
            _cpuChart.SetSeries(cpu);
            _memChart.SetSeries(mem);
        }

        private async Task ContainerActionAsync(System.Collections.Generic.List<ContainerRow> rows, string action, string label)
        {
            try
            {
                var ssh = _ssh;
                if (rows.Count == 0 || ssh == null) return;
                var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count}개";
                // compose 의존 순서: 시작은 의존 대상부터, 중지는 반대로(ComposeOrder)
                var steps = ComposeOrder.Plan(rows.Select(x => x.ToTarget()).ToList(), action);
                var nameOf = rows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
                if (action != "start")
                {
                    if (MessageBox.Show(Window.GetWindow(this), ConfirmText(rows, steps, label), "Docker 모니터", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                        return;
                }
                _status.Text = $"{what} {label} 중...";
                foreach (var x in rows) _actedOn[x.Id] = DateTime.Now; // 스스로 멈춘 것을 알림으로 띄우지 않는다
                // 한 채널에서 차례로 — 조작당 최대 60초, 건강 대기는 최대 HealthWaitSeconds
                int waits = steps.Count(s => s.Action == ComposeOrder.WaitHealthy);
                var timeout = TimeSpan.FromSeconds(30 + 60 * (steps.Count - waits) + RemoteScript.HealthWaitSeconds * waits);
                var r = await ssh.RunAsync(RemoteScript.ContainerSteps(steps), timeout);
                var results = RemoteScript.ParseStepResults(r.Output);

                var failed = new System.Collections.Generic.List<string>();
                for (int i = 0; i < steps.Count; i++)
                {
                    var name = nameOf[steps[i].Id];
                    if (i >= results.Count) { failed.Add($"{name}({StepLabel(steps[i].Action)} 응답 없음)"); break; }
                    var res = results[i].Result;
                    if (!RemoteScript.StepSucceeded(res))
                        failed.Add(res == "timeout" ? $"{name}(healthy 대기 시간 초과)" : $"{name}({StepLabel(steps[i].Action)} HTTP {res})");
                }
                if (rows.Count == 1 && failed.Count == 0 && results.Count == 1 && results[0].Result == "304")
                    _status.Text = $"{what} 이미 그 상태입니다";
                else if (failed.Count == 0)
                    _status.Text = $"{what} {label} 완료";
                else
                    _status.Text = $"{what} {label} 중 실패: " + string.Join(", ", failed) + (string.IsNullOrWhiteSpace(r.Error) ? "" : " — " + r.Error.Trim());
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _status.Text = $"{label} 실패: " + Describe(ex);
            }
        }

        /// <summary>중지·재시작 확인 문구. 여러 개면 실행 순서를 붙인다.</summary>
        internal static string ConfirmText(System.Collections.Generic.List<ContainerRow> rows, System.Collections.Generic.List<ContainerStep> steps, string label)
        {
            if (rows.Count == 1) return $"{rows[0].Name} 컨테이너를 {label}할까요?";
            var nameOf = rows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
            return $"컨테이너 {rows.Count}개를 {label}할까요?\n\n{StepList(steps, nameOf)}";
        }

        /// <summary>화면 시험·문서용: 고른 줄에 그 조작을 하면 보일 확인 문구.</summary>
        internal string PreviewConfirm(string action, string label)
        {
            var rows = SelectedRows();
            return ConfirmText(rows, ComposeOrder.Plan(rows.Select(x => x.ToTarget()).ToList(), action), label);
        }

        private static string StepLabel(string action) =>
            action == "start" ? "시작" : action == "stop" ? "중지" : action == "restart" ? "재시작" : "healthy 대기";

        /// <summary>확인 창에 보일 실행 순서. 길면 앞부분만.</summary>
        private static string StepList(System.Collections.Generic.List<ContainerStep> steps, System.Collections.Generic.Dictionary<string, string> nameOf)
        {
            var lines = steps.Select((s, i) => $"  {i + 1}. {nameOf[s.Id]} {StepLabel(s.Action)}").ToList();
            const int max = 20;
            return "실행 순서:\n" + string.Join("\n", lines.Take(max)) + (lines.Count > max ? $"\n  … 외 {lines.Count - max}단계" : "");
        }

        private async Task ShowLogsAsync()
        {
            try
            {
                if (!(_grid.SelectedItem is ContainerRow row) || _ssh == null) return;
                _status.Text = $"{row.Name} 로그 조회 중...";
                var r = await _ssh.RunAsync(RemoteScript.Logs(row.Id, 200), TimeSpan.FromSeconds(30));
                _status.Text = $"{row.Name} 로그 조회 완료";
                LogWindow.Show(Window.GetWindow(this), row.Name, r.Output);
            }
            catch (Exception ex)
            {
                _status.Text = "로그 조회 실패: " + Describe(ex);
            }
        }

        private string Setting(string key)
        {
            try { return _manager?.GetSetting(key); }
            catch { return null; }
        }

        private void SaveSetting(string key, string value)
        {
            try { _manager?.SetSetting(key, value); }
            catch (Exception ex) { _status.Text = "설정 저장 실패: " + ex.Message; }
        }
    }
}
