using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PodmanMonitor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

namespace PodmanMonitor.UiTests
{
    /// <summary>프로세스 스냅샷: 상단 막대 버튼, 예시 스냅샷을 창에 넣어 정렬·소속 컨테이너·거르기 확인과 캡처(서버 없이).</summary>
    public class ProcessSnapshotUiTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
        private readonly ITestOutputHelper _out;

        public ProcessSnapshotUiTests(ITestOutputHelper output) => _out = output;

        static string Hex(string prefix) => prefix + new string('0', 64 - prefix.Length);

        /// <summary>Linux 스냅샷 출력 예시: 4코어·16GiB, penpot·gitlab 컨테이너의 프로세스와 호스트 프로세스.</summary>
        static string DemoOutput()
        {
            var procs = new (int Pid, string User, int Ticks, long RssKb, long Up, string Cmd, string Container)[]
            {
                (1, "root", 0, 12800, 691200, "/sbin/init", null),
                (812, "root", 3, 98000, 691100, "/usr/bin/dockerd -H fd:// --containerd=/run/containerd/containerd.sock", null),
                (1450, "root", 1, 42000, 691000, "/usr/bin/containerd", null),
                (2101, "999", 38, 420000, 604800, "postgres: checkpointer", "1a2b3c4d5e04"),
                (2102, "999", 12, 160000, 604800, "postgres: walwriter", "1a2b3c4d5e04"),
                (2210, "1001", 95, 1650000, 604700, "java -Xmx2g -jar penpot.jar", "1a2b3c4d5e02"),
                (2290, "1001", 2, 61000, 604700, "node app.js", "1a2b3c4d5e01"),
                (3001, "git", 110, 3900000, 604000, "puma 6.4.2 (unix:///var/opt/gitlab/gitlab-rails/sockets/gitlab.socket) [gitlab-puma-worker]", "2b3c4d5e6f01"),
                (3002, "git", 21, 820000, 604000, "sidekiq 7.1.6 queues:default [0 of 20 busy]", "2b3c4d5e6f01"),
                (4100, "deploy", 0, 5200, 3600, "-bash", null),
                (4200, "root", 0, 0, 120, "[kworker/2:1-events]", null),
            };
            var sb = new StringBuilder("@@PINFO@@ 4 100 16384000 9000\n@@PT1@@\n5000.00\n");
            foreach (var p in procs) sb.Append(p.Pid).Append(" 1000\n");
            sb.Append("@@PT2@@\n5001.00\n");
            foreach (var p in procs) sb.Append(p.Pid).Append(' ').Append(1000 + p.Ticks).Append('\n');
            sb.Append("@@PPS@@\n");
            foreach (var p in procs) sb.Append($"{p.Pid} 1 {p.User} {p.RssKb} {p.Up} {p.Cmd}\n");
            sb.Append("@@PCG@@\n");
            foreach (var p in procs.Where(p => p.Container != null)) sb.Append($"/proc/{p.Pid}/cgroup:{Hex(p.Container)}\n");
            return sb.ToString();
        }

