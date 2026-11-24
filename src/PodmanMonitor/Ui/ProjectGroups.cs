using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PodmanMonitor.Monitoring;

namespace PodmanMonitor.Ui
{
    /// <summary>표에서 compose 프로젝트·pod 묶음 한 개의 머리줄(이름·실행 수·CPU·메모리 합계)과 접힘 상태.</summary>
    public sealed class ProjectGroup : INotifyPropertyChanged
    {
        public const string NoProject = "(compose·pod 아님)";
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _expanded = true;

        public ProjectGroup(string key) { Key = key; }
        public string Key { get; }
        public string Title => string.IsNullOrEmpty(Key) ? NoProject : Key;
        public string Summary { get; private set; } = "";

        public bool IsExpanded
        {
            get => _expanded;
            set { if (_expanded == value) return; _expanded = value; Changed(nameof(IsExpanded)); Changed(nameof(Arrow)); }
        }

        public string Arrow => _expanded ? "▾" : "▸";

        public void Update(IReadOnlyCollection<ContainerInfo> members)
        {
            Summary = Summarize(members);
            Changed(nameof(Summary));
        }

        /// <summary>"7/7 실행 · CPU 1.2% · 메모리 1.5GiB" — 통계가 없는(멈춘) 컨테이너는 합계에서 빠진다.</summary>
        public static string Summarize(IReadOnlyCollection<ContainerInfo> members)
        {
            var running = members.Count(c => c.State == "running");
            var parts = new List<string> { $"{running}/{members.Count} 실행" };
            var withStats = members.Where(c => c.HasStats).ToList();
            if (withStats.Any(c => c.CpuPercent.HasValue)) parts.Add($"CPU {withStats.Sum(c => c.CpuPercent ?? 0):0.0}%");
            if (withStats.Count > 0) parts.Add($"메모리 {Format.Bytes(withStats.Sum(c => c.MemUsed))}");
            var unhealthy = members.Count(c => c.Status?.Contains("unhealthy") == true);
            if (unhealthy > 0) parts.Add($"unhealthy {unhealthy}");
            return string.Join(" · ", parts);
        }

        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// DataGrid 묶음 표시. 그룹 이름(프로젝트) → <see cref="ProjectGroup"/> 을 찾아 머리줄에 붙인다.
    /// 머리줄을 누르면 접고 편다. 접힘 상태는 갱신·재정렬 뒤에도 유지된다(그룹 객체를 이름으로 기억).
    /// </summary>
    internal sealed class ProjectGroups : IValueConverter
    {
        private readonly Dictionary<string, ProjectGroup> _groups = new Dictionary<string, ProjectGroup>();

        public ProjectGroup Get(string key)
        {
            key ??= "";
            if (!_groups.TryGetValue(key, out var g)) _groups[key] = g = new ProjectGroup(key);
            return g;
        }

        public IEnumerable<ProjectGroup> All => _groups.Values;

        public void Clear() => _groups.Clear();

        /// <summary>이번 표본으로 머리줄 합계를 고친다.</summary>
        public void Update(IEnumerable<ContainerInfo> containers)
        {
            foreach (var g in containers.GroupBy(c => c.Group))
                Get(g.Key).Update(g.ToList());
        }

        object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture) => Get(value as string);
        object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

        public GroupStyle CreateStyle()
        {
            // GroupItem 템플릿: [머리줄] + [줄들(접히면 숨김)]
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetBinding(FrameworkElement.DataContextProperty, new Binding("Name") { Converter = this });

            var header = new FrameworkElementFactory(typeof(Border));
            header.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
            header.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            header.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            header.SetResourceReference(Border.BackgroundProperty, "SurfaceBackground");
            header.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            header.SetValue(AutomationIdProperty, "ProjectGroupHeader");
            header.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((s, e) =>
            {
                if (((FrameworkElement)s).DataContext is ProjectGroup g) { g.IsExpanded = !g.IsExpanded; e.Handled = true; }
            }));

            var line = new FrameworkElementFactory(typeof(StackPanel));
            line.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            line.AppendChild(Text(nameof(ProjectGroup.Arrow), FontWeights.Normal, "PrimaryText", new Thickness(0, 0, 6, 0)));
            line.AppendChild(Text(nameof(ProjectGroup.Title), FontWeights.SemiBold, "PrimaryText", new Thickness(0, 0, 12, 0)));
            line.AppendChild(Text(nameof(ProjectGroup.Summary), FontWeights.Normal, "SecondaryText", new Thickness(0)));
            header.AppendChild(line);
            root.AppendChild(header);

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            items.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ProjectGroup.IsExpanded)) { Converter = new BooleanToVisibilityConverter() });
            root.AppendChild(items);

            var style = new Style(typeof(GroupItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GroupItem)) { VisualTree = root }));
            return new GroupStyle { ContainerStyle = style };
        }

        private static readonly DependencyProperty AutomationIdProperty = System.Windows.Automation.AutomationProperties.AutomationIdProperty;

        private static FrameworkElementFactory Text(string path, FontWeight weight, string brushKey, Thickness margin)
        {
            var t = new FrameworkElementFactory(typeof(TextBlock));
            t.SetBinding(TextBlock.TextProperty, new Binding(path));
            t.SetValue(TextBlock.FontWeightProperty, weight);
            t.SetValue(FrameworkElement.MarginProperty, margin);
            t.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }
    }
}
