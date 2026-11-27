using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;

namespace PodmanMonitor.Ui
{
    /// <summary>
    /// 컨테이너 안에서 명령을 실행하고 결과를 이어 붙여 보여 주는 비모달 창(podman exec, tty 없음).
    /// 모니터 창의 SSH 연결을 그대로 쓴다. cd 는 다음 명령에도 이어진다. top·vi 같은 대화형 명령은 [터미널 열기]를 쓴다.
    /// </summary>
    internal sealed class ExecWindow : Window
    {
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        const int MaxOutput = 400_000; // 너무 길어지면 앞부분을 버린다

        private readonly SshConnection _ssh;
        private readonly string _id;
        private readonly string _name;
        private readonly Action _openTerminal;
        private readonly TextBox _output = new TextBox();
        private readonly TextBox _input = new TextBox();
        private readonly TextBlock _cwdText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _run = new Button { Content = "실행", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        private readonly List<string> _history = new List<string>();
        private int _historyAt;
        private string _cwd;
        private bool _busy;

        public ExecWindow(Window owner, SshConnection ssh, string id, string name, Action openTerminal)
        {
            _ssh = ssh; _id = id; _name = name; _openTerminal = openTerminal;
            Title = $"명령 실행 - {name} ({Short(id)})";
            Width = 1000; Height = 600;
            Owner = owner;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");

            var mono = new FontFamily("Consolas");
            _output.IsReadOnly = true;
            _output.FontFamily = mono; _output.FontSize = 12;
            _output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _output.TextWrapping = TextWrapping.NoWrap;
            _output.VerticalContentAlignment = VerticalAlignment.Top; // Folderss 테마의 TextBox 기본값은 Center
            _output.BorderThickness = new Thickness(0);
            _output.Padding = new Thickness(6);
            _output.SetResourceReference(BackgroundProperty, "PanelBackground");
            _output.SetResourceReference(ForegroundProperty, "PrimaryText");
            AutomationProperties.SetAutomationId(_output, "ExecOutput");

            _input.FontFamily = mono; _input.FontSize = 12;
            _input.Padding = new Thickness(4, 2, 4, 2);
            _input.SetResourceReference(BackgroundProperty, "PanelBackground");
            _input.SetResourceReference(ForegroundProperty, "PrimaryText");
            _input.SetResourceReference(BorderBrushProperty, "BorderBrush");
            AutomationProperties.SetAutomationId(_input, "ExecInput");
            _input.PreviewKeyDown += OnInputKey;
            AutomationProperties.SetAutomationId(_run, "ExecRun");
            _run.Click += async (s, e) => await RunAsync();

            var clear = new Button { Content = "지우기", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            clear.Click += (s, e) => _output.Clear();
            var terminal = new Button { Content = "터미널 열기", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "ssh 로 새 콘솔 창을 열어 컨테이너 셸에 붙습니다(top·vi 같은 대화형 명령용)" };
            terminal.Click += (s, e) => _openTerminal?.Invoke();

            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = true };
            DockPanel.SetDock(_cwdText, Dock.Left);
            bar.Children.Add(_cwdText);
            foreach (var b in new UIElement[] { terminal, clear, _run })
            {
                DockPanel.SetDock(b, Dock.Right);
                bar.Children.Add(b);
            }
            bar.Children.Add(_input);

            var hint = new TextBlock
            {
                Text = "Enter 실행 · ↑↓ 이전 명령 · 명령마다 새 셸(환경 변수는 이어지지 않음, cd 는 이어짐) · 최대 30초 · 표준 입력 없음",
                Margin = new Thickness(6, 0, 6, 4), FontSize = 11, Opacity = 0.7,
            };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom);
            DockPanel.SetDock(hint, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(hint);
            root.Children.Add(_output);
            Content = root;

            UpdatePrompt();
            Loaded += (s, e) => _input.Focus();
        }

        static string Short(string id) => id != null && id.Length > 12 ? id.Substring(0, 12) : id;

        private void UpdatePrompt() => _cwdText.Text = (_cwd ?? "~") + " $";

        private async void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await RunAsync(); }
            else if (e.Key == Key.Up && _history.Count > 0)
            {
                e.Handled = true;
                _historyAt = Math.Max(0, _historyAt - 1);
                SetInput(_history[_historyAt]);
            }
            else if (e.Key == Key.Down && _history.Count > 0)
            {
                e.Handled = true;
                _historyAt = Math.Min(_history.Count, _historyAt + 1);
                SetInput(_historyAt < _history.Count ? _history[_historyAt] : "");
            }
        }

        private void SetInput(string text)
        {
            _input.Text = text;
            _input.CaretIndex = text.Length;
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            var command = _input.Text.Trim();
            if (_busy || command.Length == 0) return;
            if (_history.Count == 0 || _history[_history.Count - 1] != command) _history.Add(command);
            _historyAt = _history.Count;
            _input.Clear();
            Append($"{_cwd ?? "~"} $ {command}\n");

            _busy = true;
            _run.IsEnabled = false;
            Title = $"명령 실행 - {_name} ({Short(_id)}) · 실행 중...";
            try
            {
                var r = await _ssh.RunAsync(os => RemoteScript.Exec(_id, command, _cwd, os), Timeout);
                var (text, code, cwd) = RemoteScript.ParseExec(r.Output);
                if (!string.IsNullOrEmpty(r.Error)) text += r.Error;
                if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
                Append(text);
                if (cwd != null) _cwd = cwd;
                var exit = code ?? r.ExitCode;
                if (exit != 0) Append($"[종료 코드 {exit}]\n");
            }
            catch (ObjectDisposedException)
            {
                Append("[연결이 닫혔습니다 — 모니터 창에서 다시 연결한 뒤 이 창을 새로 여세요]\n");
            }
            catch (Exception ex)
            {
                var msg = ex is Renci.SshNet.Common.SshOperationTimeoutException ? $"{Timeout.TotalSeconds:0}초 안에 끝나지 않았습니다" : ex.Message;
                Append($"[실패: {msg}]\n");
            }
            finally
            {
                _busy = false;
                _run.IsEnabled = true;
                Title = $"명령 실행 - {_name} ({Short(_id)})";
                UpdatePrompt();
                _input.Focus();
            }
        }

        private void Append(string text)
        {
            _output.AppendText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            if (_output.Text.Length > MaxOutput) _output.Text = _output.Text.Substring(_output.Text.Length - MaxOutput);
            _output.CaretIndex = _output.Text.Length;
            _output.ScrollToEnd();
        }
    }
}
