using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PodmanMonitor.Ui
{
    /// <summary>창이 활성 상태가 아니면 작업 표시줄 단추를 깜빡여 알린다.</summary>
    internal static class WindowFlash
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);

        private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

        public static void IfInactive(Window window)
        {
            try
            {
                if (window == null || window.IsActive) return;
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG };
                FlashWindowEx(ref info);
            }
            catch
            {
                // 알림 보조 기능 — 실패해도 무시(플러그인 예외가 Folderss 로 번지지 않게)
            }
        }
    }
}
