using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PodmanMonitor.Monitoring;
using PodmanMonitor.Ssh;
using Microsoft.Win32;

namespace PodmanMonitor.Ui
{
    /// <summary>
    /// SSH 호스트 목록 설정 팝업(모달). 왼쪽 목록에서 고르고 오른쪽에서 고친다.
    /// [저장]을 누르면 <see cref="Result"/> 에 새 목록이 담기고 DialogResult = true.
    /// </summary>
    internal sealed class HostSettingsWindow : Window
    {
        private readonly ObservableCollection<HostEntry> _hosts;
        private readonly ListBox _list = new ListBox { DisplayMemberPath = nameof(HostEntry.Name) };
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _host = new TextBox();
        private readonly TextBox _port = new TextBox();
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _key = new TextBox();
        private readonly TextBlock _resolved = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Grid _form = new Grid();
        private bool _loading;

        public List<HostEntry> Result { get; private set; }

        public HostSettingsWindow(IEnumerable<HostEntry> hosts, string select)
        {
            Title = "SSH 호스트 설정";
            Width = 760;
            Height = 460;
            MinWidth = 600;
            MinHeight = 380;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "PrimaryText");
            SetResourceReference(FontFamilyProperty, "AppFontFamily");
            AutomationProperties.SetAutomationId(this, "HostSettingsWindow");

            _hosts = new ObservableCollection<HostEntry>(hosts.Select(h => h.Clone()));
            Content = Build();
            _list.ItemsSource = _hosts;
            _list.SelectedItem = _hosts.FirstOrDefault(h => h.Name == select) ?? _hosts.FirstOrDefault();
            ShowSelected();
        }

