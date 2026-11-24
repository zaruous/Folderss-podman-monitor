using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PodmanMonitor.Ssh
{
    /// <summary>
    /// 호스트 설정 팝업에서 등록하는 서버 하나. Host 만 채우면 ~/.ssh/config 별칭으로 풀고,
    /// 포트·사용자·개인키를 채우면 그 값이 별칭 설정보다 우선한다.
    /// </summary>
    public sealed class HostEntry
    {
        static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9._-]+$");

        /// <summary>콤보박스에 보이는 이름(목록 안에서 겹치면 안 됨).</summary>
        public string Name { get; set; } = "";
        /// <summary>~/.ssh/config 의 Host 별칭 또는 주소(IP·도메인).</summary>
        public string Host { get; set; } = "";
        public int? Port { get; set; }
        public string User { get; set; }
        public string IdentityFile { get; set; }

        public HostEntry Clone() => (HostEntry)MemberwiseClone();

        public override string ToString() => Name;

        /// <summary>문제가 있으면 사용자에게 보일 문구, 없으면 null.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "이름을 입력하세요.";
            if (Name.Length > 64) return "이름은 64자 이하로 입력하세요.";
            if (string.IsNullOrWhiteSpace(Host)) return "호스트(별칭 또는 주소)를 입력하세요.";
            if (!SafeName.IsMatch(Host)) return "호스트에는 영문·숫자·. _ - 만 쓸 수 있습니다.";
            if (Port.HasValue && (Port < 1 || Port > 65535)) return "포트는 1~65535 사이여야 합니다.";
            if (!string.IsNullOrEmpty(User) && !SafeName.IsMatch(User)) return "사용자에는 영문·숫자·. _ - 만 쓸 수 있습니다.";
            if (!string.IsNullOrEmpty(IdentityFile) && !File.Exists(IdentityFile)) return "개인키 파일이 없습니다: " + IdentityFile;
            return null;
        }

        /// <summary>~/.ssh/config 로 별칭을 풀고, 이 항목에 적힌 값으로 덮어쓴다.</summary>
        public SshTarget Resolve(string configText, string sshDirectory)
        {
            var t = SshConfigFile.Resolve(Host, configText, sshDirectory);
            if (Port.HasValue) t.Port = Port.Value;
            if (!string.IsNullOrWhiteSpace(User)) t.User = User;
            if (!string.IsNullOrWhiteSpace(IdentityFile))
            {
                t.IdentityFiles.Clear();
                t.IdentityFiles.Add(IdentityFile);
            }
            t.Alias = Name;
            return t;
        }

        public SshTarget Resolve()
        {
            var dir = SshConfigFile.DefaultSshDirectory;
            var path = Path.Combine(dir, "config");
            return Resolve(File.Exists(path) ? File.ReadAllText(path) : "", dir);
        }
    }

    /// <summary>호스트 목록을 플러그인 설정(문자열 사전)에 JSON 으로 저장한다.</summary>
    public static class HostStore
    {
        public const string HostsKey = "hosts";
        public const string SelectedKey = "selectedHost";
        const string LegacyKey = "host"; // 예전 단일 호스트 입력칸

        // 한글 이름을 \uXXXX 로 바꾸지 않고 그대로 저장한다(설정 파일을 사람이 읽을 수 있게)
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        };

        public static List<HostEntry> Parse(string json, string legacyHost)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    return (JsonSerializer.Deserialize<List<HostEntry>>(json) ?? new List<HostEntry>())
                        .Where(h => h != null && !string.IsNullOrWhiteSpace(h.Name)).ToList();
                }
                catch (JsonException) { /* 깨진 값은 비운 것으로 본다 */ }
            }
            // 예전 버전에서 쓰던 호스트가 있으면 첫 항목으로 옮겨 온다
            return string.IsNullOrWhiteSpace(legacyHost)
                ? new List<HostEntry>()
                : new List<HostEntry> { new HostEntry { Name = legacyHost, Host = legacyHost } };
        }

        public static string Serialize(IEnumerable<HostEntry> hosts) => JsonSerializer.Serialize(hosts.ToList(), Json);

        public static List<HostEntry> Load(Func<string, string> get) => Parse(get(HostsKey), get(LegacyKey));

        public static void Save(Action<string, string> set, IEnumerable<HostEntry> hosts)
        {
            set(HostsKey, Serialize(hosts));
            set(LegacyKey, null);
        }

        /// <summary>~/.ssh/config 의 Host 별칭(와일드카드 제외)을 목록으로.</summary>
        public static List<string> AliasesFromConfig(string configText)
        {
            var list = new List<string>();
            foreach (var raw in (configText ?? "").Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^Host\s+(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                foreach (var name in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (name.IndexOfAny(new[] { '*', '?', '!' }) < 0 && !list.Contains(name)) list.Add(name);
            }
            return list;
        }
    }
}
