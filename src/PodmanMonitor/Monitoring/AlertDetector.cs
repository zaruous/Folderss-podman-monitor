using System;
using System.Collections.Generic;
using System.Linq;

namespace PodmanMonitor.Monitoring
{
    public enum AlertLevel { Info, Warning, Critical }

    public sealed class Alert
    {
        public Alert(DateTime time, AlertLevel level, string message) { Time = time; Level = level; Message = message; }
        public DateTime Time { get; }
        public AlertLevel Level { get; }
        public string Message { get; }
        public string Icon => Level == AlertLevel.Critical ? "⛔" : Level == AlertLevel.Warning ? "⚠" : "ℹ";
        public override string ToString() => $"{Time:HH:mm:ss} {Icon} {Message}";
    }

    /// <summary>
    /// 직전 표본과 이번 표본을 비교해 알릴 변화를 찾는다(서버에 따로 묻지 않고 이미 받은 값만 쓴다).
    /// - 실행 중이던 컨테이너가 멈춤·재시작 반복·사라짐 / 다시 실행됨
    /// - healthy → unhealthy / unhealthy 에서 회복
    /// - 디스크·호스트 메모리가 경계값을 넘음
    /// 사용자가 방금 조작한 컨테이너는 <c>ignoreIds</c> 로 빼서 스스로 멈춘 것을 알리지 않는다.
    /// </summary>
    public static class AlertDetector
    {
        public const double DiskThreshold = 90;
        public const double MemThreshold = 90;

        public static List<Alert> Detect(Snapshot prev, Snapshot cur, ICollection<string> ignoreIds = null)
        {
            var alerts = new List<Alert>();
            if (prev == null || cur == null) return alerts;
            var t = cur.Time;
            bool Ignored(string id) => ignoreIds != null && id != null && ignoreIds.Contains(id);

            var before = prev.Containers.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            var now = cur.Containers.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());

            foreach (var c in cur.Containers)
            {
                if (!before.TryGetValue(c.Name, out var p) || Ignored(c.Id) || Ignored(p.Id)) continue;
                bool wasRunning = p.State == "running", isRunning = c.State == "running";
                if (wasRunning && c.State == "restarting")
                    alerts.Add(new Alert(t, AlertLevel.Critical, $"{c.Name} 재시작 반복 중 ({c.Status})"));
                else if (wasRunning && !isRunning)
                    alerts.Add(new Alert(t, AlertLevel.Critical, $"{c.Name} 멈춤 ({c.Status})"));
                else if (!wasRunning && isRunning)
                    alerts.Add(new Alert(t, AlertLevel.Info, $"{c.Name} 다시 실행됨"));

                bool wasUnhealthy = Unhealthy(p), isUnhealthy = Unhealthy(c);
                if (isRunning && !wasUnhealthy && isUnhealthy)
                    alerts.Add(new Alert(t, AlertLevel.Warning, $"{c.Name} unhealthy"));
                else if (isRunning && wasUnhealthy && !isUnhealthy)
                    alerts.Add(new Alert(t, AlertLevel.Info, $"{c.Name} unhealthy 에서 회복"));
            }
            foreach (var p in prev.Containers)
                if (!now.ContainsKey(p.Name) && p.State == "running" && !Ignored(p.Id))
                    alerts.Add(new Alert(t, AlertLevel.Critical, $"{p.Name} 사라짐 (실행 중이던 컨테이너가 목록에서 없어짐)"));

            var prevDisks = prev.Disks.GroupBy(d => d.Mount).ToDictionary(g => g.Key, g => g.First());
            foreach (var d in cur.Disks)
                if (prevDisks.TryGetValue(d.Mount, out var pd) && pd.Percent < DiskThreshold && d.Percent >= DiskThreshold)
                    alerts.Add(new Alert(t, AlertLevel.Warning, $"디스크 {d.Mount} {d.Percent:0.#}% 사용 (남은 공간 {FormatBytes(d.Avail)})"));

            if (prev.Host != null && cur.Host != null && prev.Host.MemTotal > 0 && cur.Host.MemTotal > 0 &&
                prev.Host.MemPercent < MemThreshold && cur.Host.MemPercent >= MemThreshold)
                alerts.Add(new Alert(t, AlertLevel.Warning, $"호스트 메모리 {cur.Host.MemPercent:0.#}% 사용"));
            return alerts;
        }

        private static bool Unhealthy(ContainerInfo c) => c.Status?.Contains("(unhealthy)") == true;

        private static string FormatBytes(long b)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{b}B" : $"{v:0.##}{units[i]}";
        }
    }
}
