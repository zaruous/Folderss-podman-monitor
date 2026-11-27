using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PodmanMonitor.UiTests.Headless;
using Xunit;
using Xunit.Abstractions;

namespace PodmanMonitor.UiTests
{
    /// <summary>
    /// 서버 없이 예시 데이터(가상의 compose 프로젝트들)를 화면에 넣어 묶음·여러 줄 조작 메뉴·알림을 확인한다.
    /// 문서(docs/변경사항)에 쓰는 캡처도 여기서 만든다 — 실제 서버 이름·주소가 그림에 남지 않는다.
    /// </summary>
    public class DemoDataTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
        private readonly ITestOutputHelper _out;

        public DemoDataTests(ITestOutputHelper output) => _out = output;

        [UiFact]
        public async Task DemoData_GroupsMenuAndAlerts()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SetSetting("host", null);
            plugin.Manager.SetSetting("hosts", null);
            plugin.Manager.SetSetting("groupByProject", null); // 기본값(묶기 켬)
            var window = ui.Run(() => plugin.OpenView(1200, 800));
            try
            {
                await ui.WaitFor(() => Ui.ById<TextBlock>(window, "StatusText"), Wait, "상태줄");
                var server = new DemoServer();
                ui.Run(() => Ui.Choose(Ui.ById<ComboBox>(window, "WindowBox"), "5분"));
                const int ticks = 100; // 3초 × 100 = 5분
                var start = DateTime.Now.AddSeconds(-3 * ticks);
                for (int k = 0; k < ticks; k++)
                {
                    var output = server.Output(k);
                    var time = start.AddSeconds(3 * k);
                    ui.Run(() => Push(window, output, time));
                }
                await Task.Delay(500);
                ui.Run(() =>
                {
                    Ui.ById<TextBlock>(window, "StatusText").Text = "demo (deploy@demo-host:22) · 마지막 갱신 " + DateTime.Now.ToString("HH:mm:ss") + " · 조회 184ms · 연결 1회";
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    Assert.Equal(11, grid.Items.Count);
                    // 묶음 머리줄: 프로젝트 3개 + pod 1개 + 둘 다 아님 (pod infra 컨테이너는 줄에 없다)
                    var headers = Ui.Texts(grid).Where(t => t.Contains(" 실행")).ToList();
                    _out.WriteLine("묶음: " + string.Join(" | ", headers));
                    Assert.Equal(5, headers.Count);
                    Assert.Contains(headers, t => t.StartsWith("6/6 실행"));
                    Assert.Contains("storage (pod)", Ui.Texts(grid));
                    Assert.Contains("(compose·pod 아님)", Ui.Texts(grid));
                    Assert.Equal(Visibility.Collapsed, Ui.ById<Border>(window, "AlertBar").Visibility);
                    SelectByName(grid, "penpot-backend-1");
                });
                await Task.Delay(500);
                Shot(ui, window, plugin, "demo-grouped");

                // 묶기 끄기 — 프로젝트 열만 남는다
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "GroupByProjectBox")));
                await Task.Delay(300);
                ui.Run(() => Assert.DoesNotContain(Ui.Texts(Ui.ById<DataGrid>(window, "ContainerGrid")), t => t.StartsWith("6/6 실행")));
                Shot(ui, window, plugin, "demo-flat");
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    SelectByName(grid, "penpot-backend-1", "penpot-postgres-1", "penpot-valkey-1");
                    SnapWithMenu(window, grid, "penpot-postgres-1", plugin.ShotFolder, "demo-menu-flat");
                });
                ui.Run(() => Ui.ClickToggle(Ui.ById<CheckBox>(window, "GroupByProjectBox")));
                await Task.Delay(300);

                // 여러 줄 고르고 우클릭 메뉴
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    SelectByName(grid, "penpot-backend-1");
                    var items = Ui.ContextMenu(grid, null);
                    _out.WriteLine("메뉴(한 줄): " + string.Join(" · ", items));
                    Assert.Contains(items, t => t.StartsWith("'penpot' 프로젝트 전체 중지 (6개)"));
                    Assert.Contains("명령 실행…", items);
                    Assert.Contains("터미널 열기 (ssh 새 창)", items);
                    Assert.Contains("ID 복사", items);
                    SelectByName(grid, "penpot-backend-1", "penpot-postgres-1", "penpot-valkey-1");
                });
                await Task.Delay(200);
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    SnapWithMenu(window, grid, "penpot-valkey-1", plugin.ShotFolder, "demo-menu");
                    var view = window.Content;
                    var confirm = (string)view.GetType().GetMethod("PreviewConfirm", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(view, new object[] { "stop", "중지" });
                    _out.WriteLine("확인 창(고른 3개 중지):\n" + confirm);
                    Assert.Contains("1. penpot-backend-1 중지", confirm);
                    Assert.Contains("3. penpot-postgres-1 중지", confirm);

                    // 프로젝트 전체 재시작 순서
                    SelectByName(grid, grid.Items.OfType<object>().Select(Name).Where(n => n.StartsWith("penpot-")).ToArray());
                    var all = (string)view.GetType().GetMethod("PreviewConfirm", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(view, new object[] { "restart", "재시작" });
                    _out.WriteLine("확인 창(penpot 전체 재시작):\n" + all);
                    File.WriteAllText(Path.Combine(plugin.ShotFolder, "demo-confirm.txt"), confirm + "\n\n---\n\n" + all, new UTF8Encoding(false));
                    Assert.Contains("healthy 대기", all);
                });

                // 상태 변화 → 알림 줄
                ui.Run(() =>
                {
                    server.Incident = true;
                    Push(window, server.Output(ticks), DateTime.Now);
                    var bar = Ui.ById<Border>(window, "AlertBar");
                    Assert.Equal(Visibility.Visible, bar.Visibility);
                    _out.WriteLine("알림: " + Ui.ById<TextBlock>(window, "AlertText").Text);
                    Assert.Equal("기록 (4)", Ui.ContentText(Ui.ById<Button>(window, "AlertListButton").Content));
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    grid.SelectedItems.Clear();
                    // monitoring 묶음을 접어 둔다(머리줄 누르기와 같은 동작)
                    var header = Ui.FindAll<Border>(grid, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ProjectGroupHeader")
                        .First(b => Ui.Texts(b).Contains("monitoring"));
                    header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                });
                await Task.Delay(300);
                ui.Run(() =>
                {
                    var grid = Ui.ById<DataGrid>(window, "ContainerGrid");
                    foreach (var item in grid.Items.OfType<object>().Where(r => Name(r) == "prometheus" || Name(r) == "grafana"))
                        Assert.False((grid.ItemContainerGenerator.ContainerFromItem(item) as UIElement)?.IsVisible == true, Name(item) + " 줄이 접히지 않았습니다");
                });
                Shot(ui, window, plugin, "demo-alert");

                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "AlertListButton")));
                var log = await ui.WaitFor(() => Ui.Windows().FirstOrDefault(w => w.Title == "Podman 모니터 - 알림 기록"), Wait, "알림 기록 창");
                ui.Run(() => { log.Width = 720; log.Height = 220; });
                await Task.Delay(300);
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(log, plugin.ShotFolder, "demo-alert-log")));
                ui.Run(() => Ui.Close(log));

                ui.Run(() => Ui.Click(Ui.ById<Button>(window, "AlertClearButton")));
                await ui.WaitUntil(() => Ui.ById<Border>(window, "AlertBar").Visibility == Visibility.Collapsed, Wait, "지우기 후 알림 줄 숨김");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() => Ui.Close(window));
            }
        }

        private void Shot(UiThread ui, Window window, PluginUnderTest plugin, string name) =>
            _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(window, plugin.ShotFolder, name)));

        /// <summary>플러그인 안의 Collector.Build → MonitorView.ShowSnapshot (갱신 한 번과 같다).</summary>
        private static void Push(Window window, string output, DateTime time)
        {
            var view = window.Content;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var collector = view.GetType().GetField("_collector", flags).GetValue(view);
            var snap = collector.GetType().GetMethod("Build").Invoke(collector, new object[] { output, time });
            view.GetType().GetMethod("ShowSnapshot", flags).Invoke(view, new[] { snap });
        }

        private static string Name(object row) => (string)row.GetType().GetProperty("Name").GetValue(row);

        private static void SelectByName(DataGrid grid, params string[] names)
        {
            grid.SelectedItems.Clear();
            foreach (var item in grid.Items.OfType<object>().Where(r => names.Contains(Name(r)))) grid.SelectedItems.Add(item);
            grid.UpdateLayout();
        }

        /// <summary>창 + 열린 우클릭 메뉴를 한 그림으로(메뉴는 별도 팝업 창이라 창 캡처에 안 잡힌다).</summary>
        private static void SnapWithMenu(Window window, DataGrid grid, string rowName, string folder, string name)
        {
            var menu = grid.ContextMenu;
            menu.PlacementTarget = grid;
            menu.IsOpen = true;
            menu.UpdateLayout();
            var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
            client.UpdateLayout();
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items.OfType<object>().First(r => Name(r) == rowName));
            var at = row.TranslatePoint(new Point(420, row.ActualHeight / 2), client);
            int width = (int)Math.Ceiling(client.ActualWidth), height = (int)Math.Ceiling(client.ActualHeight);
            var menuSize = new Size(menu.ActualWidth, menu.ActualHeight);
            if (at.Y + menuSize.Height > height) at.Y = height - menuSize.Height - 4;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(client) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Rect(at.X + 3, at.Y + 3, menuSize.Width, menuSize.Height)); // 그림자
                dc.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(at, menuSize));
            }
            menu.IsOpen = false;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(stream);
        }

        /// <summary>가상의 서버: compose 프로젝트 penpot(6)·gitlab(1)·monitoring(2) + pod storage(minio, infra 포함) + 둘 다 아닌 것 1개. podman 출력 형식.</summary>
        private sealed class DemoServer
        {
            private sealed class Demo
            {
                public string Id, Name, Image, Project, Service, DependsOn, Ports, Health, Pod = "";
                public bool Infra;
                public bool Running = true;
                public double Cpu, MemMiB;
            }

            private readonly List<Demo> _all = new List<Demo>
            {
                D("1a2b3c4d5e01", "penpot-frontend-1", "penpotapp/frontend:2.4", "penpot", "frontend", "backend:service_started:false,exporter:service_started:false", 9001, 80, null, 0.4, 24),
                D("1a2b3c4d5e02", "penpot-backend-1", "penpotapp/backend:2.4", "penpot", "backend", "postgres:service_healthy:false,valkey:service_healthy:false", 0, 0, "healthy", 6.5, 820),
                D("1a2b3c4d5e03", "penpot-exporter-1", "penpotapp/exporter:2.4", "penpot", "exporter", "valkey:service_healthy:false", 0, 0, null, 0.3, 310),
                D("1a2b3c4d5e04", "penpot-postgres-1", "postgres:15", "penpot", "postgres", "", 0, 0, "healthy", 1.2, 140),
                D("1a2b3c4d5e05", "penpot-valkey-1", "valkey/valkey:8.1", "penpot", "valkey", "", 0, 0, "healthy", 0.6, 9),
                D("1a2b3c4d5e06", "penpot-mailcatch-1", "sj26/mailcatcher:latest", "penpot", "mailcatch", "", 1080, 1080, null, 0.1, 19),
                D("2b3c4d5e6f01", "gitlab", "gitlab/gitlab-ce:17.5.1-ce.0", "gitlab", "gitlab", "", 8929, 80, "healthy", 22.0, 5900),
                D("3c4d5e6f7a01", "prometheus", "prom/prometheus:v2.55.0", "monitoring", "prometheus", "", 9090, 9090, null, 2.1, 380),
                D("3c4d5e6f7a02", "grafana", "grafana/grafana:11.3.0", "monitoring", "grafana", "prometheus:service_started:false", 3000, 3000, "healthy", 0.8, 120),
                D("4d5e6f7a8b01", "minio", "quay.io/minio/minio:latest", "", "", "", 9000, 9000, null, 0.2, 160, pod: "storage"),
                D("4d5e6f7a8b03", "8f1e2d3c4b5a-infra", "", "", "", "", 9000, 9000, null, 0, 1, pod: "storage", infra: true),
                D("4d5e6f7a8b02", "old-backup", "busybox:1.36", "", "", "", 0, 0, null, 0, 0),
            };

            public bool Incident;

            public DemoServer()
            {
                _all.Single(c => c.Name == "old-backup").Running = false;
            }

            private static Demo D(string id, string name, string image, string project, string service, string deps, int pub, int priv, string health, double cpu, double mem,
                                  string pod = "", bool infra = false) =>
                new Demo
                {
                    Id = id, Name = name, Image = image, Project = project, Service = service, DependsOn = deps, Health = health, Cpu = cpu, MemMiB = mem, Pod = pod, Infra = infra,
                    Ports = pub > 0 ? "[{\"host_ip\":\"\",\"container_port\":" + priv + ",\"host_port\":" + pub + ",\"range\":1,\"protocol\":\"tcp\"}]" : "null",
                };

            const long CpuPerTick = 4L * 3 * 1_000_000_000; // 4코어 × 3초(ns) — 호스트 전체가 쓸 수 있는 CPU 시간
            const long TickNano = 3L * 1_000_000_000;
            const long T0 = 1_795_482_000_000_000_000;

            public string Output(int k)
            {
                var sb = new StringBuilder();
                sb.Append("@@PS@@\n[");
                sb.Append(string.Join(",", _all.Select(Json)));
                sb.Append("]\n");
                // podman stats --format '{{json .}}': 실행 중인 것(infra 포함)마다 한 줄
                sb.Append("@@STATS@@\n");
                foreach (var c in _all.Where(Running))
                {
                    long cpuNano = (long)(Enumerable.Range(0, k + 1).Sum(i => c.Cpu / 100 * (1 + 0.35 * Math.Sin(i * 0.5 + c.Cpu))) * CpuPerTick);
                    long mem = (long)(c.MemMiB * (1 + 0.02 * Math.Sin(k * 0.3)) * 1048576);
                    sb.Append("{\"ContainerID\":\"" + c.Id + "\",\"Name\":\"" + c.Name + "\",\"CPUNano\":" + cpuNano + ",\"SystemNano\":" + (T0 + TickNano * k) +
                              ",\"MemUsage\":" + mem + ",\"MemLimit\":16777216000," +
                              "\"NetInput\":" + (long)(c.MemMiB * 90000 * (k + 10)) + ",\"NetOutput\":" + (long)(c.MemMiB * 40000 * (k + 10)) + "," +
                              "\"BlockInput\":" + (long)(c.MemMiB * 300000) + ",\"BlockOutput\":" + (long)(c.MemMiB * 120000 * (k + 1)) + ",\"PIDs\":12}\n");
                }
                // 호스트: 4코어, 사용률 30% 안팎, iowait 1~2%
                long total = 1200L * (k + 1);
                long user = (long)(Enumerable.Range(0, k + 1).Sum(i => 300 + 80 * Math.Sin(i * 0.4))), sys = 60L * (k + 1), iowait = 18L * (k + 1);
                long idle = total - user - sys - iowait;
                sb.Append("@@HOST@@\n");
                sb.Append($"cpu  {user} 0 {sys} {idle} {iowait} 0 0 0 0 0\n");
                sb.Append("MemTotal:       16384000 kB\n");
                sb.Append($"MemAvailable:   {(Incident ? 1400000 : 7700000 + 150000 * Math.Sin(k * 0.3)):0} kB\n");
                sb.Append("1.42 1.31 1.18 3/1544 223311\n4\n");
                sb.Append("@@DISK@@\nFilesystem 1-blocks Used Available Capacity Mounted on\n");
                long size = 500_000_000_000, used = Incident ? 452_000_000_000 : 438_000_000_000;
                sb.Append($"/dev/nvme0n1p2 {size} {used} {size - used} 88% /\n");
                sb.Append("/dev/nvme0n1p1 1127000000 6440000 1120560000 1% /boot/efi\n");
                sb.Append("/dev/sdb1 2000000000000 640000000000 1360000000000 32% /mnt/backup\n");
                return sb.ToString();
            }

            private bool Running(Demo c) => c.Running && !(Incident && c.Name == "penpot-exporter-1");

            private string Json(Demo c)
            {
                var running = Running(c);
                var health = c.Health;
                if (Incident && c.Name == "grafana") health = "unhealthy";
                var status = running ? "Up 7 days" + (health != null ? $" ({health})" : "")
                           : c.Name == "old-backup" ? "Exited (0) 3 weeks ago" : "Exited (137) 2 seconds ago";
                var labels = string.IsNullOrEmpty(c.Project) ? "null"
                    : "{\"com.docker.compose.project\":\"" + c.Project + "\",\"com.docker.compose.service\":\"" + c.Service + "\"" +
                      (c.DependsOn.Length > 0 ? ",\"com.docker.compose.depends_on\":\"" + c.DependsOn + "\"" : "") + "}";
                return "{\"Id\":\"" + c.Id + "\",\"Names\":[\"" + c.Name + "\"],\"Image\":\"" + c.Image + "\",\"State\":\"" + (running ? "running" : "exited") +
                       "\",\"Status\":\"" + status + "\",\"Ports\":" + c.Ports + ",\"Labels\":" + labels +
                       ",\"PodName\":\"" + c.Pod + "\",\"IsInfra\":" + (c.Infra ? "true" : "false") + "}";
            }
        }
    }
}
