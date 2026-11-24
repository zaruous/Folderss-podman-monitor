using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PodmanMonitor.Ssh;

namespace PodmanMonitor.Monitoring
{
    /// <summary>
    /// 갱신마다 원격 스크립트를 한 번 실행해 <see cref="Snapshot"/> 을 만들고, CPU 처럼 차이로 계산하는 값을 위해 직전 누적값을 기억한다.
    /// 추이 그래프용 이력도 여기서 쌓는다.
    /// </summary>
    public sealed class Collector
    {
        public const int MaxSamples = 3600;

        private long[] _prevHostCpu;
        private readonly Dictionary<string, (long cpu, long sys)> _prevContainerCpu = new Dictionary<string, (long, long)>();

        public List<Sample> HostCpu { get; } = new List<Sample>();
        public List<Sample> HostIoWait { get; } = new List<Sample>();
        public List<Sample> HostMem { get; } = new List<Sample>();
        public List<Sample> TotalCpu { get; } = new List<Sample>();
        public List<Sample> TotalMem { get; } = new List<Sample>();
        public Dictionary<string, (List<Sample> Cpu, List<Sample> Mem)> PerContainer { get; } =
            new Dictionary<string, (List<Sample>, List<Sample>)>();

        public async Task<Snapshot> CollectAsync(SshConnection ssh, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = await ssh.RunAsync(RemoteScript.Collect, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            sw.Stop();
            var snap = Build(result.Output, DateTime.Now);
            snap.ElapsedMs = sw.ElapsedMilliseconds;
            return snap;
        }

        /// <summary>원격 출력 → 스냅숏(시험에서 바로 부를 수 있게 분리).</summary>
        public Snapshot Build(string output, DateTime now)
        {
            var sec = RemoteScript.Split(output);
            if (sec.Errors.Count > 0)
                throw new InvalidOperationException(string.Join(" / ", sec.Errors));

            var snap = new Snapshot { Time = now };

            // 호스트
            var host = RemoteScript.ParseHost(sec.HostLines);
            var cpuLine = RemoteScript.ParseCpuLine(sec.HostLines);
            var cpu = RemoteScript.CpuBetween(_prevHostCpu, cpuLine);
            if (cpuLine != null) _prevHostCpu = cpuLine;
            if (cpu.HasValue)
            {
                host.CpuPercent = cpu.Value.usage;
                host.IoWaitPercent = cpu.Value.iowait;
            }
            snap.Host = host;
            snap.Disks.AddRange(RemoteScript.ParseDf(sec.DiskLines));

            // 컨테이너
            var containers = RemoteScript.ParseContainers(sec.ContainersJson);
            var stats = sec.StatsJson.Select(RemoteScript.ParseStats).Where(s => !string.IsNullOrEmpty(s.Id))
                .ToDictionary(s => s.Id);
            foreach (var c in containers)
            {
                if (stats.TryGetValue(c.Id, out var s))
                {
                    c.HasStats = true;
                    c.MemUsed = s.MemUsed;
                    c.MemLimit = s.MemLimit;
                    c.NetRx = s.NetRx; c.NetTx = s.NetTx;
                    c.BlockRead = s.BlockRead; c.BlockWrite = s.BlockWrite;
                    c.Pids = s.Pids;
                    if (_prevContainerCpu.TryGetValue(c.Id, out var prev))
                    {
                        long dCpu = s.CpuTotal - prev.cpu, dSys = s.SystemCpu - prev.sys;
                        if (dSys > 0 && dCpu >= 0)
                        {
                            // system_cpu_usage 는 모든 코어의 합 — 비율이 곧 호스트 전체 대비 사용률
                            c.CpuPercent = 100.0 * dCpu / dSys;
                            c.CpuCores = (double)dCpu / dSys * Math.Max(1, s.OnlineCpus);
                        }
                    }
                    _prevContainerCpu[c.Id] = (s.CpuTotal, s.SystemCpu);
                }
                snap.Containers.Add(c);
            }
            // 사라진 컨테이너의 직전값은 버린다
            foreach (var gone in _prevContainerCpu.Keys.Where(id => !stats.ContainsKey(id)).ToList())
                _prevContainerCpu.Remove(gone);

            Record(snap);
            return snap;
        }

        private void Record(Snapshot snap)
        {
            var t = snap.Time;
            if (snap.Host.CpuPercent.HasValue)
            {
                Add(HostCpu, new Sample(t, snap.Host.CpuPercent.Value));
                Add(HostIoWait, new Sample(t, snap.Host.IoWaitPercent ?? 0));
            }
            if (snap.Host.MemTotal > 0) Add(HostMem, new Sample(t, snap.Host.MemPercent));

            var withStats = snap.Containers.Where(c => c.HasStats).ToList();
            foreach (var c in withStats)
            {
                if (!PerContainer.TryGetValue(c.Name, out var h))
                    PerContainer[c.Name] = h = (new List<Sample>(), new List<Sample>());
                if (c.CpuPercent.HasValue) Add(h.Cpu, new Sample(t, c.CpuPercent.Value));
                Add(h.Mem, new Sample(t, c.MemPercent));
            }
            if (withStats.Any(c => c.CpuPercent.HasValue))
                Add(TotalCpu, new Sample(t, withStats.Sum(c => c.CpuPercent ?? 0)));
            Add(TotalMem, new Sample(t, withStats.Sum(c => c.MemPercent)));
        }

        private static void Add(List<Sample> list, Sample s)
        {
            list.Add(s);
            if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
        }
    }
}
