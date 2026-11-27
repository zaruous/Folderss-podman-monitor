using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using Folderss.Plugins;

namespace PodmanMonitor.Standalone
{
    /// <summary>
    /// Folderss 없이 Podman 모니터 창만 띄운다. 플러그인 DLL 을 직접 참조하므로 zip 설치·Folderss 재시작이 필요 없다.
    /// 사용: PodmanMonitorStandalone.exe [--theme Black|Light|Catppuccin|Dracula|GitHub|Nord|Solarized]
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var theme = Arg(args, "--theme") ?? FolderssTheme() ?? "Black";
            app.Resources.MergedDictionaries.Add(LoadTheme(theme) ?? LoadTheme("Black"));
            app.Resources.MergedDictionaries.Add(LoadTheme("Controls"));

            var manager = new StandaloneManager(theme);
            var plugin = new PodmanMonitorPlugin();
            plugin.Initialize(manager);
            var window = new Window
            {
                Title = "Podman 모니터 (단독 실행)",
                Width = 1280,
                Height = 820,
                MinWidth = 320,
                MinHeight = 240,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = plugin.CreateView(),
            };
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            return app.Run(window);
        }

        static string Arg(string[] args, string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Folderss 에서 고른 테마(사본이 있는 것만).</summary>
        static string FolderssTheme()
        {
            try
            {
                var path = Path.Combine(StandaloneManager.FolderssData, "theme.txt");
                var name = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                return name != null && Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("Themes." + name + ".xaml") ? name : null;
            }
            catch (IOException) { return null; }
        }

        static ResourceDictionary LoadTheme(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Themes." + name + ".xaml");
            return stream == null ? null : (ResourceDictionary)XamlReader.Load(stream);
        }
    }

    /// <summary>
    /// 설정은 %LOCALAPPDATA%\Folderss\plugin-data\zaruous.podman-monitor.standalone\settings.json 에 둔다.
    /// 처음 실행할 때 Folderss 쪽 설정(등록한 호스트 등)을 복사해 온다. 같은 파일을 쓰지 않는 것은 두 프로그램이 서로 덮어쓰지 않게 하려는 것.
    /// </summary>
    internal sealed class StandaloneManager : IPluginManager
    {
        const string Id = "zaruous.podman-monitor";
        public static readonly string FolderssData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folderss");
        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        };

        private readonly Dictionary<string, string> _settings;
        private readonly string _file;
        private readonly string _theme;

        public StandaloneManager(string theme)
        {
            _theme = theme;
            DataDirectory = Path.Combine(FolderssData, "plugin-data", Id + ".standalone");
            Directory.CreateDirectory(DataDirectory);
            _file = Path.Combine(DataDirectory, "settings.json");
            var source = File.Exists(_file) ? _file : Path.Combine(FolderssData, "plugin-data", Id, "settings.json");
            _settings = Read(source);
            if (!File.Exists(_file)) Save();
        }

        static Dictionary<string, string> Read(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { }
            return new Dictionary<string, string>();
        }

        void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_settings, Json));

        public string PluginId => Id;
        public string PluginDirectory => AppContext.BaseDirectory;
        public string DataDirectory { get; }
        public string GetSetting(string key) => _settings.TryGetValue(key, out var v) ? v : null;

        public void SetSetting(string key, string value)
        {
            if (value == null) _settings.Remove(key); else _settings[key] = value;
            Save();
        }

        public IReadOnlyDictionary<string, string> GetAllSettings() => new Dictionary<string, string>(_settings);
        public IReadOnlyDictionary<string, string> GetAppSettings() => new Dictionary<string, string> { ["theme"] = _theme };
        public IFolderPanel CreateFolderPanel(string path) => throw new NotSupportedException("단독 실행에서는 폴더 패널을 쓸 수 없습니다");
        public void AddSettingsPage(IPluginSettingsPage page) { }
    }
}
