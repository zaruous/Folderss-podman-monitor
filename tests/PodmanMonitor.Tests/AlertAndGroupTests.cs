using System;
using System.Collections.Generic;
using System.Linq;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ui;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class AlertAndGroupTests
    {
        static ContainerInfo C(string name, string state, string status = "Up 1 hour", string id = null) =>
            new ContainerInfo { Id = id ?? name + "id", Name = name, State = state, Status = status };

        static Snapshot S(IEnumerable<ContainerInfo> containers, double diskPct = 50, double memPct = 50)
        {
            var s = new Snapshot { Time = new DateTime(2026, 11, 14, 12, 0, 0) };
            s.Containers.AddRange(containers);
            s.Disks.Add(new DiskInfo { Mount = "/", Used = (long)diskPct, Avail = (long)(100 - diskPct), Size = 100 });
            s.Host = new HostInfo { MemTotal = 1000, MemAvailable = (long)(1000 - memPct * 10) };
            return s;
        }

        static List<string> Detect(Snapshot a, Snapshot b, params string[] ignore) =>
            AlertDetector.Detect(a, b, ignore).Select(x => x.Level + " " + x.Message).ToList();

        [Fact]
        public void FirstSnapshot_NoAlerts() => Assert.Empty(AlertDetector.Detect(null, S(new[] { C("web", "exited") })));

        [Fact]
        public void Stopped_Restarting_Gone_Back()
        {
            var a = S(new[] { C("web", "running"), C("db", "running"), C("cache", "running"), C("job", "exited", "Exited (0)") });
            var b = S(new[] { C("web", "exited", "Exited (137) 1 second ago"), C("db", "restarting", "Restarting (1)"), C("job", "running") });
            Assert.Equal(new[]
            {
                "Critical web 멈춤 (Exited (137) 1 second ago)",
                "Critical db 재시작 반복 중 (Restarting (1))",
                "Info job 다시 실행됨",
                "Critical cache 사라짐 (실행 중이던 컨테이너가 목록에서 없어짐)",
            }, Detect(a, b));
        }

        [Fact]
        public void Health_Changes()
        {
            var a = S(new[] { C("web", "running", "Up 1 hour (healthy)"), C("api", "running", "Up 1 hour (unhealthy)") });
            var b = S(new[] { C("web", "running", "Up 1 hour (unhealthy)"), C("api", "running", "Up 1 hour (healthy)") });
            Assert.Equal(new[] { "Warning web unhealthy", "Info api unhealthy 에서 회복" }, Detect(a, b));
        }

        [Fact]
        public void UserActions_Ignored()
        {
            var a = S(new[] { C("web", "running"), C("db", "running") });
            var b = S(new[] { C("web", "exited", "Exited (0)"), C("db", "exited", "Exited (0)") });
            Assert.Equal(new[] { "Critical db 멈춤 (Exited (0))" }, Detect(a, b, "webid"));
        }

        [Fact]
        public void Thresholds_OnlyWhenCrossing()
        {
            var c = new[] { C("web", "running") };
            Assert.Equal(new[] { "Warning 디스크 / 91% 사용 (남은 공간 9B)", "Warning 호스트 메모리 95% 사용" },
                Detect(S(c, 85, 80), S(c, 91, 95)));
            Assert.Empty(Detect(S(c, 91, 95), S(c, 92, 96))); // 이미 넘은 상태가 이어지면 다시 알리지 않는다
        }

        [Fact]
        public void GroupSummary_SumsRunningMembers()
        {
            var a = C("a", "running"); a.HasStats = true; a.CpuPercent = 1.25; a.MemUsed = 512L << 20;
            var b = C("b", "running", "Up (unhealthy)"); b.HasStats = true; b.CpuPercent = 0.5; b.MemUsed = 512L << 20;
            var c = C("c", "exited", "Exited (0)");
            Assert.Equal("2/3 실행 · CPU 1.8% · 메모리 1GiB · unhealthy 1", ProjectGroup.Summarize(new[] { a, b, c }));
            Assert.Equal("0/1 실행", ProjectGroup.Summarize(new[] { c }));
        }
    }
}
