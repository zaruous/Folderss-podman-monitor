using System;
using System.Linq;
using System.Text;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;
using Xunit;

namespace PodmanMonitor.Tests
{
    /// <summary>Windows 서버(OpenSSH 로그인 셸이 cmd, podman.exe + podman machine): PowerShell 스크립트와 그 출력 해석.</summary>
    public class WindowsHostTests
    {
        static string Decode(string command)
        {
            const string prefix = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
            Assert.StartsWith(prefix, command);
            return Encoding.Unicode.GetString(Convert.FromBase64String(command.Substring(prefix.Length)));
        }

        // Windows 스크립트가 내는 출력(Windows 11 + podman 6.2.0 에서 받은 형식). 디스크는 드라이브, load average 없음
        static string Output(long cpuNano, long sampleNano, string cpuLine) =>
            "@@PS@@\n[{\"Id\":\"6b38f679028b3f21\",\"Names\":[\"mesplus_weekly\"],\"Image\":\"docker.io/library/postgres:15\",\"State\":\"running\",\"Status\":\"Up 22 minutes\"," +
            "\"Labels\":null,\"PodName\":\"\",\"IsInfra\":false,\"Ports\":[{\"host_ip\":\"\",\"container_port\":5432,\"host_port\":5432,\"range\":1,\"protocol\":\"tcp\"}," +
            "{\"host_ip\":\"::\",\"container_port\":5432,\"host_port\":5432,\"range\":1,\"protocol\":\"tcp\"}]}]\n" +
            "@@STATS@@\n{\"ContainerID\":\"6b38f679028b3f21\",\"CPUNano\":" + cpuNano + ",\"SystemNano\":" + sampleNano + ",\"MemUsage\":47411200,\"MemLimit\":16633774080," +
            "\"Network\":{\"eth0\":{\"RxBytes\":622,\"TxBytes\":1174}},\"BlockInput\":49528832,\"BlockOutput\":647168,\"PIDs\":7}\n" +
            "@@HOST@@\n" + cpuLine + "\nMemTotal: 33286068 kB\nMemAvailable: 22627812 kB\n12\n" +
            "@@DISK@@\nC: 999228960768 215473987584 783754973184 22% C:\\\nD: 100 50 50 50% D:\\\n";

        [Fact]
        public void Build_WindowsOutput()
        {
            var c = new Collector();
            var t0 = DateTime.Now;
            // 앞에 BOM 이 붙어 와도 된다
            c.Build("\uFEFF" + Output(1_000_000_000, 1_000_000_000_000, "cpu 100 0 200 700 0 0 0 0"), t0);
            var snap = c.Build(Output(1_600_000_000, 1_002_000_000_000, "cpu 130 0 230 840 0 0 0 0"), t0.AddSeconds(2));

            Assert.Equal(12, snap.Host.Cores);
            Assert.Equal("", snap.Host.LoadAverage);
            Assert.Equal(33286068L * 1024, snap.Host.MemTotal);
            Assert.Equal(30.0, snap.Host.CpuPercent.Value, 6);   // (30+30)/(30+30+140)
            Assert.Equal(0.0, snap.Host.IoWaitPercent.Value, 6);

            Assert.Equal(new[] { "C:\\", "D:\\" }, snap.Disks.Select(d => d.Mount));
            Assert.Equal("C:", snap.Disks[0].Device);
            Assert.Equal(999228960768, snap.Disks[0].Size);

            var x = Assert.Single(snap.Containers);
            Assert.Equal(0.3, x.CpuCores.Value, 6);              // 0.6초 / 2초
            Assert.Equal(2.5, x.CpuPercent.Value, 6);            // 0.3코어 / 12코어
            Assert.Equal("5432→5432/tcp", x.Ports);              // IPv4·IPv6 와일드카드는 하나로
        }

        [Fact]
        public void Scripts_AreEncodedPowerShell()
        {
            foreach (var cmd in new[] { RemoteScript.CollectFor(RemoteOs.Windows), RemoteScript.ProbeFor(RemoteOs.Windows), RemoteScript.Logs("abc123", 200, RemoteOs.Windows) })
            {
                Assert.True(cmd.Length < 8000, "cmd 한 줄 제한");
                var ps = Decode(cmd);
                Assert.Contains("function Invoke-Podman", ps);
                Assert.Contains("UTF8Encoding $false", ps);
                Assert.DoesNotContain("\r", ps);
            }
            Assert.Contains("@@PS@@", Decode(RemoteScript.CollectFor(RemoteOs.Windows)));
            Assert.Contains("Win32_LogicalDisk", Decode(RemoteScript.CollectFor(RemoteOs.Windows)));
            Assert.Contains("nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits", Decode(RemoteScript.CollectFor(RemoteOs.Windows)));
            Assert.Contains("@@OK@@", Decode(RemoteScript.ProbeFor(RemoteOs.Windows)));
            // Linux 는 그대로 sh
            Assert.Same(RemoteScript.Collect, RemoteScript.CollectFor(RemoteOs.Posix));
            Assert.Equal("podman logs --tail 200 --timestamps abc123 2>&1", RemoteScript.Logs("abc123", 200));
        }

        [Fact]
        public void ContainerSteps_Windows_ShortIdsInOrder()
        {
            var id = new string('a', 64);
            var ps = Decode(RemoteScript.ContainerSteps(new[] { new ContainerStep(id, "stop"), new ContainerStep("def456", ComposeOrder.WaitHealthy) }, RemoteOs.Windows));
            Assert.Contains("@('aaaaaaaaaaaa:stop','def456:wait-healthy')", ps);
            Assert.Contains("healthcheck run", ps);
            Assert.DoesNotContain(id, ps);
            // 출력 형식은 Linux 와 같아 ParseStepResults 를 그대로 쓴다
            Assert.Equal(("aaaaaaaaaaaa", "stop", "ok"), RemoteScript.ParseStepResults("aaaaaaaaaaaa stop ok\r\n").Single());
        }

        [Fact]
        public void ContainerSteps_Windows_TooManyThrows()
        {
            var steps = Enumerable.Range(0, 200).Select(i => new ContainerStep(i.ToString("x12"), "restart"));
            Assert.Throws<ArgumentException>(() => RemoteScript.ContainerSteps(steps, RemoteOs.Windows));
        }

        [Theory]
        [InlineData("abc;rm")]
        [InlineData("")]
        public void Logs_RejectsBadId(string id) => Assert.Throws<ArgumentException>(() => RemoteScript.Logs(id, 10, RemoteOs.Windows));
    }
}
