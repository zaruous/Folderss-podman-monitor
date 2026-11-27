using System.ComponentModel;
using System.Runtime.CompilerServices;
using PodmanMonitor.Monitoring;

namespace PodmanMonitor.Ui
{
    /// <summary>표 한 줄. 갱신 때 객체를 바꾸지 않고 값만 고쳐 선택·스크롤이 유지되게 한다.</summary>
    public sealed class ContainerRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Id { get; private set; }
        /// <summary>podman ps 와 같은 앞 12자리.</summary>
        public string ShortId => Id != null && Id.Length > 12 ? Id.Substring(0, 12) : Id;
        public string Name { get; private set; }
        public string Image { get; private set; }
        public string State { get; private set; }
        public string Status { get; private set; }
        public string Ports { get; private set; }
        /// <summary>표 묶음 이름(compose 프로젝트, 없으면 "pod이름 (pod)").</summary>
        public string Project { get; private set; }
        /// <summary>compose 프로젝트(의존 순서 계산용). compose 가 아니면 빈 문자열.</summary>
        public string ComposeProject { get; private set; }
        public string Pod { get; private set; }
        public string Service { get; private set; }
        public System.Collections.Generic.List<ComposeDependency> DependsOn { get; private set; }
        public string Cpu { get; private set; }
        public double CpuValue { get; private set; }
        public string Memory { get; private set; }
        public string MemPercent { get; private set; }
        public double MemValue { get; private set; }
        public string Net { get; private set; }
        public string Block { get; private set; }
        public bool Unhealthy { get; private set; }
        public bool CpuHot => CpuValue >= 80;
        public bool MemHot => MemValue >= 80;

        public ComposeTarget ToTarget() => new ComposeTarget { Id = Id, Project = ComposeProject, Service = Service, DependsOn = DependsOn };

        public void Update(ContainerInfo c)
        {
            Id = c.Id;
            Name = c.Name;
            Image = c.Image;
            State = c.State;
            Status = c.Status;
            Ports = c.Ports;
            Project = c.Group;
            ComposeProject = c.Project ?? "";
            Pod = c.Pod ?? "";
            Service = c.Service ?? "";
            DependsOn = c.DependsOn ?? new System.Collections.Generic.List<ComposeDependency>();
            Unhealthy = c.Status?.Contains("unhealthy") == true;
            if (c.HasStats)
            {
                CpuValue = c.CpuPercent ?? 0;
                Cpu = c.CpuPercent.HasValue ? $"{c.CpuPercent:0.0}% ({c.CpuCores:0.00}코어)" : "측정 중";
                MemValue = c.MemPercent;
                Memory = $"{Format.Bytes(c.MemUsed)} / {Format.Bytes(c.MemLimit)}";
                MemPercent = $"{c.MemPercent:0.0}%";
                Net = $"{Format.Bytes(c.NetRx)} / {Format.Bytes(c.NetTx)}";
                Block = $"{Format.Bytes(c.BlockRead)} / {Format.Bytes(c.BlockWrite)}";
            }
            else
            {
                CpuValue = MemValue = 0;
                Cpu = Memory = MemPercent = Net = Block = "";
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // 모든 속성
        }
    }

    internal static class Format
    {
        public static string Bytes(long b)
        {
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{b}B" : $"{v:0.##}{units[i]}";
        }
    }
}
