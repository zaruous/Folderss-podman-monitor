using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PodmanMonitor.Monitoring
{
    /// <summary>
    /// 갱신 한 번에 서버에서 실행할 셸 스크립트와 그 출력 해석.
    /// Docker Engine API 는 서버의 유닉스 소켓을 curl 로 부른다(SSH 위 Docker API). 호스트 지표는 /proc·df 에서 읽는다.
    /// </summary>
    public static class RemoteScript
    {
        public const string ApiVersion = "v1.44"; // Docker 25+ (one-shot stats 지원)
        const string PsMark = "@@PS@@", StatsMark = "@@STATS@@", HostMark = "@@HOST@@", DiskMark = "@@DISK@@", ErrMark = "@@ERR@@";

        public static string Collect { get; } = string.Join("\n", new[]
        {
            "S=/var/run/docker.sock",
            "command -v curl >/dev/null 2>&1 || echo '" + ErrMark + " 서버에 curl 이 없습니다'",
            "[ -w \"$S\" ] || echo '" + ErrMark + " docker.sock 에 쓸 권한이 없습니다(docker 그룹 확인)'",
            "api(){ curl -s --max-time 5 --unix-socket \"$S\" \"http://localhost/" + ApiVersion + "$1\"; }",
            "echo '" + PsMark + "'; api '/containers/json?all=1'; echo",
            "for id in $(api '/containers/json' | grep -o '\"Id\":\"[0-9a-f]*\"' | cut -d'\"' -f4); do",
            "  echo '" + StatsMark + "'; api \"/containers/$id/stats?stream=false&one-shot=true\"; echo",
            "done",
            "echo '" + HostMark + "'; head -1 /proc/stat; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; cat /proc/loadavg; nproc",
            "echo '" + DiskMark + "'; LC_ALL=C df -P -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs",
        });

        /// <summary>연결 시험: 성공하면 "@@OK@@ 호스트이름 · Docker 버전".</summary>
        public static string Probe { get; } = string.Join("\n", new[]
        {
            "command -v curl >/dev/null 2>&1 || { echo '서버에 curl 이 없습니다'; exit 0; }",
            "v=$(curl -s --max-time 5 --unix-socket /var/run/docker.sock http://localhost/version | grep -o '\"Version\":\"[^\"]*\"' | head -1 | cut -d'\"' -f4)",
            "if [ -n \"$v\" ]; then echo \"@@OK@@ $(hostname) · Docker $v\"; else echo 'docker.sock 에 접근할 수 없습니다(docker 그룹 확인)'; fi",
        });

        public const string ComposeProjectLabel = "com.docker.compose.project";
        public const string ComposeServiceLabel = "com.docker.compose.service";
        public const string ComposeDependsOnLabel = "com.docker.compose.depends_on";
        static readonly HashSet<string> Actions = new HashSet<string> { "start", "stop", "restart", ComposeOrder.WaitHealthy };

        /// <summary>건강해지기를 기다리는 최대 시간(초).</summary>
        public const int HealthWaitSeconds = 90;

        /// <summary>
        /// 조작 단계를 차례로 실행한다(exec 채널 하나). 출력은 단계마다 한 줄 "id 조작 결과".
        /// 결과는 HTTP 코드(204 완료·304 이미 그 상태), wait-healthy 는 healthy 또는 timeout.
        /// id 는 Docker API 가 준 16진수만 받는다 — 셸에 그대로 들어가므로.
        /// </summary>
        public static string ContainerSteps(IEnumerable<ContainerStep> steps)
        {
            var list = steps.ToList();
            if (list.Count == 0) throw new ArgumentException("조작할 컨테이너가 없습니다");
            foreach (var s in list)
            {
                if (!Actions.Contains(s.Action)) throw new ArgumentException("지원하지 않는 조작: " + s.Action);
                if (string.IsNullOrEmpty(s.Id) || !s.Id.All(Uri.IsHexDigit)) throw new ArgumentException("컨테이너 id 가 올바르지 않습니다");
            }
            return string.Join("\n", new[]
            {
                "S=/var/run/docker.sock; B=http://localhost/" + ApiVersion + "/containers",
                "for x in " + string.Join(" ", list) + "; do",
                "  id=${x%%:*}; a=${x#*:}",
                "  if [ \"$a\" = " + ComposeOrder.WaitHealthy + " ]; then",
                "    r=timeout; i=0",
                "    while [ $i -lt " + HealthWaitSeconds + " ]; do",
                "      curl -s --max-time 5 --unix-socket $S \"$B/$id/json\" | grep -q '\"Status\":\"healthy\"' && { r=healthy; break; }",
                "      sleep 1; i=$((i+1))",
                "    done",
                "  else",
                "    r=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 -X POST --unix-socket $S \"$B/$id/$a\")",
                "  fi",
                "  echo \"$id $a $r\"",
                "done",
            });
        }

        /// <summary>ContainerSteps 출력 → 단계별 (id, 조작, 결과). 실행되지 못한 단계는 빠진다.</summary>
        public static List<(string Id, string Action, string Result)> ParseStepResults(string output)
        {
            var result = new List<(string, string, string)>();
            foreach (var line in (output ?? "").Split('\n'))
            {
                var parts = line.Trim().Split(' ');
                if (parts.Length == 3 && parts[0].Length > 0) result.Add((parts[0], parts[1], parts[2]));
            }
            return result;
        }

        /// <summary>단계 결과가 성공인지(204 완료, 304 이미 그 상태, healthy).</summary>
        public static bool StepSucceeded(string result) => result == "204" || result == "304" || result == "healthy";

        /// <summary>로그는 API 응답이 멀티플렉스 바이너리라 docker CLI 로 받는다.</summary>
        public static string Logs(string id, int tail) => $"docker logs --tail {tail} --timestamps {id} 2>&1";

        /// <summary>원시 출력을 구획별로 나눈다.</summary>
        public sealed class Sections
        {
            public string ContainersJson;
            public List<string> StatsJson = new List<string>();
            public List<string> HostLines = new List<string>();
            public List<string> DiskLines = new List<string>();
            public List<string> Errors = new List<string>();
        }

        public static Sections Split(string output)
        {
            var s = new Sections();
            string current = null;
            var buf = new List<string>();

            void Flush()
            {
                var text = string.Join("\n", buf).Trim();
                switch (current)
                {
                    case PsMark: s.ContainersJson = text; break;
                    case StatsMark: if (text.Length > 0) s.StatsJson.Add(text); break;
                    case HostMark: s.HostLines.AddRange(buf.Select(l => l.Trim()).Where(l => l.Length > 0)); break;
                    case DiskMark: s.DiskLines.AddRange(buf.Select(l => l.Trim()).Where(l => l.Length > 0)); break;
                }
                buf.Clear();
            }

            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith(ErrMark)) { s.Errors.Add(raw.Substring(ErrMark.Length).Trim()); continue; }
                if (raw == PsMark || raw == StatsMark || raw == HostMark || raw == DiskMark)
                {
                    Flush();
                    current = raw;
                    continue;
                }
                buf.Add(raw);
            }
            Flush();
            return s;
        }

        // ---------- 컨테이너 ----------

        public static List<ContainerInfo> ParseContainers(string json)
        {
            var list = new List<ContainerInfo>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Docker API 응답이 목록이 아닙니다: " + Truncate(json));
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var names = c.TryGetProperty("Names", out var n) && n.ValueKind == JsonValueKind.Array
                    ? n.EnumerateArray().Select(x => x.GetString()?.TrimStart('/')).ToList()
                    : new List<string>();
                list.Add(new ContainerInfo
                {
                    Id = Str(c, "Id"),
                    Name = names.FirstOrDefault() ?? Str(c, "Id").Substring(0, 12),
                    Image = Str(c, "Image"),
                    State = Str(c, "State"),
                    Status = Str(c, "Status"),
                    Ports = FormatPorts(c),
                });
                if (c.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                {
                    var info = list[list.Count - 1];
                    info.Project = Str(labels, ComposeProjectLabel);
                    info.Service = Str(labels, ComposeServiceLabel);
                    info.DependsOn = ComposeOrder.ParseDependsOn(Str(labels, ComposeDependsOnLabel));
                }
            }
            return list;
        }

        static string FormatPorts(JsonElement c)
        {
            if (!c.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array) return "";
            var items = new List<string>();
            foreach (var p in ports.EnumerateArray())
            {
                var priv = p.TryGetProperty("PrivatePort", out var pp) ? pp.GetInt32() : 0;
                var type = Str(p, "Type");
                if (p.TryGetProperty("PublicPort", out var pub))
                {
                    var ip = Str(p, "IP");
                    if (ip == "::") continue; // IPv4·IPv6 가 같은 포트를 두 번 알려 주므로 하나만
                    items.Add($"{(ip == "0.0.0.0" || ip == "" ? "" : ip + ":")}{pub.GetInt32()}→{priv}/{type}");
                }
                else items.Add($"{priv}/{type}");
            }
            return string.Join(", ", items.Distinct());
        }

        /// <summary>stats 한 건에서 누적값을 꺼낸다. CPU % 는 직전 표본과의 차이로 <see cref="Collector"/> 가 계산한다.</summary>
        public sealed class RawStats
        {
            public string Id;
            public long CpuTotal, SystemCpu;
            public int OnlineCpus;
            public long MemUsed, MemLimit, NetRx, NetTx, BlockRead, BlockWrite, Pids;
        }

        public static RawStats ParseStats(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var s = new RawStats { Id = Str(r, "id") };

            if (r.TryGetProperty("cpu_stats", out var cpu))
            {
                if (cpu.TryGetProperty("cpu_usage", out var u) && u.TryGetProperty("total_usage", out var t)) s.CpuTotal = t.GetInt64();
                if (cpu.TryGetProperty("system_cpu_usage", out var sys)) s.SystemCpu = sys.GetInt64();
                if (cpu.TryGetProperty("online_cpus", out var on)) s.OnlineCpus = on.GetInt32();
            }
            if (r.TryGetProperty("memory_stats", out var mem))
            {
                long usage = Long(mem, "usage");
                long cache = 0;
                if (mem.TryGetProperty("stats", out var ms))
                    cache = ms.TryGetProperty("inactive_file", out var inf) ? inf.GetInt64()      // cgroup v2
                          : ms.TryGetProperty("total_inactive_file", out var tif) ? tif.GetInt64() // cgroup v1
                          : 0;
                // docker stats CLI 와 같은 식: usage - inactive_file
                s.MemUsed = Math.Max(0, usage - cache);
                s.MemLimit = Long(mem, "limit");
            }
            if (r.TryGetProperty("networks", out var nets) && nets.ValueKind == JsonValueKind.Object)
                foreach (var n in nets.EnumerateObject())
                {
                    s.NetRx += Long(n.Value, "rx_bytes");
                    s.NetTx += Long(n.Value, "tx_bytes");
                }
            if (r.TryGetProperty("blkio_stats", out var blk) &&
                blk.TryGetProperty("io_service_bytes_recursive", out var io) && io.ValueKind == JsonValueKind.Array)
                foreach (var e in io.EnumerateArray())
                {
                    var op = Str(e, "op").ToLowerInvariant();
                    if (op == "read") s.BlockRead += Long(e, "value");
                    else if (op == "write") s.BlockWrite += Long(e, "value");
                }
            if (r.TryGetProperty("pids_stats", out var pids)) s.Pids = Long(pids, "current");
            return s;
        }

        // ---------- 호스트 ----------

        /// <summary>/proc/stat 첫 줄의 user nice system idle iowait irq softirq steal.</summary>
        public static long[] ParseCpuLine(IEnumerable<string> hostLines)
        {
            var line = hostLines.FirstOrDefault(l => l.StartsWith("cpu "));
            if (line == null) return null;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return v.Length == 8 ? v : null;
        }

        /// <summary>두 /proc/stat 표본 사이의 CPU 사용률·iowait(%).</summary>
        public static (double usage, double iowait)? CpuBetween(long[] prev, long[] cur)
        {
            if (prev == null || cur == null) return null;
            long total = cur.Sum() - prev.Sum();
            if (total <= 0) return null;
            long idle = (cur[3] + cur[4]) - (prev[3] + prev[4]);
            long iowait = cur[4] - prev[4];
            return (100.0 * (total - idle) / total, 100.0 * iowait / total);
        }

        public static HostInfo ParseHost(List<string> lines)
        {
            long Kb(string key) => lines.Where(l => l.StartsWith(key + ":"))
                .Select(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024)
                .FirstOrDefault();
            var load = lines.FirstOrDefault(l => l.Count(ch => ch == '/') == 1 && l.Split(' ').Length == 5);
            var cores = lines.LastOrDefault(l => int.TryParse(l, out _));
            return new HostInfo
            {
                MemTotal = Kb("MemTotal"),
                MemAvailable = Kb("MemAvailable"),
                LoadAverage = load == null ? "" : string.Join(" ", load.Split(' ').Take(3)),
                Cores = cores == null ? 1 : Math.Max(1, int.Parse(cores, CultureInfo.InvariantCulture)),
            };
        }

        /// <summary>LC_ALL=C df -P -B1: 장치 크기 사용 가용 용량% 마운트(공백 포함 가능).</summary>
        public static List<DiskInfo> ParseDf(IEnumerable<string> lines) =>
            lines.Where(l => l.StartsWith("/"))
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(f => f.Length >= 6)
                .Select(f => new DiskInfo
                {
                    Device = f[0],
                    Size = long.Parse(f[1], CultureInfo.InvariantCulture),
                    Used = long.Parse(f[2], CultureInfo.InvariantCulture),
                    Avail = long.Parse(f[3], CultureInfo.InvariantCulture),
                    Mount = string.Join(" ", f.Skip(5)),
                })
                .ToList();

        static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static long Long(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

        static string Truncate(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }
}
