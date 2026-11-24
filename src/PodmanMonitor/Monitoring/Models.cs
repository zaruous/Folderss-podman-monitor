using System;
using System.Collections.Generic;

namespace PodmanMonitor.Monitoring
{
    /// <summary>컨테이너 한 개의 이번 표본(표시용 값은 계산이 끝난 상태).</summary>
    public sealed class ContainerInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Image { get; set; }
        public string State { get; set; }
        public string Status { get; set; }
        public string Ports { get; set; }
        /// <summary>compose 프로젝트 이름(라벨 com.docker.compose.project, podman-compose 는 io.podman.compose.project 도). compose 로 띄운 것이 아니면 빈 문자열.</summary>
        public string Project { get; set; } = "";
        /// <summary>속한 podman pod 이름. pod 에 없으면 빈 문자열.</summary>
        public string Pod { get; set; } = "";
        /// <summary>표 묶음 이름: compose 프로젝트가 있으면 그것, 없고 pod 에 속하면 "이름 (pod)", 둘 다 없으면 빈 문자열.</summary>
        public string Group => !string.IsNullOrEmpty(Project) ? Project : !string.IsNullOrEmpty(Pod) ? Pod + " (pod)" : "";
        /// <summary>compose 서비스 이름(라벨 com.docker.compose.service).</summary>
        public string Service { get; set; } = "";
        /// <summary>compose 의존(라벨 com.docker.compose.depends_on).</summary>
        public List<ComposeDependency> DependsOn { get; set; } = new List<ComposeDependency>();

        public bool HasStats { get; set; }
        /// <summary>호스트 전체 대비 CPU % (100 = 모든 코어). 첫 표본은 비교 대상이 없어 null.</summary>
        public double? CpuPercent { get; set; }
        /// <summary>사용 코어 수(= podman stats 의 CPU % / 100).</summary>
        public double? CpuCores { get; set; }
        public long MemUsed { get; set; }
        public long MemLimit { get; set; }
        public double MemPercent => MemLimit > 0 ? 100.0 * MemUsed / MemLimit : 0;
        public long NetRx { get; set; }
        public long NetTx { get; set; }
        public long BlockRead { get; set; }
        public long BlockWrite { get; set; }
        public long Pids { get; set; }
    }

    public sealed class HostInfo
    {
        /// <summary>CPU 사용률 %(직전 표본 대비). 첫 표본은 null.</summary>
        public double? CpuPercent { get; set; }
        public double? IoWaitPercent { get; set; }
        public long MemTotal { get; set; }
        public long MemAvailable { get; set; }
        public double MemPercent => MemTotal > 0 ? 100.0 * (MemTotal - MemAvailable) / MemTotal : 0;
        public string LoadAverage { get; set; }
        public int Cores { get; set; }
    }

    public sealed class DiskInfo
    {
        public string Device { get; set; }
        public string Mount { get; set; }
        public long Size { get; set; }
        public long Used { get; set; }
        public long Avail { get; set; }
        /// <summary>df 의 '용량'과 같은 식: used / (used + avail) — 예약 블록 제외.</summary>
        public double Percent => Used + Avail > 0 ? 100.0 * Used / (Used + Avail) : 0;
    }

    public sealed class Snapshot
    {
        public DateTime Time { get; set; }
        public List<ContainerInfo> Containers { get; } = new List<ContainerInfo>();
        public HostInfo Host { get; set; }
        public List<DiskInfo> Disks { get; } = new List<DiskInfo>();
        /// <summary>조회는 됐지만 일부를 읽지 못한 이유(예: rootless + cgroup v1 이라 컨테이너 통계 없음). 상태줄에 보인다.</summary>
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>원격 명령 수행 시간(ms).</summary>
        public long ElapsedMs { get; set; }
    }

    public readonly struct Sample
    {
        public Sample(DateTime time, double value) { Time = time; Value = value; }
        public DateTime Time { get; }
        public double Value { get; }
    }
}
