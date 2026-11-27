using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PodmanMonitor.Ssh;

namespace PodmanMonitor.Monitoring
{
    /// <summary>프로세스 스냅샷의 한 줄.</summary>
    public sealed class ProcessInfo
    {
        public int Pid { get; set; }
        public string User { get; set; } = "";
        /// <summary>호스트 전체 대비 CPU % (100 = 모든 코어). 못 읽으면 null.</summary>
        public double? CpuPercent { get; set; }
        /// <summary>사용 코어 수(top 의 CPU % / 100).</summary>
        public double? CpuCores { get; set; }
        /// <summary>상주 메모리(바이트). Windows 는 작업 집합.</summary>
        public long Rss { get; set; }
        public double? MemPercent { get; set; }
        /// <summary>시작한 뒤 지난 시간. 못 읽으면 null.</summary>
        public TimeSpan? Elapsed { get; set; }
        public string Command { get; set; } = "";
        /// <summary>속한 컨테이너 id(cgroup 경로의 64자리 16진수). 컨테이너 밖이면 빈 문자열. Windows 는 늘 빈 문자열.</summary>
        public string ContainerId { get; set; } = "";
    }

    public sealed class ProcessSnapshot
    {
        public DateTime Time { get; set; }
        public int Cores { get; set; }
        public long MemTotal { get; set; }
        public List<ProcessInfo> Processes { get; } = new List<ProcessInfo>();
    }

    /// <summary>
    /// 서버의 프로세스를 한 번 찍는다(계속 갱신하지 않음).
    /// Linux: ps 의 %CPU 는 시작 후 평균이라 /proc/*/stat 의 누적 CPU 를 1초 간격으로 두 번 읽어 그 순간 사용률을 낸다.
    /// 속한 컨테이너는 /proc/*/cgroup 경로의 컨테이너 id(docker-…·libpod-…)로 안다.
    /// Windows: Get-Process 의 누적 CPU 시간을 1초 간격으로 두 번 읽는다. 명령줄은 Win32_Process, 사용자는 관리자일 때만.
    /// </summary>
    public static class ProcessScript
    {
        const string InfoMark = "@@PINFO@@", T1Mark = "@@PT1@@", T2Mark = "@@PT2@@", PsMark = "@@PPS@@", CgMark = "@@PCG@@", RowsMark = "@@PROWS@@";

        /// <summary>/proc/PID/stat 한 줄 → "PID utime+stime". 프로세스 이름(괄호 안)에 공백·괄호가 있을 수 있어 마지막 ") " 뒤부터 센다.</summary>
        public const string StatAwk = "{p=$1; s=$0; sub(/^.*\\) /,\"\",s); split(s,a,\" \"); print p, a[12]+a[13]}";

        public static string Linux { get; } = string.Join("\n", new[]
        {
            "n=$(nproc 2>/dev/null || echo 1); hz=$(getconf CLK_TCK 2>/dev/null || echo 100); m=$(awk '/^MemTotal:/{print $2}' /proc/meminfo)",
            // $$ 는 이 셸 — 스냅샷을 찍느라 띄운 ps·awk·sleep 등(이 셸의 자식)은 결과에서 뺀다
            "echo \"" + InfoMark + " $n $hz $m $$\"",
            "t(){ cut -d' ' -f1 /proc/uptime; cat /proc/[0-9]*/stat 2>/dev/null | awk '" + StatAwk + "'; }",
            "echo '" + T1Mark + "'; t; sleep 1; echo '" + T2Mark + "'; t",
            "echo '" + PsMark + "'; LC_ALL=C ps -eww -o pid=,ppid=,user:32=,rss=,etimes=,args=",
            "echo '" + CgMark + "'; grep -oE '[0-9a-f]{64}' /proc/[0-9]*/cgroup 2>/dev/null; true",
        });

