using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PodmanMonitor.Ssh
{
    /// <summary>
    /// Windows 의 ssh.exe(OpenSSH)로 새 콘솔 창을 열어 원격 명령을 대화형(-t)으로 실행한다.
    /// 접속 정보는 플러그인과 같은 값(해석이 끝난 SshTarget)을 넘기고, 서버 키는 같은 ~/.ssh/known_hosts 를 쓴다.
    /// </summary>
    public static class SshTerminal
    {
        public static string FindSsh()
        {
            var system = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
            if (File.Exists(system)) return system;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "ssh.exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static List<string> BuildArguments(SshTarget target, string remoteCommand)
        {
            var args = new List<string> { "-t" };
            if (target.Port != 22) { args.Add("-p"); args.Add(target.Port.ToString()); }
            var key = target.IdentityFiles.FirstOrDefault(File.Exists);
            if (key != null) { args.Add("-i"); args.Add(key); }
            args.Add(string.IsNullOrEmpty(target.User) ? target.HostName : target.User + "@" + target.HostName);
            args.Add(remoteCommand);
            return args;
        }

        /// <summary>새 콘솔 창으로 연다(GUI 프로세스의 자식 콘솔 앱은 자기 창을 받는다).</summary>
        public static void Open(SshTarget target, string remoteCommand)
        {
            var ssh = FindSsh() ?? throw new InvalidOperationException(
                "ssh.exe 를 찾지 못했습니다. Windows 설정 > 시스템 > 선택적 기능에서 'OpenSSH 클라이언트'를 설치하세요.");
            var psi = new ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = false };
            foreach (var a in BuildArguments(target, remoteCommand)) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
    }
}
