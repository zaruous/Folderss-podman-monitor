using System.Collections.Generic;
using System.IO;
using PodmanMonitor.Ssh;
using Xunit;

namespace PodmanMonitor.Tests
{
    public class HostStoreTests
    {
        private static readonly string SshDir = Path.Combine(Path.GetTempPath(), "home", ".ssh");

        [Fact]
        public void SaveAndLoad_RoundTrip_ClearsLegacyKey()
        {
            var settings = new Dictionary<string, string> { ["host"] = "old" };
            HostStore.Save((k, v) => settings[k] = v, new[]
            {
                new HostEntry { Name = "운영", Host = "kyj" },
                new HostEntry { Name = "개발", Host = "10.0.0.5", Port = 2222, User = "dev" },
            });
            Assert.Null(settings["host"]);

            var loaded = HostStore.Load(k => settings.TryGetValue(k, out var v) ? v : null);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("운영", loaded[0].Name);
            Assert.Equal(2222, loaded[1].Port);
            Assert.Equal("dev", loaded[1].User);
        }

        [Fact]
        public void Load_MigratesLegacySingleHost()
        {
            var loaded = HostStore.Parse(null, "kyj");
            var h = Assert.Single(loaded);
            Assert.Equal("kyj", h.Name);
            Assert.Equal("kyj", h.Host);
        }

        [Fact]
        public void Load_BrokenJson_IsEmpty() => Assert.Empty(HostStore.Parse("{not json", null));

        [Fact]
        public void AliasesFromConfig_SkipsWildcards()
        {
            var aliases = HostStore.AliasesFromConfig("Host kyj web\n  HostName x\nHost *.corp\nHost * !bad\nHost db\n");
            Assert.Equal(new[] { "kyj", "web", "db" }, aliases);
        }

        [Theory]
        [InlineData("운영", "kyj", null, null, null)]
        [InlineData("", "kyj", null, null, "이름")]
        [InlineData("a", "", null, null, "호스트")]
        [InlineData("a", "kyj; rm -rf /", null, null, "호스트에는")]
        [InlineData("a", "kyj", 70000, null, "포트")]
        [InlineData("a", "kyj", null, "root$(id)", "사용자에는")]
        public void Validate(string name, string host, int? port, string user, string expectedContains)
        {
            var error = new HostEntry { Name = name, Host = host, Port = port, User = user }.Validate();
            if (expectedContains == null) Assert.Null(error);
            else Assert.Contains(expectedContains, error);
        }

        [Fact]
        public void Validate_MissingKeyFile() =>
            Assert.Contains("개인키", new HostEntry { Name = "a", Host = "kyj", IdentityFile = @"Z:\없는\키" }.Validate());

        [Fact]
        public void Resolve_EntryValuesOverrideConfig()
        {
            var config = "Host kyj\n  HostName 192.168.45.7\n  User kyj\n  Port 22\n  IdentityFile ~/.ssh/id_ed25519\n";
            var plain = new HostEntry { Name = "운영", Host = "kyj" }.Resolve(config, SshDir);
            Assert.Equal("192.168.45.7", plain.HostName);
            Assert.Equal("kyj", plain.User);
            Assert.Equal("운영", plain.Alias);

            var key = Path.Combine(SshDir, "other_key");
            var custom = new HostEntry { Name = "x", Host = "kyj", Port = 2222, User = "admin", IdentityFile = key }.Resolve(config, SshDir);
            Assert.Equal("192.168.45.7", custom.HostName);
            Assert.Equal(2222, custom.Port);
            Assert.Equal("admin", custom.User);
            Assert.Equal(key, Assert.Single(custom.IdentityFiles));
        }
    }
}
