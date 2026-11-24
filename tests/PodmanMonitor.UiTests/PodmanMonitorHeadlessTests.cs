using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PodmanMonitor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

namespace PodmanMonitor.UiTests
{
    /// <summary>
    /// Podman 모니터 화면 시험. Folderss처럼 zip을 불러 Initialize → CreateView를 팝업 창에 담고 확인한다.
    /// 실제 서버에 붙는 시험은 PODMAN_MONITOR_LIVE_HOST(예: ~/.ssh/config 별칭)가 있을 때만 돈다.
    /// </summary>
    public class PodmanMonitorHeadlessTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
        private readonly ITestOutputHelper _out;

        public PodmanMonitorHeadlessTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>호스트 목록 설정을 미리 넣는다(설정 팝업에서 저장한 것과 같은 형식). host 가 null 이면 목록을 비운다.</summary>
        private static void SetHosts(PluginUnderTest plugin, string name, string host)
        {
            plugin.Manager.SetSetting("host", null);
            plugin.Manager.SetSetting("hosts", host == null ? null : "[{\"Name\":\"" + name + "\",\"Host\":\"" + host + "\"}]");
            plugin.Manager.SetSetting("selectedHost", name);
        }

        /// <summary>
        /// 디버깅 진입점: 테스트 탐색기에서 [디버그]로 실행하면 플러그인 창이 화면에 뜨고, 창을 닫을 때까지 기다린다.
        /// PODMAN_MONITOR_LIVE_HOST 를 주면 그 호스트로 바로 연결한다.
        /// </summary>
        [UiFact]
        public async Task OpenView()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var live = Environment.GetEnvironmentVariable("PODMAN_MONITOR_LIVE_HOST");
            SetHosts(plugin, live, live);
            var window = ui.Run(() => plugin.OpenView(1200, 800));
            try
            {
                await ui.WaitUntil(() => window.IsLoaded, Wait, "플러그인 창 표시");
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "open-view")));
                if (HeadlessEnvironment.Debugging)
                    await ui.WaitUntil(() => !Ui.Windows().Contains(window), TimeSpan.FromHours(8), "디버깅 창 닫힘");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => Ui.Close(window));
            }
        }

        /// <summary>등록된 호스트가 없으면 연결하지 않고 안내만 보여 준다. 대시보드가 위, 표가 아래에 있어야 한다.</summary>
        [UiFact]
        public async Task NoHost_ShowsGuideAndLayout()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            SetHosts(plugin, null, null);
            var window = ui.Run(() => plugin.OpenView());
            try
            {
                var status = await ui.WaitFor(() => Ui.ById<TextBlock>(window, "StatusText"), Wait, "상태줄");
                await ui.WaitUntil(() => status.Text.Contains("호스트 설정"), Wait, "호스트 추가 안내");
                ui.Run(() =>
                {
                    foreach (var id in new[] { "HostCombo", "HostSettingsButton", "IntervalBox", "AutoRefreshBox", "RefreshButton", "WindowBox",
                                               "ContainerGrid", "HostCpuChart", "HostMemChart", "ContainerCpuChart", "ContainerMemChart", "DiskList" })
                        Assert.True(Ui.ById<FrameworkElement>(window, id) != null, id + " 없음");
                    Assert.Empty(Ui.ById<ComboBox>(window, "HostCombo").Items);
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    Assert.Equal(11, grid.Columns.Count);
                    Assert.Equal("프로젝트/pod", grid.Columns[1].Header);
                    Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode); // 여러 개 골라 한 번에 조작
                    var chart = Ui.ById<FrameworkElement>(window, "HostCpuChart");
                    var gridTop = grid.TranslatePoint(new Point(0, 0), window).Y;
                    var chartTop = chart.TranslatePoint(new Point(0, 0), window).Y;
                    Assert.True(chartTop < gridTop, $"그래프({chartTop})가 표({gridTop})보다 위에 있어야 합니다");
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => Ui.Close(window));
            }
        }

        /// <summary>호스트 설정 팝업: 잘못된 값은 저장을 막고, 추가한 호스트는 저장 후 콤보에 나타나 선택된다.</summary>
        [UiFact]
        public async Task HostSettings_ValidatesAndSaves()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            SetHosts(plugin, null, null);
            var window = ui.Run(() => plugin.OpenView());
            try
            {
                var open = await ui.WaitFor(() => Ui.ById<Button>(window, "HostSettingsButton"), Wait, "호스트 설정 버튼");
                ui.Post(() => Ui.Click(open)); // 모달 — Run 으로 누르면 닫힐 때까지 멈춘다
                var dlg = await ui.WaitFor(() => Ui.Window("SSH 호스트 설정"), Wait, "호스트 설정 창");

                ui.Run(() => Ui.Click(Ui.ById<Button>(dlg, "AddHostButton")));
                await ui.WaitUntil(() => Ui.ById<ListBox>(dlg, "HostList").Items.Count == 1, Wait, "새 항목");

                // 셸에 쓰일 수 있는 문자는 거부
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(dlg, "HostNameBox").Text = "로컬";
                    Ui.ById<TextBox>(dlg, "HostAddressBox").Text = "kyj; rm -rf /";
                    Ui.Click(Ui.ById<Button>(dlg, "SaveHostsButton"));
                });
                await ui.WaitUntil(() => Ui.ById<TextBlock>(dlg, "HostMessageText").Text.Contains("호스트에는"), Wait, "거부 안내");
                Assert.True(ui.Run(() => dlg.IsVisible));
                Assert.Null(plugin.Manager.GetSetting("hosts"));

                // 올바른 값(닫힌 포트라 연결은 바로 실패한다 — 저장·선택만 본다)
                ui.Run(() =>
                {
                    Ui.ById<TextBox>(dlg, "HostAddressBox").Text = "127.0.0.1";
                    Ui.ById<TextBox>(dlg, "HostPortBox").Text = "1";
                });
                await ui.WaitUntil(() => Ui.ById<TextBlock>(dlg, "ResolvedText").Text.Contains("127.0.0.1:1"), Wait, "접속 정보 미리보기");
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(dlg, plugin.ShotFolder, "host-settings")));
                ui.Run(() => Ui.Click(Ui.ById<Button>(dlg, "SaveHostsButton")));
                await ui.WaitUntil(() => !dlg.IsVisible, Wait, "설정 창 닫힘");

                var combo = ui.Run(() => Ui.ById<ComboBox>(window, "HostCombo"));
                await ui.WaitUntil(() => combo.Items.Count == 1 && combo.SelectedIndex == 0, Wait, "콤보에 새 호스트");
                Assert.Contains("\"로컬\"", plugin.Manager.GetSetting("hosts"));
                Assert.Equal("로컬", plugin.Manager.GetSetting("selectedHost"));
                await ui.WaitUntil(() => Ui.ById<TextBlock>(window, "StatusText").Text.StartsWith("오류"), Wait, "닫힌 포트 연결 실패 표시");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() =>
                {
                    Ui.Close(Ui.Windows().Where(w => w.Title == "SSH 호스트 설정").ToArray());
                    Ui.Close(window);
                });
            }
        }

        /// <summary>Folderss는 실행할 때마다 CreateView를 다시 불러 창을 하나 더 띄운다 — 창마다 새 화면이어야 한다.</summary>
        [UiFact]
        public async Task OpenTwice_EachWindowGetsItsOwnView()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            SetHosts(plugin, null, null);
            Window first = null, second = null;
            try
            {
                first = ui.Run(() => plugin.OpenView());
                second = ui.Run(() => plugin.OpenView());
                await ui.WaitUntil(() => first.IsLoaded && second.IsLoaded, Wait, "두 창 표시");
                ui.Run(() => Assert.NotSame(first.Content, second.Content));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => Ui.Close(first, second));
            }
        }

        /// <summary>테마 7종으로 바꿔 가며 캡처한다. 표 배경은 PanelBackground 를 따라야 한다(어두운 테마에서 흰 표가 되지 않게).</summary>
        [UiFact]
        public async Task Themes_FollowResourceKeys()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            SetHosts(plugin, null, null);
            var window = ui.Run(() => plugin.OpenView());
            try
            {
                var grid = await ui.WaitFor(() => Ui.ById<DataGrid>(window, "ContainerGrid"), Wait, "컨테이너 표");
                foreach (var theme in UiThread.Themes)
                {
                    ui.Run(() =>
                    {
                        plugin.ApplyTheme(theme);
                        var expected = ((SolidColorBrush)Application.Current.FindResource("PanelBackground")).Color;
                        Assert.Equal(expected, ((SolidColorBrush)grid.Background).Color);
                    });
                    _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "theme-" + theme)));
                }
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() =>
                {
                    plugin.ApplyTheme("Black");
                    Ui.Close(window);
                });
            }
        }

        /// <summary>
        /// 실제 서버 시험(PODMAN_MONITOR_LIVE_HOST 가 있을 때만): 컨테이너·디스크가 채워지고, 여러 번 갱신해도 SSH 연결은 한 번만 맺는다.
        /// </summary>
        [UiFact]
        public async Task Live_CollectsAndReusesConnection()
        {
            var live = Environment.GetEnvironmentVariable("PODMAN_MONITOR_LIVE_HOST");
            if (string.IsNullOrEmpty(live))
            {
                _out.WriteLine("PODMAN_MONITOR_LIVE_HOST 가 없어 건너뜀");
                return;
            }
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            SetHosts(plugin, "live-" + live, live);
            plugin.Manager.SetSetting("interval", "2");
            var window = ui.Run(() => plugin.OpenView(1200, 800));
            try
            {
                var status = await ui.WaitFor(() => Ui.ById<TextBlock>(window, "StatusText"), Wait, "상태줄");
                await ui.WaitUntil(() => status.Text.Contains("마지막 갱신") || status.Text.StartsWith("오류"), Wait, "첫 갱신");
                Assert.DoesNotContain("오류", ui.Run(() => status.Text));
                Assert.Equal("live-" + live, ui.Run(() => Ui.ById<ComboBox>(window, "HostCombo").Text));

                // 2초 주기로 세 번 이상 더 갱신될 때까지 — CPU 는 두 번째 표본부터 나온다
                await Task.Delay(TimeSpan.FromSeconds(8));
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    Assert.NotEmpty(grid.Items);
                    Assert.NotEmpty(Ui.ById<StackPanel>(window, "DiskList").Children);
                    Assert.Contains("연결 1회", status.Text);
                    _out.WriteLine("상태: " + status.Text);
                    _out.WriteLine("요약: " + Ui.ById<TextBlock>(window, "SummaryText").Text);
                    grid.SelectedIndex = 0; // 선택 컨테이너 추이가 그래프에 겹쳐 그려진다
                });
                await Task.Delay(TimeSpan.FromSeconds(3));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "live")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => Ui.Close(window));
            }
        }
    }
}
