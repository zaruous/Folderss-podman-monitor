using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PodmanMonitor.Ssh
{
    /// <summary>접속에 필요한 최종 값. 별칭(예: kyj)을 ~/.ssh/config 로 풀어 얻는다.</summary>
    public sealed class SshTarget
    {
        public string Alias { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; } = 22;
        public string User { get; set; }
        public List<string> IdentityFiles { get; } = new List<string>();

        public override string ToString() => $"{User}@{HostName}:{Port}";
    }

    /// <summary>
    /// ~/.ssh/config 의 필요한 부분(Host·HostName·User·Port·IdentityFile)만 읽는다.
    /// OpenSSH 와 같이 위에서부터 처음 정해진 값이 이긴다. Match·Include 등은 지원하지 않는다.
    /// </summary>
    public static class SshConfigFile
    {
        public static string DefaultSshDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        public static SshTarget Resolve(string alias, string configText, string sshDirectory)
        {
            var target = new SshTarget { Alias = alias };
            bool active = false, anyIdentity = false;

            foreach (var raw in (configText ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(\S+?)\s*(?:=|\s)\s*(.+)$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value.ToLowerInvariant();
                var value = m.Groups[2].Value.Trim().Trim('"');

                if (key == "host")
                {
                    active = MatchesHost(alias, value);
                    continue;
                }
                if (!active) continue;

                switch (key)
                {
                    case "hostname": target.HostName ??= value; break;
                    case "user": target.User ??= value; break;
                    case "port":
                        if (target.Port == 22 && int.TryParse(value, out var port)) target.Port = port;
                        break;
                    case "identityfile":
                        target.IdentityFiles.Add(ExpandPath(value, sshDirectory));
                        anyIdentity = true;
                        break;
                }
            }

            target.HostName ??= alias;
            target.User ??= Environment.UserName;
            if (!anyIdentity)
                foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
                    target.IdentityFiles.Add(Path.Combine(sshDirectory, name));
            return target;
        }

        public static SshTarget Resolve(string alias)
        {
            var dir = DefaultSshDirectory;
            var path = Path.Combine(dir, "config");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            return Resolve(alias, text, dir);
        }

        /// <summary>Host 줄의 패턴(공백 구분, *·? 와일드카드, ! 부정)과 별칭을 맞춘다.</summary>
        internal static bool MatchesHost(string alias, string patterns)
        {
            bool matched = false;
            foreach (var p in patterns.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool negate = p.StartsWith("!");
                var pattern = negate ? p.Substring(1) : p;
                var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                if (Regex.IsMatch(alias, regex, RegexOptions.IgnoreCase))
                {
                    if (negate) return false;
                    matched = true;
                }
            }
            return matched;
        }

        internal static string ExpandPath(string value, string sshDirectory)
        {
            var home = Path.GetDirectoryName(sshDirectory.TrimEnd('\\', '/')) ?? "";
            if (value.StartsWith("~/") || value.StartsWith("~\\"))
                value = Path.Combine(home, value.Substring(2));
            value = value.Replace("%d", home);
            return Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>known_hosts 와 서버 키를 비교한다(평문·해시(|1|) 호스트 모두).</summary>
    public static class KnownHosts
    {
        public static string DefaultPath => Path.Combine(SshConfigFile.DefaultSshDirectory, "known_hosts");

        /// <param name="hostKeyBlob">SSH 형식 공개키 바이트(known_hosts 의 base64 부분을 디코딩한 것과 같다).</param>
        public static bool Contains(string knownHostsText, string host, int port, byte[] hostKeyBlob)
        {
            var names = port == 22 ? new[] { host } : new[] { $"[{host}]:{port}" };
            foreach (var raw in (knownHostsText ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;
                if (f[0].StartsWith("@")) continue; // @revoked·@cert-authority 는 쓰지 않는다

                if (!names.Any(n => HostFieldMatches(f[0], n))) continue;
                byte[] blob;
                try { blob = Convert.FromBase64String(f[2]); }
                catch (FormatException) { continue; }
                if (blob.AsSpan().SequenceEqual(hostKeyBlob)) return true;
            }
            return false;
        }

        internal static bool HostFieldMatches(string field, string name)
        {
            if (field.StartsWith("|1|"))
            {
                // |1|salt|hash — HMAC-SHA1(salt, 호스트 이름)
                var parts = field.Split('|');
                if (parts.Length != 4) return false;
                try
                {
                    var salt = Convert.FromBase64String(parts[2]);
                    var expected = Convert.FromBase64String(parts[3]);
                    using var hmac = new System.Security.Cryptography.HMACSHA1(salt);
                    return hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes(name)).AsSpan().SequenceEqual(expected);
                }
                catch (FormatException) { return false; }
            }
            return field.Split(',').Any(h => SshConfigFile.MatchesHost(name, h));
        }
    }
}