        const string PsWindows = @"$n=[Environment]::ProcessorCount;$m=(Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize
""" + InfoMark + @" $n 0 $m $PID""
$cmd=@{};Get-CimInstance Win32_Process|%{$cmd[[int]$_.ProcessId]=$_.CommandLine}
$a=@{};Get-Process|%{$a[$_.Id]=$_.TotalProcessorTime.TotalSeconds}
$w=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Milliseconds 1000
try{$ps=Get-Process -IncludeUserName -EA Stop}catch{$ps=Get-Process}
$el=$w.Elapsed.TotalSeconds;$now=Get-Date
'" + RowsMark + @"'
foreach($p in $ps){if($p.Id -eq 0 -or $p.Id -eq $PID){continue}
$c1=$p.TotalProcessorTime.TotalSeconds;$c0=$a[$p.Id];$cpu=''
if($c0 -ne $null -and $c1 -ne $null){$cpu=[math]::Round(100*($c1-$c0)/$el,2)}
$up='';try{$up=[long]($now-$p.StartTime).TotalSeconds}catch{}
$c=$cmd[$p.Id];if(-not $c){$c=$p.ProcessName}
""$($p.Id)`t$($p.UserName)`t$cpu`t$($p.WorkingSet64)`t$up`t$($c -replace '[\t\r\n]',' ')""}
";

        public static string For(RemoteOs os) => os == RemoteOs.Windows ? RemoteScript.PowerShell(PsWindows) : Linux;

        static readonly Regex PsLine = new Regex(@"^\s*(\d+)\s+(\d+)\s+(\S+)\s+(\d+)\s+(\d+)\s?(.*)$", RegexOptions.Compiled);
        static readonly Regex CgLine = new Regex(@"^/proc/(\d+)/cgroup:([0-9a-f]{64})$", RegexOptions.Compiled);

        static double? Num(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

        /// <summary>출력 → 스냅샷. Linux·Windows 형식을 표시 줄로 알아본다. 프로세스를 하나도 못 읽으면 예외(메시지에 출력 앞부분).</summary>
        public static ProcessSnapshot Parse(string output, DateTime now)
        {
            var sections = new Dictionary<string, List<string>>();
            string info = null, current = null;
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith(InfoMark)) { info = raw.Substring(InfoMark.Length).Trim(); continue; }
                var t = raw.Trim();
                if (t == T1Mark || t == T2Mark || t == PsMark || t == CgMark || t == RowsMark)
                {
                    current = t;
                    sections[t] = new List<string>();
                    continue;
                }
                if (current != null && raw.Length > 0) sections[current].Add(raw);
            }
            if (info == null)
                throw new InvalidOperationException("프로세스 정보를 읽지 못했습니다: " + First(output));

            var f = info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int cores = f.Length > 0 && int.TryParse(f[0], out var n) && n > 0 ? n : 1;
            double hz = f.Length > 1 ? Num(f[1]) ?? 100 : 100;
            long memKb = f.Length > 2 && long.TryParse(f[2], out var mk) ? mk : 0;
            int self = f.Length > 3 && int.TryParse(f[3], out var sp) ? sp : -1;
            var snap = new ProcessSnapshot { Time = now, Cores = cores, MemTotal = memKb * 1024 };

            if (sections.TryGetValue(RowsMark, out var rows)) ParseWindows(rows, snap);
            else ParseLinux(sections, hz, self, snap);

            if (snap.Processes.Count == 0)
                throw new InvalidOperationException("프로세스 목록이 비었습니다: " + First(output));
            return snap;
        }

        static string First(string output)
        {
            var s = (output ?? "").Trim();
            return s.Length == 0 ? "출력 없음" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        static (double Uptime, Dictionary<int, long> Ticks) Ticks(List<string> lines)
        {
            var ticks = new Dictionary<int, long>();
            double up = lines != null && lines.Count > 0 ? Num(lines[0].Trim()) ?? 0 : 0;
            foreach (var l in (lines ?? new List<string>()).Skip(1))
            {
                var p = l.Trim().Split(' ');
                if (p.Length == 2 && int.TryParse(p[0], out var pid) && long.TryParse(p[1], out var tk)) ticks[pid] = tk;
            }
            return (up, ticks);
        }

        static void ParseLinux(Dictionary<string, List<string>> sec, double hz, int self, ProcessSnapshot snap)
        {
            sec.TryGetValue(T1Mark, out var l1);
            sec.TryGetValue(T2Mark, out var l2);
            var (up1, t1) = Ticks(l1);
            var (up2, t2) = Ticks(l2);
            double span = up2 - up1 > 0.05 ? up2 - up1 : 1;

            var containerOf = new Dictionary<int, string>();
            if (sec.TryGetValue(CgMark, out var cg))
                foreach (var l in cg)
                {
                    var m = CgLine.Match(l.Trim());
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pid) && !containerOf.ContainsKey(pid)) containerOf[pid] = m.Groups[2].Value;
                }

            if (!sec.TryGetValue(PsMark, out var ps)) return;
            foreach (var l in ps)
            {
                var m = PsLine.Match(l);
                if (!m.Success) continue;
                int pid = int.Parse(m.Groups[1].Value), ppid = int.Parse(m.Groups[2].Value);
                if (pid == self || ppid == self) continue;
                long rssKb = long.Parse(m.Groups[4].Value);
                var p = new ProcessInfo
                {
                    Pid = pid,
                    User = m.Groups[3].Value,
                    Rss = rssKb * 1024,
                    MemPercent = snap.MemTotal > 0 ? 100.0 * rssKb * 1024 / snap.MemTotal : (double?)null,
                    Elapsed = TimeSpan.FromSeconds(long.Parse(m.Groups[5].Value)),
                    Command = m.Groups[6].Value.Trim(),
                    ContainerId = containerOf.TryGetValue(pid, out var cid) ? cid : "",
                };
                if (t1.TryGetValue(pid, out var a) && t2.TryGetValue(pid, out var b) && b >= a)
                {
                    p.CpuCores = (b - a) / hz / span;
                    p.CpuPercent = p.CpuCores / snap.Cores * 100;
                }
                snap.Processes.Add(p);
            }
        }

        static void ParseWindows(List<string> rows, ProcessSnapshot snap)
        {
            foreach (var l in rows)
            {
                var f = l.Split('\t');
                if (f.Length < 6 || !int.TryParse(f[0], out var pid)) continue;
                long.TryParse(f[3], out var ws);
                var cpu = Num(f[2]);
                var up = Num(f[4]);
                snap.Processes.Add(new ProcessInfo
                {
                    Pid = pid,
                    User = f[1],
                    CpuCores = cpu / 100,
                    CpuPercent = cpu / snap.Cores,
                    Rss = ws,
                    MemPercent = snap.MemTotal > 0 ? 100.0 * ws / snap.MemTotal : (double?)null,
                    Elapsed = up.HasValue ? TimeSpan.FromSeconds(up.Value) : (TimeSpan?)null,
                    Command = string.Join("\t", f.Skip(5)).Trim(),
                });
            }
        }
    }
}