        private UIElement Build()
        {
            var root = new DockPanel { Margin = new Thickness(12) };

            // 아래: 저장·취소
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(Btn("저장", "SaveHostsButton", (s, e) => Save(), isDefault: true));
            bottom.Children.Add(Btn("취소", "CancelHostsButton", (s, e) => { DialogResult = false; }, isCancel: true));
            root.Children.Add(bottom);

            // 왼쪽: 목록 + 추가·삭제·가져오기
            var left = new DockPanel { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(left, Dock.Left);
            var leftButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(leftButtons, Dock.Bottom);
            leftButtons.Children.Add(Btn("추가", "AddHostButton", (s, e) => AddHost()));
            leftButtons.Children.Add(Btn("삭제", "RemoveHostButton", (s, e) => RemoveHost()));
            leftButtons.Children.Add(Btn("~/.ssh/config 가져오기", "ImportHostsButton", (s, e) => ImportFromConfig()));
            left.Children.Add(leftButtons);
            _list.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            _list.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            _list.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            _list.SelectionChanged += (s, e) => ShowSelected();
            AutomationProperties.SetAutomationId(_list, "HostList");
            left.Children.Add(_list);
            root.Children.Add(left);

            // 오른쪽: 편집 양식
            _form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _form.ColumnDefinitions.Add(new ColumnDefinition());
            _form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Row(0, "이름", _name, "HostNameBox", "콤보박스에 보이는 이름");
            Row(1, "호스트", _host, "HostAddressBox", "~/.ssh/config 의 Host 별칭 또는 IP·도메인");
            Row(2, "포트", _port, "HostPortBox", "비우면 별칭 설정 또는 22");
            Row(3, "사용자", _user, "HostUserBox", "비우면 별칭 설정 또는 Windows 사용자 이름");
            Row(4, "개인키", _key, "HostKeyBox", "비우면 별칭의 IdentityFile 또는 ~/.ssh/id_ed25519 등");
            var browse = Btn("찾아보기…", "BrowseKeyButton", (s, e) => BrowseKey());
            Grid.SetRow(browse, 4);
            Grid.SetColumn(browse, 2);
            _form.Children.Add(browse);

            foreach (var box in new[] { _name, _host, _port, _user, _key })
                box.TextChanged += (s, e) => { if (!_loading) ApplyForm(); };

            var right = new StackPanel();
            right.Children.Add(_form);
            _resolved.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            AutomationProperties.SetAutomationId(_resolved, "ResolvedText");
            right.Children.Add(_resolved);
            var test = Btn("연결 시험", "TestHostButton", async (s, e) => await TestAsync());
            test.HorizontalAlignment = HorizontalAlignment.Left;
            test.Margin = new Thickness(0, 10, 0, 0);
            right.Children.Add(test);
            AutomationProperties.SetAutomationId(_message, "HostMessageText");
            right.Children.Add(_message);
            var note = new TextBlock
            {
                Text = "키 인증만 지원합니다(암호 없는 개인키). 서버 키는 ~/.ssh/known_hosts 에 있어야 합니다 — 처음이면 터미널에서 ssh 로 한 번 접속해 두세요.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 12, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            right.Children.Add(note);
            root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return root;
        }

        private void Row(int row, string label, TextBox box, string id, string tip)
        {
            _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            Grid.SetRow(l, row);
            _form.Children.Add(l);
            box.Margin = new Thickness(0, 4, 0, 4);
            box.ToolTip = tip;
            AutomationProperties.SetAutomationId(box, id);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            _form.Children.Add(box);
        }

        private static Button Btn(string text, string id, RoutedEventHandler click, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 4), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += click;
            AutomationProperties.SetAutomationId(b, id);
            return b;
        }

        private HostEntry Selected => _list.SelectedItem as HostEntry;

        private void ShowSelected()
        {
            var h = Selected;
            _loading = true;
            _form.IsEnabled = h != null;
            _name.Text = h?.Name ?? "";
            _host.Text = h?.Host ?? "";
            _port.Text = h?.Port?.ToString() ?? "";
            _user.Text = h?.User ?? "";
            _key.Text = h?.IdentityFile ?? "";
            _loading = false;
            _message.Text = "";
            UpdateResolved();
        }

        /// <summary>양식 값을 선택한 항목에 바로 반영한다(목록 이름도 함께 바뀌게).</summary>
        private void ApplyForm()
        {
            var h = Selected;
            if (h == null) return;
            h.Name = _name.Text.Trim();
            h.Host = _host.Text.Trim();
            h.Port = int.TryParse(_port.Text.Trim(), out var p) ? p : (int?)null;
            h.User = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
            h.IdentityFile = string.IsNullOrWhiteSpace(_key.Text) ? null : _key.Text.Trim();
            _message.Text = ""; // 고쳤으면 이전 오류 문구는 지운다
            _list.Items.Refresh();
            UpdateResolved();
        }

        private void UpdateResolved()
        {
            var h = Selected;
            if (h == null || string.IsNullOrWhiteSpace(h.Host)) { _resolved.Text = ""; return; }
            try
            {
                var t = h.Resolve();
                _resolved.Text = $"접속: {t.User}@{t.HostName}:{t.Port}  ·  키: {string.Join(", ", t.IdentityFiles.Select(Path.GetFileName))}";
            }
            catch (Exception ex) { _resolved.Text = ex.Message; }
        }

        private void AddHost()
        {
            var name = "새 호스트";
            for (int i = 2; _hosts.Any(x => x.Name == name); i++) name = "새 호스트 " + i;
            var h = new HostEntry { Name = name };
            _hosts.Add(h);
            _list.SelectedItem = h;
            _name.Focus();
            _name.SelectAll();
        }

        private void RemoveHost()
        {
            var h = Selected;
            if (h == null) return;
            var i = _hosts.IndexOf(h);
            _hosts.Remove(h);
            _list.SelectedItem = _hosts.Count == 0 ? null : _hosts[Math.Min(i, _hosts.Count - 1)];
        }

        private void ImportFromConfig()
        {
            var path = Path.Combine(SshConfigFile.DefaultSshDirectory, "config");
            if (!File.Exists(path)) { _message.Text = "~/.ssh/config 파일이 없습니다."; return; }
            var added = 0;
            foreach (var alias in HostStore.AliasesFromConfig(File.ReadAllText(path)))
            {
                if (_hosts.Any(h => h.Name == alias || h.Host == alias)) continue;
                _hosts.Add(new HostEntry { Name = alias, Host = alias });
                added++;
            }
            _message.Text = added == 0 ? "새로 가져올 별칭이 없습니다." : $"별칭 {added}개를 가져왔습니다.";
            if (Selected == null) _list.SelectedItem = _hosts.FirstOrDefault();
        }

        private void BrowseKey()
        {
            var dlg = new OpenFileDialog
            {
                Title = "개인키 파일",
                InitialDirectory = Directory.Exists(SshConfigFile.DefaultSshDirectory) ? SshConfigFile.DefaultSshDirectory : null,
                Filter = "모든 파일|*.*",
            };
            if (dlg.ShowDialog(this) == true) _key.Text = dlg.FileName;
        }

        private async System.Threading.Tasks.Task TestAsync()
        {
            var h = Selected;
            if (h == null) return;
            var error = h.Validate();
            if (error != null) { _message.Text = error; return; }
            _message.Text = "연결 시험 중...";
            try
            {
                using var ssh = new SshConnection(h);
                var r = await ssh.RunAsync(RemoteScript.Probe, TimeSpan.FromSeconds(20));
                _message.Text = r.Output.Contains("@@OK@@")
                    ? $"연결 성공 — {r.Output.Replace("@@OK@@", "").Trim()}"
                    : "연결은 됐지만 Docker 를 쓸 수 없습니다: " + (r.Output + " " + r.Error).Trim();
            }
            catch (Exception ex)
            {
                _message.Text = "연결 실패: " + ex.Message;
            }
        }

        private void Save()
        {
            foreach (var h in _hosts)
            {
                var error = h.Validate();
                if (error != null)
                {
                    _list.SelectedItem = h;
                    _message.Text = $"[{h.Name}] {error}";
                    return;
                }
            }
            var dup = _hosts.GroupBy(h => h.Name).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) { _message.Text = $"이름이 겹칩니다: {dup.Key}"; return; }
            Result = _hosts.Select(h => h.Clone()).ToList();
            DialogResult = true;
        }
    }
}
