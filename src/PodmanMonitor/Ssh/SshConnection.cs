using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PodmanMonitor.Ssh
{
    public sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    /// <summary>서버 셸 종류. 접속할 때 한 번 알아낸다.</summary>
    public enum RemoteOs
    {
        /// <summary>Linux 등 sh 호환 셸.</summary>
        Posix,
        /// <summary>Windows OpenSSH(로그인 셸이 cmd 또는 PowerShell) — 스크립트는 PowerShell 로 보낸다.</summary>
        Windows,
    }

    /// <summary>
    /// SSH 연결 하나를 유지하며 명령마다 exec 채널만 새로 연다. 끊기면 다음 명령 때 다시 연결한다.
    /// 서버 키는 ~/.ssh/known_hosts 와 일치해야 한다(먼저 ssh 로 한 번 접속해 두면 된다).
    /// </summary>
    public sealed class SshConnection : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private SshClient _client;
        private bool _disposed;

        private readonly HostEntry _entry;

        public SshConnection(HostEntry entry)
        {
            _entry = entry.Clone();
        }

        /// <summary>표시용 이름(호스트 설정의 이름).</summary>
        public string Alias => _entry.Name;
        public SshTarget Target { get; private set; }
        /// <summary>지금까지 맺은 연결 수(재사용 확인용).</summary>
        public int ConnectCount { get; private set; }
        public bool IsConnected => _client?.IsConnected == true;
        /// <summary>접속한 서버의 셸 종류(연결한 뒤에 알 수 있다).</summary>
        public RemoteOs Os { get; private set; }

        public Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default) =>
            RunAsync(_ => command, timeout, ct);

        /// <summary>서버 셸 종류에 맞는 명령을 만들어 실행한다(연결·재연결 뒤에 고르므로 항상 지금 서버에 맞다).</summary>
        public async Task<CommandResult> RunAsync(Func<RemoteOs, string> build, TimeSpan timeout, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        return RunCore(build, timeout);
                    }
                    catch (Exception ex) when (!_disposed && (ex is SshConnectionException || ex is System.Net.Sockets.SocketException || ex is ObjectDisposedException))
                    {
                        // 끊긴 연결 — 한 번만 다시 연결해서 시도한다
                        DropClient();
                        return RunCore(build, timeout);
                    }
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private CommandResult RunCore(Func<RemoteOs, string> build, TimeSpan timeout)
        {
            EnsureConnected();
            using var cmd = _client.CreateCommand(build(Os));
            cmd.CommandTimeout = timeout;
            var output = cmd.Execute();
            return new CommandResult { ExitCode = cmd.ExitStatus ?? -1, Output = output, Error = cmd.Error };
        }

        private void EnsureConnected()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SshConnection));
            if (_client?.IsConnected == true) return;
            DropClient();

            Target = _entry.Resolve();
            var keys = Target.IdentityFiles.Where(File.Exists).Select(p => new PrivateKeyFile(p)).ToArray();
            if (keys.Length == 0)
                throw new InvalidOperationException("개인키를 찾지 못했습니다: " + string.Join(", ", Target.IdentityFiles));

            var info = new ConnectionInfo(Target.HostName, Target.Port, Target.User,
                new PrivateKeyAuthenticationMethod(Target.User, keys))
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            var knownHosts = File.Exists(KnownHosts.DefaultPath) ? File.ReadAllText(KnownHosts.DefaultPath) : "";
            bool hostKeyRejected = false;
            client.HostKeyReceived += (s, e) =>
            {
                e.CanTrust = KnownHosts.Contains(knownHosts, Target.HostName, Target.Port, e.HostKey);
                hostKeyRejected = !e.CanTrust;
            };
            try
            {
                client.Connect();
            }
            catch (SshConnectionException ex) when (hostKeyRejected)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    $"서버 키가 known_hosts 에 없습니다({Target.HostName}). 먼저 터미널에서 'ssh {(Target.Port == 22 ? "" : "-p " + Target.Port + " ")}{Target.User}@{Target.HostName}' 로 한 번 접속해 키를 등록하세요.", ex);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            Os = DetectOs(client);
            _client = client;
            ConnectCount++;
        }

        /// <summary>uname 이 되면 sh 계열, 안 되면(cmd·PowerShell 에는 uname 이 없다) Windows.</summary>
        private static RemoteOs DetectOs(SshClient client)
        {
            using var cmd = client.CreateCommand("uname -s");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = cmd.Execute();
            return cmd.ExitStatus == 0 && !string.IsNullOrWhiteSpace(output) ? RemoteOs.Posix : RemoteOs.Windows;
        }

        private void DropClient()
        {
            var c = _client;
            _client = null;
            if (c == null) return;
            try { if (c.IsConnected) c.Disconnect(); } catch { }
            try { c.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DropClient();
        }
    }
}
