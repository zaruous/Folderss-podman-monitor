using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PodmanMonitor.Ui
{
    /// <summary>컨테이너 로그를 보여 주는 비모달 창. 색은 테마를 따른다.</summary>
    internal static class LogWindow
    {
        public static void Show(Window owner, string name, string text, string title = null)
        {
            var box = new TextBox
            {
                Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                VerticalContentAlignment = VerticalAlignment.Top, // Folderss 테마의 TextBox 기본값은 Center
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
            };
            box.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            box.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            AutomationProperties.SetAutomationId(box, "LogText");

            var window = new Window
            {
                Title = title ?? $"로그 - {name}",
                Width = 1000,
                Height = 600,
                Content = box,
                Owner = owner,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            window.Loaded += (s, e) => { box.CaretIndex = box.Text.Length; box.ScrollToEnd(); };
            window.Show();
        }
    }
}
