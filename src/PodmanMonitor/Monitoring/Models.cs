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
        /// <summary>docker compose 프로젝트 이름(라벨 com.docker.compose.project). compose 로 띄운 것이 아니면 빈 문자열.</summary>
        public string Project { get; set; } = "";
        /// <summary>compose 서비스 이름(라벨 com.docker.compose.service).</summary>
        public string Service { get; set; } = "";
        /// <summary>compose 의존(라벨 com.docker.compose.depends_on).</summary>
        public List<ComposeDependency> DependsOn { get; set; } = new List<ComposeDependency>();

        public bool HasStats { get; set; }
        /// <summary>호스트 전체 대비 CPU % (100 = 모든 코어). 첫 표본은 비교 대상이 없어 null.</summary>
        public double? CpuPercent { get; set; }
        /// <summary>사용 코어 수(= docker stats 의 CPU % / 100).</summary>
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
