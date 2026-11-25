using System;
using System.Linq;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ui;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class CollectorTests
    {
        // podman 4.9 의 ps -a --pod --format json 형식(필요한 키만). 이름에 '/' 가 없고, 포트는 host_port/container_port/range
        const string ContainersJson =
            "[{\"Id\":\"aaa111aaa111\",\"Names\":[\"web\"],\"Image\":\"docker.io/library/nginx:1\",\"State\":\"running\",\"Status\":\"Up 2 days (unhealthy)\"," +
            "\"Labels\":null,\"Pod\":\"\",\"PodName\":\"\",\"IsInfra\":false," +
            "\"Ports\":[{\"host_ip\":\"\",\"container_port\":80,\"host_port\":8080,\"range\":1,\"protocol\":\"tcp\"}," +
            "{\"host_ip\":\"127.0.0.1\",\"container_port\":9000,\"host_port\":19000,\"range\":3,\"protocol\":\"udp\"}]}," +
            "{\"Id\":\"bbb222bbb222\",\"Names\":[\"old\"],\"Image\":\"docker.io/library/busybox\",\"State\":\"exited\",\"Status\":\"Exited (0) 3 hours ago\",\"Ports\":null,\"IsInfra\":false}," +
            "{\"Id\":\"ccc333ccc333\",\"Names\":[\"5eb3bd2af5ba-infra\"],\"Image\":\"\",\"State\":\"running\",\"Status\":\"Up 2 days\",\"PodName\":\"web\",\"IsInfra\":true}]";

        // podman stats --no-stream --format '{{json .}}' 한 줄(podman 4.9 실제 출력 형식)
        static string Stats(long cpuNano, long systemNano) =>
            "{\"AvgCPU\":0.8,\"ContainerID\":\"aaa111aaa111\",\"Name\":\"web\",\"PerCPU\":null,\"CPU\":0.8,\"CPUNano\":" + cpuNano +
            ",\"CPUSystemNano\":3956047,\"SystemNano\":" + systemNano + ",\"MemUsage\":100000000,\"MemLimit\":1073741824,\"MemPerc\":9.3," +
            "\"NetInput\":1001,\"NetOutput\":2002,\"BlockInput\":15,\"BlockOutput\":20,\"PIDs\":7,\"UpTime\":28808786,\"Duration\":28808786}";

        static string Output(string stats, string cpuLine) => string.Join("\n",
            "@@PS@@", ContainersJson, "",
            "@@STATS@@", stats, "",
            "@@HOST@@", cpuLine,
            "MemTotal:       16000000 kB", "MemAvailable:   12000000 kB",
            "0.60 1.02 1.10 2/1387 1795324", "4",
            "@@DISK@@",
            "Filesystem 1-blocks Used Available Capacity Mounted on",
            "/dev/nvme0n1p2 500000000000 90000000000 386000000000  19% /",
            "/dev/sdb1 1000 250 750 25% /mnt/my disk");

        const long T0 = 1_795_482_245_000_000_000;

        [Fact]
        public void Build_ParsesContainersHostAndDisks()
        {
            var c = new Collector();
            var snap = c.Build(Output(Stats(1_000, T0), "cpu  100 0 100 800 0 0 0 0 0 0"), DateTime.Now);

            Assert.Equal(2, snap.Containers.Count);                // pod infra 컨테이너는 뺀다
            var web = snap.Containers.Single(x => x.Name == "web");
            Assert.True(web.HasStats);
            Assert.Null(web.CpuPercent);                       // 첫 표본은 비교 대상이 없다
            Assert.Equal(100_000_000, web.MemUsed);
            Assert.Equal(1_073_741_824, web.MemLimit);
            Assert.Equal(1001, web.NetRx);
            Assert.Equal(2002, web.NetTx);
            Assert.Equal(15, web.BlockRead);
            Assert.Equal(20, web.BlockWrite);
            Assert.Equal(7, web.Pids);
            Assert.Equal("8080→80/tcp, 127.0.0.1:19000-19002→9000-9002/udp", web.Ports);
            Assert.Equal("", web.Group);
            var old = snap.Containers.Single(x => x.Name == "old");
            Assert.False(old.HasStats);
            Assert.Equal("", old.Ports);

            Assert.Null(snap.Host.CpuPercent);
            Assert.Equal(25, snap.Host.MemPercent, 3);
            Assert.Equal("0.60 1.02 1.10", snap.Host.LoadAverage);
            Assert.Equal(4, snap.Host.Cores);

            Assert.Equal(2, snap.Disks.Count);
            Assert.Equal("/mnt/my disk", snap.Disks[1].Mount);
            Assert.Equal(25, snap.Disks[1].Percent, 3);
            Assert.Empty(snap.Warnings);
        }

        [Fact]
        public void Build_SecondSample_ComputesCpuFromDeltas()
        {
            var c = new Collector();
            var t0 = DateTime.Now;
            c.Build(Output(Stats(1_000_000_000, T0), "cpu  100 0 100 800 0 0 0 0 0 0"), t0);
            // 컨테이너: 2초 동안 CPU 시간 2초 증가 → 1코어, 4코어 호스트의 25%
            // 호스트: 전체 증가 100 중 idle+iowait 60(iowait 10) → 사용률 40%, iowait 10%
            var snap = c.Build(Output(Stats(3_000_000_000, T0 + 2_000_000_000), "cpu  130 0 110 850 10 0 0 0 0 0"), t0.AddSeconds(2));

            var web = snap.Containers.Single(x => x.Name == "web");
            Assert.Equal(25, web.CpuPercent.Value, 6);
            Assert.Equal(1, web.CpuCores.Value, 6);
            Assert.Equal(40, snap.Host.CpuPercent.Value, 6);
            Assert.Equal(10, snap.Host.IoWaitPercent.Value, 6);

            Assert.Single(c.HostCpu);
            Assert.Single(c.TotalCpu);
            Assert.Equal(2, c.PerContainer["web"].Mem.Count);
        }

        [Fact]
        public void Build_RemoteError_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new Collector().Build("@@ERR@@ 서버에 podman 이 없습니다\n", DateTime.Now));
            Assert.Contains("podman", ex.Message);
        }

        [Fact]
        public void Build_StatsUnavailable_ShowsContainersWithWarning()
        {
            // rootless + cgroup v1: podman stats 는 실패하지만 목록·호스트는 보여야 한다
            var output = Output(Stats(1, T0), "cpu  100 0 100 800 0 0 0 0 0 0")
                .Replace("@@STATS@@\n" + Stats(1, T0) + "\n", "@@WARN@@ 컨테이너 통계를 읽지 못했습니다: Error: stats is not supported in rootless mode without cgroups v2\n");
            var snap = new Collector().Build(output, DateTime.Now);
            Assert.Equal(2, snap.Containers.Count);
            Assert.All(snap.Containers, x => Assert.False(x.HasStats));
            Assert.Contains("cgroups v2", Assert.Single(snap.Warnings));
            Assert.Equal(4, snap.Host.Cores);
        }

        // nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits
        const string GpuSection = "@@GPU@@\n0, NVIDIA GeForce RTX 3090, 35, 6144, 24576\n1, NVIDIA A100-SXM4-40GB, 0, 0, 40960\n";

        [Fact]
        public void Build_ParsesGpusAndKeepsHistory()
        {
            var c = new Collector();
            var t0 = DateTime.Now;
            var snap = c.Build(Output(Stats(1, T0), "cpu  100 0 100 800 0 0 0 0 0 0") + "\n" + GpuSection, t0);

            Assert.Equal(2, snap.Gpus.Count);
            var g = snap.Gpus[0];
            Assert.Equal(0, g.Index);
            Assert.Equal("NVIDIA GeForce RTX 3090", g.Name);
            Assert.Equal(35, g.Utilization.Value, 6);
            Assert.Equal(6144L * 1048576, g.MemUsed);
            Assert.Equal(24576L * 1048576, g.MemTotal);
            Assert.Equal(25, g.MemPercent.Value, 6);
            Assert.Equal(2, snap.Disks.Count);                     // GPU 줄이 디스크로 섞이지 않는다

            c.Build(Output(Stats(1, T0 + 3_000_000_000), "cpu  130 0 110 850 10 0 0 0 0 0") + "\n" + GpuSection, t0.AddSeconds(3));
            Assert.Equal(new[] { 0, 1 }, c.Gpu.Keys);
            Assert.Equal(2, c.Gpu[0].Util.Count);
            Assert.Equal(2, c.Gpu[1].Mem.Count);
            Assert.Equal(0, c.Gpu[1].Mem[1].Value, 6);
        }

        [Fact]
        public void Build_NoGpu_EmptyListNoHistory()
        {
            var c = new Collector();
            var snap = c.Build(Output(Stats(1, T0), "cpu  100 0 100 800 0 0 0 0 0 0"), DateTime.Now);
            Assert.Empty(snap.Gpus);
            Assert.Empty(c.Gpu);
        }

        [Fact]
        public void ParseGpus_NotAvailableValuesAndCommaInName()
        {
            // 사용률·메모리를 못 주는 GPU 는 [N/A]. 이름에 쉼표가 있어도 앞(index)·뒤 3칸으로 자른다. 오류 문구 줄은 버린다
            var gpus = RemoteScript.ParseGpus(new[]
            {
                "0, Tesla K80, rev 2, [N/A], 100, 11441",
                "1, NVIDIA GH200 480GB, 12, [N/A], [N/A]",
                "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.",
            });
            Assert.Equal(2, gpus.Count);
            Assert.Equal("Tesla K80, rev 2", gpus[0].Name);
            Assert.Null(gpus[0].Utilization);
            Assert.Equal(100L * 1048576, gpus[0].MemUsed);
            Assert.Equal(12, gpus[1].Utilization.Value, 6);
            Assert.Null(gpus[1].MemPercent);
        }

        [Fact]
        public void Build_GpuQueryFailed_Warning()
        {
            var snap = new Collector().Build(Output(Stats(1, T0), "cpu  100 0 100 800 0 0 0 0 0 0") +
                "\n@@WARN@@ GPU 정보를 읽지 못했습니다: NVIDIA-SMI has failed\n", DateTime.Now);
            Assert.Empty(snap.Gpus);
            Assert.Contains("GPU", Assert.Single(snap.Warnings));
        }

        [Fact]
        public void CollectScript_QueriesNvidiaSmiWithTimeout()
        {
            Assert.Contains("nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits", RemoteScript.Collect);
            Assert.Contains("timeout 5", RemoteScript.Collect);
        }

        [Fact]
        public void ParseStats_Podman5NetworkMap()
        {
            // podman 5 는 NetInput/NetOutput 대신 인터페이스별 Network 를 준다
            var s = RemoteScript.ParseStats("{\"ContainerID\":\"x\",\"CPUNano\":5,\"SystemNano\":6,\"MemUsage\":1000,\"MemLimit\":4000," +
                "\"Network\":{\"eth0\":{\"RxBytes\":10,\"TxBytes\":20},\"eth1\":{\"RxBytes\":1,\"TxBytes\":2}}}");
            Assert.Equal(11, s.NetRx);
            Assert.Equal(22, s.NetTx);
            Assert.Equal(1000, s.MemUsed);
            Assert.Equal(5, s.CpuNano);
        }

        [Fact]
        public void CpuBetween_NoProgress_ReturnsNull()
        {
            var a = new long[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            Assert.Null(RemoteScript.CpuBetween(a, a));
            Assert.Null(RemoteScript.CpuBetween(null, a));
        }

        [Theory]
        [InlineData(0.3, 0.5)]
        [InlineData(7, 10)]
        [InlineData(11, 20)]
        [InlineData(23, 25)]
        [InlineData(260, 500)]
        public void NiceCeiling(double value, double expected) => Assert.Equal(expected, TrendChart.NiceCeiling(value), 6);
    }
}
