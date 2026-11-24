using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PodmanMonitor.Ssh;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class SshConfigTests
    {
        private static readonly string SshDir = Path.Combine(Path.GetTempPath(), "home", ".ssh");

        [Fact]
        public void Resolve_UsesHostBlockValues()
        {
            var config = "Host kyj\n    HostName 192.168.45.7\n    User kyj\n    IdentityFile ~/.ssh/id_ed25519\n    Port 2222\n";
            var t = SshConfigFile.Resolve("kyj", config, SshDir);
            Assert.Equal("192.168.45.7", t.HostName);
            Assert.Equal("kyj", t.User);
            Assert.Equal(2222, t.Port);
            Assert.Equal(Path.Combine(SshDir, "id_ed25519"), Assert.Single(t.IdentityFiles));
        }

        [Fact]
        public void Resolve_FirstValueWins_AndWildcardApplies()
        {
            var config = "Host web\n  User alice\nHost *\n  User nobody\n  HostName ignored\n  IdentityFile ~/.ssh/shared\n";
            var t = SshConfigFile.Resolve("web", config, SshDir);
            Assert.Equal("alice", t.User);            // 먼저 나온 값이 이긴다
            Assert.Equal("ignored", t.HostName);      // Host * 에서 채워짐
            Assert.Single(t.IdentityFiles);
        }

        [Fact]
        public void Resolve_UnknownAlias_FallsBackToDefaults()
        {
            var t = SshConfigFile.Resolve("10.0.0.5", "Host other\n  User x\n", SshDir);
            Assert.Equal("10.0.0.5", t.HostName);
            Assert.Equal(Environment.UserName, t.User);
            Assert.Equal(22, t.Port);
            Assert.Contains(Path.Combine(SshDir, "id_ed25519"), t.IdentityFiles);
        }

        [Theory]
        [InlineData("kyj", "kyj", true)]
        [InlineData("kyj", "k*", true)]
        [InlineData("kyj", "* !kyj", false)]
        [InlineData("kyj", "web db", false)]
        [InlineData("KYJ", "kyj", true)]
        public void MatchesHost(string alias, string patterns, bool expected) =>
            Assert.Equal(expected, SshConfigFile.MatchesHost(alias, patterns));

        [Fact]
        public void KnownHosts_PlainAndPortEntries()
        {
            var key = new byte[] { 0, 0, 0, 11, 1, 2, 3, 4 };
            var other = new byte[] { 9, 9, 9 };
            var text = $"192.168.45.7 ssh-ed25519 {Convert.ToBase64String(key)}\n" +
                       $"[127.0.0.1]:8022 ssh-ed25519 {Convert.ToBase64String(other)}\n";
            Assert.True(KnownHosts.Contains(text, "192.168.45.7", 22, key));
            Assert.False(KnownHosts.Contains(text, "192.168.45.7", 22, other)); // 키가 다르면 거부
            Assert.False(KnownHosts.Contains(text, "192.168.45.7", 2222, key)); // 포트가 다르면 다른 호스트
            Assert.True(KnownHosts.Contains(text, "127.0.0.1", 8022, other));
        }

        [Fact]
        public void KnownHosts_HashedEntry()
        {
            var key = new byte[] { 1, 2, 3 };
            var salt = new byte[20];
            new Random(1).NextBytes(salt);
            using var hmac = new HMACSHA1(salt);
            var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes("myhost"));
            var text = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 {Convert.ToBase64String(key)}";
            Assert.True(KnownHosts.Contains(text, "myhost", 22, key));
            Assert.False(KnownHosts.Contains(text, "otherhost", 22, key));
        }
    }
}
