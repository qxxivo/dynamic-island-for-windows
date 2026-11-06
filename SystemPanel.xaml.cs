using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandApp
{
    public partial class SystemPanel : UserControl
    {
        // Системные вызовы Windows: время работы процессора и состояние памяти
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MemoryStatus
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        private ulong _prevIdle, _prevKernel, _prevUser;

        public SystemPanel()
        {
            InitializeComponent();

            ReadCpuTimes(out _prevIdle, out _prevKernel, out _prevUser);

            _timer.Tick += (_, _) => Update();

            // Считаем нагрузку, только пока вкладка видна
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    ReadCpuTimes(out _prevIdle, out _prevKernel, out _prevUser);
                    _timer.Start();
                }
                else
                {
                    _timer.Stop();
                }
            };
        }

        private static ulong ToUlong(FILETIME ft) =>
            ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

        private static void ReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            GetSystemTimes(out var i, out var k, out var u);
            idle = ToUlong(i);
            kernel = ToUlong(k);
            user = ToUlong(u);
        }

        private void Update()
        {
            // Процессор
            ReadCpuTimes(out var idle, out var kernel, out var user);
            ulong idleDelta = idle - _prevIdle;
            ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser); // kernel уже включает idle
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

            double cpu = totalDelta == 0 ? 0 : (1.0 - (double)idleDelta / totalDelta) * 100.0;
            cpu = Math.Clamp(cpu, 0, 100);
            SetBar(CpuText, CpuFill, CpuTrack, cpu);

            // Память
            var mem = new MemoryStatus();
            if (GlobalMemoryStatusEx(mem))
            {
                double total = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
                double used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
                SetBar(RamText, RamFill, RamTrack, mem.dwMemoryLoad);
                RamDetail.Text = $"{used:F1} из {total:F1} ГБ";
            }
        }

        private static void SetBar(TextBlock label, Border fill, Grid track, double percent)
        {
            label.Text = $"{percent:F0}%";

            fill.Background = new SolidColorBrush(
                percent < 60 ? Color.FromRgb(0x30, 0xD1, 0x58)    // зелёный
                : percent < 85 ? Color.FromRgb(0xFF, 0x9F, 0x0A)  // оранжевый
                : Color.FromRgb(0xFF, 0x45, 0x3A));               // красный

            double target = track.ActualWidth * percent / 100.0;
            fill.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(400)));
        }
    }
}