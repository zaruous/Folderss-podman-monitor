using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;
using PodmanMonitor.Ui;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class ProcessSnapshotTests
    {
        const string GitSh = @"C:\Program Files\Git\usr\bin\sh.exe";
        static readonly string Cid = new string('a', 64);

        // 4코어, CLK_TCK 100, 메모리 8GiB(8388608 kB), 스냅샷 셸 PID 900
        static readonly string LinuxOutput = string.Join("\n",
            "@@PINFO@@ 4 100 8388608 900",
            "@@PT1@@", "1000.00", "1 500", "200 1000", "300 70", "900 1",
            "@@PT2@@", "1001.00", "1 500", "200 1200", "300 75", "900 2", "901 0",
            "@@PPS@@",
            "    1     0 root          12000 86400 /sbin/init splash",
            "  200     1 postgres     524288   120 postgres: writer process   ",
            "  300     1 www-data       1024    59 nginx -g daemon off;",
            "  400     2 root              0  3600 [kworker/0:1]",
            "  900   850 deploy         3000     1 sh -c n=$(nproc)",
            "  901   900 deploy         2000     0 ps -eww -o pid=",
            "@@PCG@@",
            "/proc/200/cgroup:" + Cid,
            "/proc/200/cgroup:" + Cid,
            "");

        [Fact]
        public void ParseLinux_CpuFromTickDelta_SkipsOwnShell_MapsContainer()
        {
            var snap = ProcessScript.Parse(LinuxOutput, DateTime.Now);
            Assert.Equal(4, snap.Cores);
            Assert.Equal(8L * 1024 * 1024 * 1024, snap.MemTotal);
            Assert.Equal(new[] { 1, 200, 300, 400 }, snap.Processes.Select(p => p.Pid)); // 900(스냅샷 셸)·901(그 자식) 제외

            var pg = snap.Processes.Single(p => p.Pid == 200);
            Assert.Equal(2.0, pg.CpuCores.Value, 6);           // 200틱 / 100Hz / 1초
            Assert.Equal(50.0, pg.CpuPercent.Value, 6);        // 4코어 중 2코어
            Assert.Equal(524288L * 1024, pg.Rss);
            Assert.Equal(6.25, pg.MemPercent.Value, 6);        // 512MiB / 8GiB
            Assert.Equal(TimeSpan.FromSeconds(120), pg.Elapsed);
            Assert.Equal("postgres: writer process", pg.Command);
            Assert.Equal(Cid, pg.ContainerId);

            var init = snap.Processes.Single(p => p.Pid == 1);
            Assert.Equal(0, init.CpuPercent.Value, 6);
            Assert.Equal("", init.ContainerId);
            Assert.Null(snap.Processes.Single(p => p.Pid == 400).CpuPercent); // 첫 표본에 없던 프로세스
        }

        [Fact]
        public void ParseWindows_Rows()
        {
            var snap = ProcessScript.Parse(string.Join("\n",
                "@@PINFO@@ 8 0 16777216 4242",
                "@@PROWS@@",
                "1234\tCORP\\kim\t150\t104857600\t3600\t\"C:\\Program Files\\app.exe\" --serve",
                "4\t\t\t1048576\t\tSystem",
                "bad line"), DateTime.Now);
            Assert.Equal(2, snap.Processes.Count);
            var app = snap.Processes[0];
            Assert.Equal(1.5, app.CpuCores.Value, 6);
            Assert.Equal(18.75, app.CpuPercent.Value, 6);      // 1.5코어 / 8코어
            Assert.Equal("CORP\\kim", app.User);
            Assert.Equal(TimeSpan.FromHours(1), app.Elapsed);
            Assert.Equal("\"C:\\Program Files\\app.exe\" --serve", app.Command);
            var sys = snap.Processes[1];
            Assert.Null(sys.CpuPercent);                       // 권한이 없어 CPU 시간을 못 읽음
            Assert.Null(sys.Elapsed);
            Assert.Equal("System", sys.Command);
        }

        [Fact]
        public void Parse_NoInfo_ThrowsWithOutputHead()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ProcessScript.Parse("sh: ps: not found", DateTime.Now));
            Assert.Contains("ps: not found", ex.Message);
        }

        /// <summary>/proc/PID/stat 해석 awk 를 실제 awk 로: 이름에 공백·괄호가 있어도 utime(14번째)+stime(15번째)을 고른다.</summary>
        [Fact]
        public void StatAwk_HandlesSpacesAndParensInName()
        {
            if (!File.Exists(GitSh)) return;
            var stat = "77 (my (odd) proc) S 1 77 77 0 -1 4194560 100 0 0 0 30 12 0 0 20 0 1 0 500 1000 10\n"
                     + "5 (kthreadd) S 0 0 0 0 -1 0 0 0 0 0 7 3 0 0\n";
            var psi = new ProcessStartInfo(GitSh) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
            psi.Environment["PATH"] = Path.GetDirectoryName(GitSh) + ";" + psi.Environment["PATH"];
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("awk '" + ProcessScript.StatAwk + "'");
            using var p = Process.Start(psi);
            p.StandardInput.Write(stat);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd().Replace("\r", "");
            p.WaitForExit();
            Assert.Equal("77 42\n5 10\n", output);
        }

        /// <summary>Windows 서버용 스크립트를 이 PC 의 PowerShell 5.1 로 실제로 돌려 해석한다(이 PC 가 곧 Windows 서버 역할).</summary>
        [Fact]
        public void WindowsScript_RunsInRealPowerShell()
        {
            var cmd = ProcessScript.For(RemoteOs.Windows);
            var b64 = cmd.Substring(cmd.LastIndexOf(' ') + 1);
            var psi = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", b64 }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            var snap = ProcessScript.Parse(output, DateTime.Now);
            Assert.Equal(Environment.ProcessorCount, snap.Cores);
            Assert.True(snap.MemTotal > 0);
            Assert.True(snap.Processes.Count > 10, "프로세스 수 " + snap.Processes.Count);
            Assert.Contains(snap.Processes, x => x.Pid == Environment.ProcessId);   // 시험 프로세스 자신
            Assert.DoesNotContain(snap.Processes, x => x.Pid == p.Id);              // 찍는 PowerShell 은 뺀다
            Assert.Contains(snap.Processes, x => x.CpuPercent.HasValue);
            Assert.All(snap.Processes, x => Assert.False(string.IsNullOrEmpty(x.Command)));
        }

        /// <summary>Linux 서버용 스크립트를 WSL 의 Ubuntu 로 실제로 돌려 해석한다(WSL·Ubuntu 가 없으면 건너뜀).</summary>
        [Fact]
        public void LinuxScript_RunsInRealLinux()
        {
            var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (!File.Exists(wsl)) return;
            var list = new ProcessStartInfo(wsl, "-l -q") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.Unicode };
            using (var l = Process.Start(list))
            {
                var distros = l.StandardOutput.ReadToEnd();
                l.WaitForExit();
                if (!distros.Split('\n').Any(d => d.Trim('\0', '\r', ' ') == "Ubuntu")) return;
            }
            var psi = new ProcessStartInfo(wsl) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-d", "Ubuntu", "-e", "sh" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.StandardInput.NewLine = "\n";
            p.StandardInput.Write(ProcessScript.Linux + "\n");
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));

            var snap = ProcessScript.Parse(output, DateTime.Now);
            Assert.True(snap.Cores >= 1);
            Assert.True(snap.MemTotal > 0);
            Assert.Contains(snap.Processes, x => x.Pid == 1);
            Assert.All(snap.Processes, x => Assert.True(x.CpuPercent.HasValue, $"PID {x.Pid} CPU"));   // 두 표본 모두에 있었다
            Assert.DoesNotContain(snap.Processes, x => x.Command.StartsWith("ps -eww"));             // 찍느라 띄운 ps 는 뺀다
            Assert.DoesNotContain(snap.Processes, x => x.Command.StartsWith("sleep 1"));
        }

        [Fact]
        public void ToText_TabSeparatedWithHeader()
        {
            var snap = ProcessScript.Parse(LinuxOutput, DateTime.Now);
            var rows = snap.Processes.Select(x => new ProcessRow(x, x.ContainerId.Length > 0 ? "db" : "")).ToList();
            var lines = ProcessWindow.ToText(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("PID\t사용자\tCPU%", lines[0]);
            Assert.Equal("200\tpostgres\t50\t6.25\t536870912\t00:02:00\tdb\tpostgres: writer process", lines[2]);
            Assert.Equal("1일 00:00:00", rows[0].ElapsedText);
        }
    }
}
