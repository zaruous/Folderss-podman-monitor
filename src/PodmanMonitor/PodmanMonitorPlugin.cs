using PodmanMonitor.Ui;
using Folderss.Plugins;
using System.Windows;

namespace PodmanMonitor
{
    /// <summary>
    /// 플러그인 진입점. plugin.json의 "type"(네임스페이스.클래스)과 이름이 같아야 한다.
    /// </summary>
    public sealed class PodmanMonitorPlugin : IFolderssPlugin
    {
        private IPluginManager _manager;

        /// <summary>처음 실행할 때 한 번 호출된다.</summary>
        public void Initialize(IPluginManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인에서 실행할 때마다 호출된다. 창마다 새 화면(과 새 SSH 연결)을 만든다.
        /// </summary>
        public FrameworkElement CreateView() => new MonitorView(_manager);
    }
}
