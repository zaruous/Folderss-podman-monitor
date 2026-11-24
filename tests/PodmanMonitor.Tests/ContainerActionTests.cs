using System;
using System.Collections.Generic;
using System.Linq;
using PodmanMonitor.Monitoring;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class ContainerActionTests
    {
        [Fact]
        public void ComposeLabels_Parsed()
        {
            var list = RemoteScript.ParseContainers(
                "[{\"Id\":\"aaa\",\"Names\":[\"/penpot-backend-1\"],\"Labels\":{\"com.docker.compose.project\":\"penpot\"," +
                "\"com.docker.compose.service\":\"backend\",\"com.docker.compose.depends_on\":\"postgres:service_healthy:false,valkey:service_started:false\"}}," +
                "{\"Id\":\"bbb\",\"Names\":[\"/solo\"],\"Labels\":{}}," +
                "{\"Id\":\"ccc\",\"Names\":[\"/nolabels\"]}]");
            Assert.Equal("penpot", list[0].Project);
            Assert.Equal("backend", list[0].Service);
            Assert.Equal(new[] { "postgres", "valkey" }, list[0].DependsOn.Select(d => d.Service));
            Assert.True(list[0].DependsOn[0].NeedsHealthy);
            Assert.False(list[0].DependsOn[1].NeedsHealthy);
            Assert.Equal("", list[1].Project);
            Assert.Equal("", list[2].Project);
            Assert.Empty(list[2].DependsOn);
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("db", 1)]                                   // 예전 형식(조건 없음)
        [InlineData("db:service_started:false, redis:service_healthy:true", 2)]
        public void DependsOn_Formats(string label, int count) => Assert.Equal(count, ComposeOrder.ParseDependsOn(label).Count);

        // penpot 과 비슷한 구성: frontend → backend → (postgres healthy, valkey), exporter → backend
        static List<ComposeTarget> Penpot() => new List<ComposeTarget>
        {
            T("f1", "frontend", "backend:service_started"),
            T("e1", "exporter", "backend:service_started"),
            T("b1", "backend", "postgres:service_healthy,valkey:service_started"),
            T("p1", "postgres"),
            T("v1", "valkey"),
        };

        static ComposeTarget T(string id, string service, string deps = "") =>
            new ComposeTarget { Id = id, Project = "penpot", Service = service, DependsOn = ComposeOrder.ParseDependsOn(deps) };

        static string Plan(IReadOnlyList<ComposeTarget> t, string action) => string.Join(" ", ComposeOrder.Plan(t, action));

        [Fact]
        public void Start_DependenciesFirst_WaitsForHealthy() =>
            Assert.Equal("p1:start p1:wait-healthy v1:start b1:start f1:start e1:start", Plan(Penpot(), "start"));

        [Fact]
        public void Stop_ReverseOrder() =>
            Assert.Equal("e1:stop f1:stop b1:stop v1:stop p1:stop", Plan(Penpot(), "stop"));

        [Fact]
        public void Restart_WithDependencies_StopThenStart() =>
            Assert.Equal("e1:stop f1:stop b1:stop v1:stop p1:stop p1:start p1:wait-healthy v1:start b1:start f1:start e1:start",
                Plan(Penpot(), "restart"));

        [Fact]
        public void Restart_Independent_PlainRestart() =>
            Assert.Equal("f1:restart p1:restart", Plan(new[] { Penpot()[0], Penpot()[3] }, "restart"));

        [Fact]
        public void DependencyOutsideSelection_Ignored() =>
            // backend 만 고르면 postgres 를 기다리지 않는다
            Assert.Equal("b1:start", Plan(new[] { Penpot()[2] }, "start"));

        [Fact]
        public void OtherProjectWithSameService_NotADependency()
        {
            var other = new ComposeTarget { Id = "x1", Project = "other", Service = "postgres" };
            Assert.Equal("b1:start x1:start", Plan(new[] { Penpot()[2], other }, "start"));
        }

        [Fact]
        public void Cycle_KeepsOriginalOrder() =>
            Assert.Equal("a1:start b2:start", Plan(new[] { T("a1", "a", "b"), T("b2", "b", "a") }, "start"));

        [Fact]
        public void Script_LoopsOverSteps()
        {
            var s = RemoteScript.ContainerSteps(new[] { new ContainerStep("abc123", "stop"), new ContainerStep("def456", "wait-healthy") });
            Assert.Contains("for x in abc123:stop def456:wait-healthy; do", s);
            Assert.Contains("\"Status\":\"healthy\"", s);
        }

        [Theory]
        [InlineData("abc; rm -rf /", "stop")]
        [InlineData("", "stop")]
        [InlineData("abc", "kill")]
        public void Script_RejectsBadInput(string id, string action) =>
            Assert.Throws<ArgumentException>(() => RemoteScript.ContainerSteps(new[] { new ContainerStep("abc", "start"), new ContainerStep(id, action) }));

        [Fact]
        public void Results_ParsedInOrder()
        {
            var r = RemoteScript.ParseStepResults("abc stop 204\r\nabc start 304\nddd wait-healthy timeout\n\n");
            Assert.Equal(3, r.Count);
            Assert.Equal(("abc", "start", "304"), r[1]);
            Assert.True(RemoteScript.StepSucceeded(r[0].Result));
            Assert.False(RemoteScript.StepSucceeded(r[2].Result));
        }
    }
}
