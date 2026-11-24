using System;
using System.Linq;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ui;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class CollectorTests
    {
        const string ContainersJson =
            "[{\"Id\":\"aaa111aaa111\",\"Names\":[\"/web\"],\"Image\":\"nginx:1\",\"State\":\"running\",\"Status\":\"Up 2 days (unhealthy)\"," +
            "\"Ports\":[{\"IP\":\"0.0.0.0\",\"PrivatePort\":80,\"PublicPort\":8080,\"Type\":\"tcp\"},{\"IP\":\"::\",\"PrivatePort\":80,\"PublicPort\":8080,\"Type\":\"tcp\"},{\"PrivatePort\":443,\"Type\":\"tcp\"}]}," +
            "{\"Id\":\"bbb222bbb222\",\"Names\":[\"/old\"],\"Image\":\"busybox\",\"State\":\"exited\",\"Status\":\"Exited (0) 3 hours ago\",\"Ports\":[]}]";

        static string Stats(long cpuTotal, long systemCpu) =>
            "{\"id\":\"aaa111aaa111\",\"cpu_stats\":{\"cpu_usage\":{\"total_usage\":" + cpuTotal + "},\"system_cpu_usage\":" + systemCpu + ",\"online_cpus\":4}," +
            "\"memory_stats\":{\"usage\":104857600,\"limit\":1073741824,\"stats\":{\"inactive_file\":4857600}}," +
            "\"networks\":{\"eth0\":{\"rx_bytes\":1000,\"tx_bytes\":2000},\"eth1\":{\"rx_bytes\":1,\"tx_bytes\":2}}," +
            "\"blkio_stats\":{\"io_service_bytes_recursive\":[{\"op\":\"read\",\"value\":10},{\"op\":\"write\",\"value\":20},{\"op\":\"read\",\"value\":5}]}," +
            "\"pids_stats\":{\"current\":7}}";

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

        [Fact]
        public void Build_ParsesContainersHostAndDisks()
        {
            var c = new Collector();
            var snap = c.Build(Output(Stats(1_000, 100_000), "cpu  100 0 100 800 0 0 0 0 0 0"), DateTime.Now);

            Assert.Equal(2, snap.Containers.Count);
            var web = snap.Containers.Single(x => x.Name == "web");
            Assert.True(web.HasStats);
            Assert.Null(web.CpuPercent);                       // 첫 표본은 비교 대상이 없다
            Assert.Equal(100_000_000, web.MemUsed);            // usage - inactive_file
            Assert.Equal(1001, web.NetRx);
            Assert.Equal(2002, web.NetTx);
            Assert.Equal(15, web.BlockRead);
            Assert.Equal(20, web.BlockWrite);
            Assert.Equal(7, web.Pids);
            Assert.Equal("8080→80/tcp, 443/tcp", web.Ports);   // IPv6 중복 제외
            Assert.False(snap.Containers.Single(x => x.Name == "old").HasStats);

            Assert.Null(snap.Host.CpuPercent);
            Assert.Equal(25, snap.Host.MemPercent, 3);
            Assert.Equal("0.60 1.02 1.10", snap.Host.LoadAverage);
            Assert.Equal(4, snap.Host.Cores);

            Assert.Equal(2, snap.Disks.Count);
            Assert.Equal("/mnt/my disk", snap.Disks[1].Mount);
            Assert.Equal(25, snap.Disks[1].Percent, 3);
        }

        [Fact]
        public void Build_SecondSample_ComputesCpuFromDeltas()
        {
            var c = new Collector();
            var t0 = DateTime.Now;
            c.Build(Output(Stats(1_000, 100_000), "cpu  100 0 100 800 0 0 0 0 0 0"), t0);
            // 컨테이너: 시스템 전체 증가 10,000 중 2,500 → 호스트 전체의 25%, 4코어 기준 1코어
            // 호스트: 전체 증가 100 중 idle+iowait 60(iowait 10) → 사용률 40%, iowait 10%
            var snap = c.Build(Output(Stats(3_500, 110_000), "cpu  130 0 110 850 10 0 0 0 0 0"), t0.AddSeconds(3));

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
                new Collector().Build("@@ERR@@ 서버에 curl 이 없습니다\n@@PS@@\n[]", DateTime.Now));
            Assert.Contains("curl", ex.Message);
        }

        [Fact]
        public void ParseStats_CgroupV1MemoryKey()
        {
            var s = RemoteScript.ParseStats("{\"id\":\"x\",\"memory_stats\":{\"usage\":1000,\"limit\":4000,\"stats\":{\"total_inactive_file\":200}}}");
            Assert.Equal(800, s.MemUsed);
            Assert.Equal(4000, s.MemLimit);
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
