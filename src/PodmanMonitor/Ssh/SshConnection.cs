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

        public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        return RunCore(command, timeout);
                    }
                    catch (Exception ex) when (!_disposed && (ex is SshConnectionException || ex is System.Net.Sockets.SocketException || ex is ObjectDisposedException))
                    {
                        // 끊긴 연결 — 한 번만 다시 연결해서 시도한다
                        DropClient();
                        return RunCore(command, timeout);
                    }
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private CommandResult RunCore(string command, TimeSpan timeout)
        {
            EnsureConnected();
            using var cmd = _client.CreateCommand(command);
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
            _client = client;
            ConnectCount++;
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
