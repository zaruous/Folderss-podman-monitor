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
        private readonly Dictionary<string, (long cpu, long time)> _prevContainerCpu = new Dictionary<string, (long, long)>();

        public List<Sample> HostCpu { get; } = new List<Sample>();
        public List<Sample> HostIoWait { get; } = new List<Sample>();
        public List<Sample> HostMem { get; } = new List<Sample>();
        public List<Sample> TotalCpu { get; } = new List<Sample>();
        public List<Sample> TotalMem { get; } = new List<Sample>();
        public Dictionary<string, (List<Sample> Cpu, List<Sample> Mem)> PerContainer { get; } =
            new Dictionary<string, (List<Sample>, List<Sample>)>();
        /// <summary>GPU 번호(nvidia-smi index) → 사용률 %·메모리 % 이력.</summary>
        public SortedDictionary<int, (List<Sample> Util, List<Sample> Mem)> Gpu { get; } =
            new SortedDictionary<int, (List<Sample>, List<Sample>)>();

        public async Task<Snapshot> CollectAsync(SshConnection ssh, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = await ssh.RunAsync(RemoteScript.CollectFor, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            sw.Stop();
            var sec = RemoteScript.Split(result.Output);
            // 표시가 하나도 없으면 스크립트가 돌지 못한 것(셸이 다르거나 막힘) — 빈 목록으로 보이지 않게 이유를 알린다
            if (sec.ContainersJson == null && sec.Errors.Count == 0)
                throw new InvalidOperationException("서버 응답을 해석하지 못했습니다: " +
                    Shorten((result.Error + " " + result.Output).Trim(), $"응답 없음(종료 코드 {result.ExitCode})"));
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
            snap.Warnings.AddRange(sec.Warnings);

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
            snap.Gpus.AddRange(RemoteScript.ParseGpus(sec.GpuLines));

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
                        // 컨테이너 CPU 시간(모든 코어 합) 증가분 / 지난 실제 시간 = 사용 코어 수. 코어 수로 나누면 호스트 전체 대비 %
                        long dCpu = s.CpuNano - prev.cpu, dTime = s.SampleNano - prev.time;
                        if (dTime > 0 && dCpu >= 0)
                        {
                            c.CpuCores = (double)dCpu / dTime;
                            c.CpuPercent = 100.0 * c.CpuCores / Math.Max(1, host.Cores);
                        }
                    }
                    _prevContainerCpu[c.Id] = (s.CpuNano, s.SampleNano);
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
            foreach (var g in snap.Gpus)
            {
                if (!Gpu.TryGetValue(g.Index, out var gh)) Gpu[g.Index] = gh = (new List<Sample>(), new List<Sample>());
                if (g.Utilization.HasValue) Add(gh.Util, new Sample(t, g.Utilization.Value));
                if (g.MemPercent.HasValue) Add(gh.Mem, new Sample(t, g.MemPercent.Value));
            }

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

        private static string Shorten(string s, string empty) =>
            s.Length == 0 ? empty : s.Length > 200 ? s.Substring(0, 200) + "…" : s;

        private static void Add(List<Sample> list, Sample s)
        {
            list.Add(s);
            if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
        }
    }
}
