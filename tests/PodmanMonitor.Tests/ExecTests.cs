using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class ExecTests
    {
        const string GitSh = @"C:\Program Files\Git\usr\bin\sh.exe";

        [Fact]
        public void ShellQuote_EscapesSingleQuote() =>
            Assert.Equal(@"'it'\''s'", RemoteScript.ShellQuote("it's"));

        [Fact]
        public void PsQuote_DoublesAllSingleQuoteKinds() =>
            Assert.Equal("'a''b\u2019\u2019c'", RemoteScript.PsQuote("a'b\u2019c"));

        [Fact]
        public void Exec_Posix_QuotesCommandAndCwd()
        {
            var s = RemoteScript.Exec("abc123", "echo 'hi' && ls", "/app");
            Assert.StartsWith("podman exec abc123 sh -c '", s);
            Assert.Contains(@"cd '\''/app'\''", s);
            Assert.Contains(@"echo '\''hi'\'' && ls", s);
            Assert.EndsWith("2>&1", s);
        }

        [Fact]
        public void Exec_Windows_PipesScriptToStdin()
        {
            var ps = Decode(RemoteScript.Exec("abc123", "echo \"x\"", null, RemoteOs.Windows));
            Assert.Contains("|& podman exec -i abc123 sh 2>&1", ps);
            Assert.Contains("echo \"x\"", ps); // 큰따옴표는 인자가 아니라 표준 입력으로 가므로 그대로
            Assert.DoesNotContain("\r", ps);
        }

        [Theory]
        [InlineData("abc;rm")]
        [InlineData("")]
        [InlineData(null)]
        public void Exec_RejectsBadId(string id) =>
            Assert.Throws<ArgumentException>(() => RemoteScript.Exec(id, "ls", null));

        [Fact]
        public void ParseExec_ReadsCodeAndCwd()
        {
            var (text, code, cwd) = RemoteScript.ParseExec("a\nb\n\n@@EXEC@@ 2 /var/lib/my dir\n");
            Assert.Equal("a\nb\n", text);
            Assert.Equal(2, code);
            Assert.Equal("/var/lib/my dir", cwd);
        }

        [Fact]
        public void ParseExec_NoMarker()
        {
            var (text, code, cwd) = RemoteScript.ParseExec("bye\n");
            Assert.Equal("bye\n", text);
            Assert.Null(code);
            Assert.Null(cwd);
        }

        const string Sample = "echo \"it's ok\"; cd /tmp; cat; false";

        /// <summary>Linux 서버용 명령을 실제 sh 로 돌린다(podman exec 부분만 뺌). Git for Windows 의 sh 가 없으면 건너뜀.</summary>
        [Fact]
        public void Exec_Posix_RunsInRealShell()
        {
            if (!File.Exists(GitSh)) return;
            var script = RemoteScript.Exec("abc", Sample, "/").Substring("podman exec abc ".Length);
            var output = Run(GitSh, "-c", script);
            AssertSample(output);
        }

        /// <summary>
        /// Windows 서버용 명령을 실제 PowerShell 5.1 로 돌린다. podman 자리에 표준 입력을 Git sh 로 넘기는 함수를 넣어
        /// 작은따옴표 인용·표준 입력 전달·끝의 CRLF 처리·한글이 맞는지 본다.
        /// </summary>
        [Fact]
        public void Exec_Windows_RunsInRealPowerShell()
        {
            if (!File.Exists(GitSh)) return;
            var ps = Decode(RemoteScript.Exec("abc", Sample + "; echo 한글", "/", RemoteOs.Windows));
            ps = "function podman{$input|& '" + GitSh + "'}\n" + ps;
            var output = Run("powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)));
            var (text, code, cwd) = RemoteScript.ParseExec(output);
            Assert.Equal("it's ok\n한글\n", text);
            Assert.Equal(0, code); // 마지막 명령이 echo
            Assert.Equal("/tmp", cwd);
        }

        static void AssertSample(string output)
        {
            var (text, code, cwd) = RemoteScript.ParseExec(output);
            Assert.Equal("it's ok\n", text); // cat 은 표준 입력이 없어 바로 끝난다
            Assert.Equal(1, code);
            Assert.Equal("/tmp", cwd);
        }

        static string Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            psi.Environment["PATH"] = Path.GetDirectoryName(GitSh) + ";" + psi.Environment["PATH"];
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        static string Decode(string command)
        {
            var b64 = command.Substring(command.LastIndexOf(' ') + 1);
            return Encoding.Unicode.GetString(Convert.FromBase64String(b64));
        }

        [Fact]
        public void Terminal_Arguments()
        {
            var t = new SshTarget { HostName = "10.0.0.5", Port = 2222, User = "ops" };
            var args = SshTerminal.BuildArguments(t, RemoteScript.InteractiveShell("abc"));
            Assert.Equal(new[] { "-t", "-p", "2222", "ops@10.0.0.5" }, args.Take(4));
            Assert.Equal("podman exec -it abc sh -c \"command -v bash >/dev/null 2>&1 && exec bash || exec sh\"", args.Last());
        }
    }
}
