using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace PodmanMonitor.Monitoring
{
    /// <summary>
    /// 갱신 한 번에 서버에서 실행할 셸 스크립트와 그 출력 해석.
    /// Podman 은 데몬이 없으므로 접속한 계정으로 podman CLI 를 직접 부르고 JSON 출력을 읽는다(rootless 면 그 계정의 컨테이너).
    /// 호스트 지표는 /proc·df 에서 읽는다.
    /// </summary>
    public static class RemoteScript
    {
        const string PsMark = "@@PS@@", StatsMark = "@@STATS@@", HostMark = "@@HOST@@", DiskMark = "@@DISK@@", ErrMark = "@@ERR@@", WarnMark = "@@WARN@@";

        public static string Collect { get; } = string.Join("\n", new[]
        {
            "command -v podman >/dev/null 2>&1 || { echo '" + ErrMark + " 서버에 podman 이 없습니다'; exit 0; }",
            // 표준 오류는 따로 받는다 — rootless 는 성공해도 경고(WARN[0000] …)를 내서 JSON 에 섞이면 안 된다
            "e=$(mktemp) || exit 1; trap 'rm -f \"$e\"' EXIT",
            // --pod: PodName 을 채운다
            "if p=$(podman ps -a --pod --format json 2>\"$e\"); then echo '" + PsMark + "'; printf '%s\\n' \"$p\"",
            "else echo \"" + ErrMark + " podman ps 실패: $(head -3 \"$e\" | tr '\\n' ' ')\"; fi",
            // 실행 중인 컨테이너만, 한 줄에 하나씩 원시값(누적 CPU ns 등). 실패해도(rootless + cgroup v1 등) 나머지는 보여 준다
            "if s=$(podman stats --no-stream --format '{{json .}}' 2>\"$e\"); then echo '" + StatsMark + "'; printf '%s\\n' \"$s\"",
            "else echo \"" + WarnMark + " 컨테이너 통계를 읽지 못했습니다: $(grep -m1 -i error \"$e\" || head -1 \"$e\")\"; fi",
            "echo '" + HostMark + "'; head -1 /proc/stat; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo; cat /proc/loadavg; nproc",
            "echo '" + DiskMark + "'; LC_ALL=C df -P -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs",
        });

        /// <summary>연결 시험: 성공하면 "@@OK@@ 호스트이름 · Podman 버전 · rootless/rootful · cgroup 버전".</summary>
        public static string Probe { get; } = string.Join("\n", new[]
        {
            "command -v podman >/dev/null 2>&1 || { echo '서버에 podman 이 없습니다'; exit 0; }",
            "if i=$(podman info --format '{{.Version.Version}} {{.Host.Security.Rootless}} {{.Host.CgroupsVersion}}' 2>/dev/null); then",
            "  set -- $i; m=\"Podman $1\"",
            "  if [ \"$2\" = true ]; then m=\"$m · rootless\"; else m=\"$m · rootful\"; fi",
            "  m=\"$m · cgroup $3\"",
            "  [ \"$2\" = true ] && [ \"$3\" = v1 ] && m=\"$m (rootless + cgroup v1 이라 컨테이너 CPU·메모리 통계를 볼 수 없습니다)\"",
            "  echo \"@@OK@@ $(hostname) · $m\"",
            "else echo \"podman 을 실행하지 못했습니다: $(podman info 2>&1 | grep -m1 -i error)\"; fi",
        });

        public const string ComposeProjectLabel = "com.docker.compose.project";
        public const string ComposeServiceLabel = "com.docker.compose.service";
        public const string ComposeDependsOnLabel = "com.docker.compose.depends_on";
        /// <summary>podman-compose 가 붙이는 라벨(com.docker.compose.* 가 없을 때 쓴다).</summary>
        public const string PodmanComposeProjectLabel = "io.podman.compose.project";
        public const string PodmanComposeServiceLabel = "io.podman.compose.service";
        static readonly HashSet<string> Actions = new HashSet<string> { "start", "stop", "restart", ComposeOrder.WaitHealthy };

        /// <summary>건강해지기를 기다리는 최대 시간(초).</summary>
        public const int HealthWaitSeconds = 90;

        /// <summary>
        /// 조작 단계를 차례로 실행한다(exec 채널 하나). 출력은 단계마다 한 줄 "id 조작 결과".
        /// 결과는 ok(podman 종료 코드 0), exit코드(실패), wait-healthy 는 healthy 또는 timeout(검사가 없는 컨테이너도 timeout).
        /// podman 은 이미 그 상태여도(멈춘 것을 stop) 0 으로 끝난다. 실패 이유는 표준 오류로 나온다.
        /// id 는 podman 이 준 16진수만 받는다 — 셸에 그대로 들어가므로.
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
                "for x in " + string.Join(" ", list) + "; do",
                "  id=${x%%:*}; a=${x#*:}",
                "  if [ \"$a\" = " + ComposeOrder.WaitHealthy + " ]; then",
                "    r=timeout; i=0",
                "    while [ $i -lt " + HealthWaitSeconds + " ]; do",
                // 상태를 읽기만 하면 안 된다: podman 의 주기 검사는 systemd 타이머로 돌아서, systemd 사용자 세션이 없으면(rootless 에 linger 없음 등)
                // 영영 starting 에 머문다. 검사를 직접 돌린다(0 = 통과, 상태도 healthy 로 바뀜)
                "      podman healthcheck run \"$id\" >/dev/null 2>&1 && { r=healthy; break; }",
                "      sleep 1; i=$((i+1))",
                "    done",
                // podman start·stop 은 성공하면 이름을 출력하므로 버린다
                "  elif podman \"$a\" \"$id\" >/dev/null; then r=ok",
                "  else r=exit$?",
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

        /// <summary>단계 결과가 성공인지(ok, healthy).</summary>
        public static bool StepSucceeded(string result) => result == "ok" || result == "healthy";

        public static string Logs(string id, int tail) => $"podman logs --tail {tail} --timestamps {id} 2>&1";

        /// <summary>원시 출력을 구획별로 나눈다.</summary>
        public sealed class Sections
        {
            public string ContainersJson;
            public List<string> StatsJson = new List<string>();
            public List<string> HostLines = new List<string>();
            public List<string> DiskLines = new List<string>();
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        public static Sections Split(string output)
        {
            var s = new Sections();
            string current = null;
            var buf = new List<string>();

            void Flush()
            {
                var lines = buf.Select(l => l.Trim()).Where(l => l.Length > 0);
                switch (current)
                {
                    case PsMark: s.ContainersJson = string.Join("\n", buf).Trim(); break;
                    case StatsMark: s.StatsJson.AddRange(lines); break; // 컨테이너마다 한 줄
                    case HostMark: s.HostLines.AddRange(lines); break;
                    case DiskMark: s.DiskLines.AddRange(lines); break;
                }
                buf.Clear();
            }

            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith(ErrMark)) { s.Errors.Add(raw.Substring(ErrMark.Length).Trim()); continue; }
                if (raw.StartsWith(WarnMark)) { s.Warnings.Add(raw.Substring(WarnMark.Length).Trim()); continue; }
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

        /// <summary>podman ps -a --pod --format json. pod 의 infra 컨테이너는 뺀다(사용자가 띄운 것이 아니므로).</summary>
        public static List<ContainerInfo> ParseContainers(string json)
        {
            var list = new List<ContainerInfo>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("podman ps 응답이 목록이 아닙니다: " + Truncate(json));
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (c.TryGetProperty("IsInfra", out var infra) && infra.ValueKind == JsonValueKind.True) continue;
                var names = c.TryGetProperty("Names", out var n) && n.ValueKind == JsonValueKind.Array
                    ? n.EnumerateArray().Select(x => x.GetString()).ToList()
                    : new List<string>();
                var id = Str(c, "Id");
                var info = new ContainerInfo
                {
                    Id = id,
                    Name = names.FirstOrDefault() ?? id.Substring(0, Math.Min(12, id.Length)),
                    Image = Str(c, "Image"),
                    State = Str(c, "State"),
                    Status = Str(c, "Status"),
                    Ports = FormatPorts(c),
                    Pod = Str(c, "PodName"),
                };
                if (c.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                {
                    info.Project = FirstNonEmpty(Str(labels, ComposeProjectLabel), Str(labels, PodmanComposeProjectLabel));
                    info.Service = FirstNonEmpty(Str(labels, ComposeServiceLabel), Str(labels, PodmanComposeServiceLabel));
                    info.DependsOn = ComposeOrder.ParseDependsOn(Str(labels, ComposeDependsOnLabel));
                }
                list.Add(info);
            }
            return list;
        }

        /// <summary>podman 포트: [{host_ip, container_port, host_port, range, protocol}]. 게시하지 않은 포트는 목록에 없다.</summary>
        static string FormatPorts(JsonElement c)
        {
            if (!c.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array) return "";
            var items = new List<string>();
            foreach (var p in ports.EnumerateArray())
            {
                int priv = Int(p, "container_port"), pub = Int(p, "host_port"), range = Math.Max(1, Int(p, "range"));
                var ip = Str(p, "host_ip");
                var proto = Str(p, "protocol");
                string Span(int start) => range > 1 ? $"{start}-{start + range - 1}" : start.ToString(CultureInfo.InvariantCulture);
                items.Add($"{(ip == "0.0.0.0" || ip == "" ? "" : ip + ":")}{Span(pub)}→{Span(priv)}/{proto}");
            }
            return string.Join(", ", items.Distinct());
        }

        /// <summary>stats 한 건(podman stats --format '{{json .}}')의 누적값. CPU % 는 직전 표본과의 차이로 <see cref="Collector"/> 가 계산한다.</summary>
        public sealed class RawStats
        {
            public string Id;
            /// <summary>컨테이너가 쓴 CPU 시간 누적(ns, 모든 코어 합).</summary>
            public long CpuNano;
            /// <summary>표본을 잰 시각(유닉스 ns).</summary>
            public long SampleNano;
            public long MemUsed, MemLimit, NetRx, NetTx, BlockRead, BlockWrite, Pids;
        }

        public static RawStats ParseStats(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var s = new RawStats
            {
                Id = Str(r, "ContainerID"),
                CpuNano = Long(r, "CPUNano"),
                SampleNano = Long(r, "SystemNano"),
                MemUsed = Long(r, "MemUsage"),
                MemLimit = Long(r, "MemLimit"),
                NetRx = Long(r, "NetInput"),
                NetTx = Long(r, "NetOutput"),
                BlockRead = Long(r, "BlockInput"),
                BlockWrite = Long(r, "BlockOutput"),
                Pids = Long(r, "PIDs"),
            };
            // podman 5 는 NetInput/NetOutput 대신 인터페이스별 Network{이름:{RxBytes,TxBytes}} 를 준다
            if (r.TryGetProperty("Network", out var nets) && nets.ValueKind == JsonValueKind.Object)
            {
                s.NetRx = s.NetTx = 0;
                foreach (var n in nets.EnumerateObject())
                {
                    s.NetRx += Long(n.Value, "RxBytes");
                    s.NetTx += Long(n.Value, "TxBytes");
                }
            }
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

        static string FirstNonEmpty(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

        static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static long Long(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : 0;

        static int Int(JsonElement e, string key) => (int)Math.Min(int.MaxValue, Long(e, key));

        static string Truncate(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }
}