        static int Pid(object row) => (int)row.GetType().GetProperty("Pid").GetValue(row);

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                yield return c;
                foreach (var d in Descendants(c)) yield return d;
            }
        }

        static IEnumerable<string> Texts(DependencyObject root) =>
            Descendants(root).OfType<TextBlock>()
                .Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text) // Inlines 로 넣은 글자까지
                .Where(t => !string.IsNullOrEmpty(t));

        /// <summary>열 머리를 누른 것과 같다(DataGridColumnHeader.OnClick → 정렬). 마우스는 쓰지 않는다.</summary>
        static void ClickHeader(DataGrid grid, string name)
        {
            grid.UpdateLayout();
            var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>()
                .First(h => h.Column != null && ((string)h.Column.Header).TrimEnd('▲', '▼', ' ') == name);
            typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(header, null);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); // ▲▼ 갱신(Background) 기다림
        }

        [UiFact]
        public async Task ProcessSnapshot_ShowsSortsFiltersAndCaptures()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var main = ui.Run(() => plugin.OpenView(1200, 800));
            Window window = null;
            try
            {
                await ui.WaitFor(() => Ui.ById<Button>(main, "ProcessButton"), Wait, "상단 막대 [프로세스 스냅샷]");

                var asm = plugin.Assembly;
                var winType = asm.GetType("PodmanMonitor.Ui.ProcessWindow", true);
                var names = new Dictionary<string, string>
                {
                    ["1a2b3c4d5e04"] = "penpot-postgres-1", ["1a2b3c4d5e02"] = "penpot-backend-1",
                    ["1a2b3c4d5e01"] = "penpot-frontend-1", ["2b3c4d5e6f01"] = "gitlab",
                };
                window = ui.Run(() =>
                {
                    var w = (Window)Activator.CreateInstance(winType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { null, null, "demo", new Func<IReadOnlyDictionary<string, string>>(() => names) }, null);
                    w.Left = 0; w.Top = 0; w.WindowStartupLocation = WindowStartupLocation.Manual; w.ShowActivated = false;
                    w.Show();
                    return w;
                });
                await Task.Delay(500); // 창이 뜨며 찍기를 시도한다(연결이 없어 실패) — 그 뒤에 예시를 넣는다

                ui.Run(() =>
                {
                    var parse = asm.GetType("PodmanMonitor.Monitoring.ProcessScript", true).GetMethod("Parse");
                    var snap = parse.Invoke(null, new object[] { DemoOutput(), DateTime.Now });
                    winType.GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new[] { snap });

                    var grid = Ui.ById<DataGrid>(window, "ProcessGrid");
                    Assert.Equal(11, grid.Items.Count);
                    var first = grid.Items[0];
                    Assert.Equal("gitlab", first.GetType().GetProperty("Container").GetValue(first)); // CPU 큰 것부터
                    Assert.Contains("penpot-postgres-1", grid.Items.OfType<object>().Select(r => (string)r.GetType().GetProperty("Container").GetValue(r)));
                    Assert.Contains("찍음", Ui.ById<TextBlock>(window, "ProcessSummary").Text);
                });
                await Task.Delay(300);
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ProcessGrid");
                    Assert.Equal("CPU ▼", grid.Columns[2].Header);                      // 처음은 CPU 큰 값부터, 표시가 보인다
                    Assert.True(Ui.ById<FrameworkElement>(window, "BoardContainers").IsVisible, "보드");
                    var board = string.Join(" | ", Texts(Ui.ById<FrameworkElement>(window, "BoardContainers")));
                    _out.WriteLine("컨테이너별: " + board);
                    Assert.StartsWith("gitlab (2)", board);                              // CPU 합계가 가장 큰 컨테이너
                    Assert.Contains("(호스트) (", board);
                    Assert.Contains("gitlab", string.Join(" ", Texts(Ui.ById<FrameworkElement>(window, "BoardSummary"))));
                });
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, "process-snapshot")));

                // 열 머리를 실제로 눌러 정렬(머리의 OnClick) — PID 는 작은 값부터, 다시 누르면 큰 값부터
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ProcessGrid");
                    ClickHeader(grid, "PID");
                    Assert.Equal("PID ▲", grid.Columns[0].Header);
                    Assert.Equal("CPU", grid.Columns[2].Header);
                    Assert.Equal(1, Pid(grid.Items[0]));
                    ClickHeader(grid, "PID");
                    Assert.Equal("PID ▼", grid.Columns[0].Header);
                    Assert.Equal(4200, Pid(grid.Items[0]));
                    ClickHeader(grid, "메모리");                                         // 숫자 열은 처음 누를 때 큰 값부터
                    Assert.Equal("메모리 ▼", grid.Columns[4].Header);
                    Assert.Equal(3001, Pid(grid.Items[0]));
                });

                // 보드의 컨테이너 줄을 누르면 표가 그 컨테이너로 걸러진다
                ui.Run(() =>
                {
                    var row = Descendants(Ui.ById<FrameworkElement>(window, "BoardContainers")).OfType<Grid>()
                        .First(g => g.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("penpot-postgres-1")));
                    row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    Assert.Equal("penpot-postgres-1", Ui.ById<TextBox>(window, "ProcessFilter").Text);
                    Assert.Equal(2, Ui.ById<DataGrid>(window, "ProcessGrid").Items.Count);
                });
            }
            finally
            {
                ui.Run(() => { window?.Close(); main.Close(); });
            }
        }
    }
}
