using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PodmanMonitor.Ssh;

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
            // hostname 명령이 없는 배포판도 있다(podman machine 의 Fedora 등) — uname -n 은 어디에나 있다
            "  echo \"@@OK@@ $(uname -n) · $m\"",
            "else echo \"podman 을 실행하지 못했습니다: $(podman info 2>&1 | grep -m1 -i error)\"; fi",
        });

        // ---------- Windows(PowerShell) ----------
        // Windows OpenSSH 의 로그인 셸은 cmd(또는 PowerShell)라 sh 스크립트가 돌지 않는다.
        // 같은 일을 PowerShell 5.1 스크립트로 하고, 출력은 Linux 와 같은 표시·줄 형식으로 내서 해석기는 그대로 쓴다.
        // podman.exe 는 podman machine(WSL VM)에 붙으므로 JSON 은 Linux 와 같다.

        /// <summary>모든 Windows 스크립트 앞부분: UTF-8(BOM 없이) 출력, podman 실행 도우미(표준 출력·오류를 나눠 받는다).</summary>
        const string PsPrelude = @"$ErrorActionPreference='Continue';$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false
function Invoke-Podman{$o=& podman @args 2>&1;$c=$LASTEXITCODE
$e=@($o|?{$_ -is [Management.Automation.ErrorRecord]}|%{""$_""});$s=@($o|?{$_ -isnot [Management.Automation.ErrorRecord]}|%{""$_""})
@{ok=($c -eq 0);code=$c;out=($s -join ""`n"");err=$e}}
";

        const string PsCollect = @"if(-not(Get-Command podman -EA 0)){'" + ErrMark + @" 서버에 podman 이 없습니다';exit}
$r=Invoke-Podman ps -a --pod --format json
if($r.ok){'" + PsMark + @"';$r.out}else{""" + ErrMark + @" podman ps 실패: $(($r.err|select -f 3) -join ' ')""}
$r=Invoke-Podman stats --no-stream --format '{{json .}}'
if($r.ok){'" + StatsMark + @"';$r.out}else{$m=@($r.err|?{$_ -match 'error'})+$r.err|select -f 1;""" + WarnMark + @" 컨테이너 통계를 읽지 못했습니다: $m""}
'" + HostMark + @"'
$c=Get-CimInstance Win32_PerfRawData_PerfOS_Processor -Filter ""Name='_Total'""
""cpu $($c.PercentUserTime) 0 $($c.PercentPrivilegedTime) $($c.PercentIdleTime) 0 0 0 0""
$m=Get-CimInstance Win32_OperatingSystem
""MemTotal: $($m.TotalVisibleMemorySize) kB"";""MemAvailable: $($m.FreePhysicalMemory) kB""
[Environment]::ProcessorCount
'" + DiskMark + @"'
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'|?{$_.Size}|%{$u=$_.Size-$_.FreeSpace;""$($_.DeviceID) $($_.Size) $u $($_.FreeSpace) $([int](100*$u/$_.Size))% $($_.DeviceID)\""}
";
        // /proc/stat 자리: user nice system idle iowait irq softirq steal — 성능 카운터의 사용자·커널·유휴 시간(100ns 누적)을 넣는다.
        // load average 는 Windows 에 없어 내지 않는다.

        const string PsProbe = @"if(-not(Get-Command podman -EA 0)){'서버에 podman 이 없습니다';exit}
$r=Invoke-Podman info --format '{{.Version.Version}} {{.Host.Security.Rootless}} {{.Host.CgroupsVersion}}'
if($r.ok){$v,$rl,$cg=$r.out.Trim() -split ' +';$m=""Podman $v""
if($rl -eq 'true'){$m+=' · rootless'}else{$m+=' · rootful'};$m+="" · cgroup $cg""
if($rl -eq 'true' -and $cg -eq 'v1'){$m+=' (rootless + cgroup v1 이라 컨테이너 CPU·메모리 통계를 볼 수 없습니다)'}
""@@OK@@ $env:COMPUTERNAME · Windows · $m""}else{""podman 을 실행하지 못했습니다: $(@($r.err|?{$_ -match 'error'})+$r.err|select -f 1)""}
";

        /// <summary>Windows 에서 실행할 명령: PowerShell 스크립트를 UTF-16 base64 로 넘긴다(cmd·PowerShell 어느 로그인 셸에서도 같다).
        /// \r 은 앞부분(PsPrelude)까지 모두 지운다 — core.autocrlf=true 로 받은 소스는 여러 줄 문자열에 CRLF 가 들어간다.</summary>
        public static string PowerShell(string script)
        {
            var command = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes((PsPrelude + script).Replace("\r", "")));
            // cmd 한 줄은 8191자까지
            if (command.Length > 8000) throw new ArgumentException("한 번에 보내기에 명령이 너무 깁니다. 컨테이너를 나눠서 조작하세요.");
            return command;
        }

        public static string CollectFor(RemoteOs os) => os == RemoteOs.Windows ? PowerShell(PsCollect) : Collect;
        public static string ProbeFor(RemoteOs os) => os == RemoteOs.Windows ? PowerShell(PsProbe) : Probe;

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
        public static string ContainerSteps(IEnumerable<ContainerStep> steps, RemoteOs os = RemoteOs.Posix)
        {
            var list = steps.ToList();
            if (list.Count == 0) throw new ArgumentException("조작할 컨테이너가 없습니다");
            foreach (var s in list)
            {
                if (!Actions.Contains(s.Action)) throw new ArgumentException("지원하지 않는 조작: " + s.Action);
                if (string.IsNullOrEmpty(s.Id) || !s.Id.All(Uri.IsHexDigit)) throw new ArgumentException("컨테이너 id 가 올바르지 않습니다");
            }
            if (os == RemoteOs.Windows)
            {
                // cmd 한 줄 길이 제한 때문에 id 는 앞 12자리만(podman 은 겹치지 않는 앞부분을 받는다). 결과는 순서로 맞추므로 상관없다
                var items = string.Join(",", list.Select(s => "'" + s.Id.Substring(0, Math.Min(12, s.Id.Length)) + ":" + s.Action + "'"));
                return PowerShell(@"foreach($x in @(" + items + @")){$id,$a=$x -split ':',2
if($a -eq '" + ComposeOrder.WaitHealthy + @"'){$r='timeout';for($i=0;$i -lt " + HealthWaitSeconds + @";$i++){$null=& podman healthcheck run $id 2>&1;if($LASTEXITCODE -eq 0){$r='healthy';break};Start-Sleep 1}}
else{$o=Invoke-Podman $a $id;if($o.ok){$r='ok'}else{$r=""exit$($o.code)"";[Console]::Error.WriteLine(($o.err -join ' '))}}
""$id $a $r""}
");
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

        public static string Logs(string id, int tail, RemoteOs os = RemoteOs.Posix)
        {
            if (string.IsNullOrEmpty(id) || !id.All(Uri.IsHexDigit)) throw new ArgumentException("컨테이너 id 가 올바르지 않습니다");
            // Windows: 표준 오류(컨테이너의 stderr 로그)도 순서대로 한 줄씩 문자열로
            return os == RemoteOs.Windows
                ? PowerShell($"& podman logs --tail {tail} --timestamps {id} 2>&1|%{{\"$_\"}}")
                : $"podman logs --tail {tail} --timestamps {id} 2>&1";
        }

        /// <summary>셸 작은따옴표 인용. 안의 ' 는 '\'' 로 바꾼다.</summary>
        public static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        /// <summary>PowerShell 작은따옴표 문자열. PowerShell 은 ‘ ’ ‚ ‛ 도 작은따옴표로 보므로 같이 두 번 쓴다.</summary>
        public static string PsQuote(string s) =>
            "'" + System.Text.RegularExpressions.Regex.Replace(s ?? "", "['‘’‚‛]", m => m.Value + m.Value) + "'";

        /// <summary>id 는 podman 이 준 16진수만 받는다 — 셸에 그대로 들어가므로.</summary>
        public static string CheckId(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.All(Uri.IsHexDigit)) throw new ArgumentException("컨테이너 id 가 올바르지 않습니다");
            return id;
        }

        const string ExecMark = "@@EXEC@@";

        /// <summary>컨테이너 안에서 돌릴 sh 스크립트: 직전 작업 폴더로 옮기고, 끝에 종료 코드와 새 작업 폴더를 표시 줄로 붙인다.</summary>
        static string ExecScript(string command, string cwd) =>
            (string.IsNullOrEmpty(cwd) ? "" : "cd " + ShellQuote(cwd) + " 2>/dev/null\n")
            + command + "\nrc=$?; printf '\\n" + ExecMark + " %s %s\\n' \"$rc\" \"$(pwd)\"";

        /// <summary>
        /// 컨테이너 안에서 명령 한 번 실행(podman exec, tty 없음). 실행마다 새 셸이라 cd 는 cwd 로 이어 준다.
        /// Windows: PowerShell 5.1 은 네이티브 프로그램 인자 속 큰따옴표를 이스케이프하지 않아 sh -c 로 넘기면 깨지므로
        /// 스크립트를 표준 입력으로 넘긴다. 전체를 { } 로 묶어 다 읽은 뒤 실행하고(명령이 나머지 스크립트를 읽어 가지 않게) 표준 입력은 /dev/null,
        /// 파이프가 끝에 붙이는 CRLF 는 마지막 줄을 주석(#)으로 두어 흘린다.
        /// </summary>
        public static string Exec(string id, string command, string cwd, RemoteOs os = RemoteOs.Posix)
        {
            CheckId(id);
            var script = ExecScript(command, cwd);
            if (os != RemoteOs.Windows) return $"podman exec {id} sh -c {ShellQuote(script)} 2>&1";
            var piped = "{\n" + script + "\n} </dev/null\n#";
            return PowerShell("$OutputEncoding=New-Object Text.UTF8Encoding $false\n"
                + PsQuote(piped) + "|& podman exec -i " + id + " sh 2>&1|%{\"$_\"}\nexit $LASTEXITCODE");
        }

        /// <summary>Exec 출력 → (본문, 종료 코드, 작업 폴더). 표시 줄이 없으면(exit 등) 코드·폴더는 null.</summary>
        public static (string Output, int? ExitCode, string Cwd) ParseExec(string output)
        {
            output = (output ?? "").Replace("\r\n", "\n");
            var at = output.LastIndexOf("\n" + ExecMark + " ", StringComparison.Ordinal);
            if (at < 0) return (output, null, null);
            var tail = output.Substring(at + ExecMark.Length + 2).TrimEnd('\n');
            var sp = tail.IndexOf(' ');
            int? code = sp > 0 && int.TryParse(tail.Substring(0, sp), out var c) ? c : (int?)null;
            return (output.Substring(0, at), code, sp > 0 ? tail.Substring(sp + 1) : null);
        }

        /// <summary>
        /// 대화형 터미널용 원격 명령: bash 가 있으면 bash, 없으면 sh.
        /// 큰따옴표만 쓰고 $ 가 없어서 서버 로그인 셸이 sh·cmd·PowerShell 어느 것이어도 같은 뜻이 된다.
        /// </summary>
        public static string InteractiveShell(string id) =>
            $"podman exec -it {CheckId(id)} sh -c \"command -v bash >/dev/null 2>&1 && exec bash || exec sh\"";

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

            foreach (var raw in (output ?? "").Replace("\r", "").TrimStart('﻿').Split('\n'))
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
                items.Add($"{(ip == "0.0.0.0" || ip == "::" || ip == "" ? "" : ip + ":")}{Span(pub)}→{Span(priv)}/{proto}");
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

        /// <summary>LC_ALL=C df -P -B1: 장치 크기 사용 가용 용량% 마운트(공백 포함 가능). Windows 스크립트는 장치 자리에 드라이브(C:)를 같은 형식으로 낸다.</summary>
        public static List<DiskInfo> ParseDf(IEnumerable<string> lines) =>
            lines.Where(l => l.StartsWith("/") || (l.Length > 2 && char.IsLetter(l[0]) && l[1] == ':' && l[2] == ' '))
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
