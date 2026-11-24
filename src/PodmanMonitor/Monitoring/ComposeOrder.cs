using System;
using System.Collections.Generic;
using System.Linq;

namespace PodmanMonitor.Monitoring
{
    /// <summary>compose 의존 한 개: 서비스 이름과 조건(service_started·service_healthy·service_completed_successfully).</summary>
    public readonly struct ComposeDependency
    {
        public ComposeDependency(string service, string condition) { Service = service; Condition = condition; }
        public string Service { get; }
        public string Condition { get; }
        public bool NeedsHealthy => Condition == "service_healthy";
    }

    /// <summary>원격에서 실행할 한 단계. Action 은 start·stop·restart, 또는 건강해질 때까지 기다리는 wait-healthy.</summary>
    public readonly struct ContainerStep
    {
        public ContainerStep(string id, string action) { Id = id; Action = action; }
        public string Id { get; }
        public string Action { get; }
        public override string ToString() => Id + ":" + Action;
    }

    /// <summary>조작할 컨테이너 한 개의 compose 정보.</summary>
    public sealed class ComposeTarget
    {
        public string Id { get; set; }
        public string Project { get; set; } = "";
        public string Service { get; set; } = "";
        public IReadOnlyList<ComposeDependency> DependsOn { get; set; } = Array.Empty<ComposeDependency>();
    }

    /// <summary>
    /// docker compose 처럼 의존 순서를 지켜 조작 단계를 만든다.
    /// 시작은 의존 대상부터(service_healthy 면 건강해질 때까지 기다린 뒤 다음으로), 중지는 그 반대 순서,
    /// 재시작은 서로 의존하는 것이 있으면 "역순 중지 → 정순 시작", 없으면 각각 restart.
    /// 같은 프로젝트 안, 함께 고른 컨테이너 사이의 의존만 따진다.
    /// </summary>
    public static class ComposeOrder
    {
        public const string WaitHealthy = "wait-healthy";

        /// <summary>라벨 com.docker.compose.depends_on 값 "db:service_healthy:false,redis:service_started:false" 해석.</summary>
        public static List<ComposeDependency> ParseDependsOn(string label)
        {
            var list = new List<ComposeDependency>();
            if (string.IsNullOrWhiteSpace(label)) return list;
            foreach (var item in label.Split(','))
            {
                var parts = item.Trim().Split(':');
                if (parts[0].Length == 0) continue;
                list.Add(new ComposeDependency(parts[0], parts.Length > 1 ? parts[1] : "service_started"));
            }
            return list;
        }

        public static List<ContainerStep> Plan(IReadOnlyList<ComposeTarget> targets, string action)
        {
            var order = StartOrder(targets);
            switch (action)
            {
                case "start":
                    return StartSteps(order);
                case "stop":
                    return Enumerable.Reverse(order).Select(t => new ContainerStep(t.Id, "stop")).ToList();
                case "restart":
                    if (!HasInternalDependency(targets))
                        return targets.Select(t => new ContainerStep(t.Id, "restart")).ToList();
                    var steps = Enumerable.Reverse(order).Select(t => new ContainerStep(t.Id, "stop")).ToList();
                    steps.AddRange(StartSteps(order));
                    return steps;
                default:
                    throw new ArgumentException("지원하지 않는 조작: " + action);
            }
        }

        /// <summary>의존 대상이 먼저 오는 순서(위상 정렬). 순환이 있으면 남은 것은 원래 순서대로 붙인다.</summary>
        public static List<ComposeTarget> StartOrder(IReadOnlyList<ComposeTarget> targets)
        {
            var result = new List<ComposeTarget>();
            var done = new HashSet<ComposeTarget>();
            var remaining = targets.ToList();
            while (remaining.Count > 0)
            {
                var ready = remaining.FirstOrDefault(t => Dependencies(t, targets).All(done.Contains));
                if (ready == null) { result.AddRange(remaining); break; } // 순환
                result.Add(ready);
                done.Add(ready);
                remaining.Remove(ready);
            }
            return result;
        }

        private static List<ContainerStep> StartSteps(List<ComposeTarget> order)
        {
            var steps = new List<ContainerStep>();
            for (int i = 0; i < order.Count; i++)
            {
                var t = order[i];
                steps.Add(new ContainerStep(t.Id, "start"));
                // 뒤에 올 것 중 이 컨테이너가 건강해지기를 기다리는 것이 있으면 기다린다
                bool someoneWaits = order.Skip(i + 1).Any(o => SameProject(o, t) && o.DependsOn.Any(d => d.Service == t.Service && d.NeedsHealthy));
                if (someoneWaits) steps.Add(new ContainerStep(t.Id, WaitHealthy));
            }
            return steps;
        }

        private static IEnumerable<ComposeTarget> Dependencies(ComposeTarget t, IReadOnlyList<ComposeTarget> all) =>
            t.DependsOn.SelectMany(d => all.Where(o => o != t && SameProject(o, t) && o.Service == d.Service));

        private static bool HasInternalDependency(IReadOnlyList<ComposeTarget> targets) =>
            targets.Any(t => Dependencies(t, targets).Any());

        private static bool SameProject(ComposeTarget a, ComposeTarget b) =>
            !string.IsNullOrEmpty(a.Project) && a.Project == b.Project && !string.IsNullOrEmpty(a.Service) && !string.IsNullOrEmpty(b.Service);
    }
}
