using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace IslandApp
{
    public record ClipEntry(string Full, string Preview);
    public record ShelfEntry(string Path, string Name, ImageSource? Icon, string Glyph);

    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings = AppSettings.Load();

        // ---------- Размеры острова ----------
        private const double ExpandedWidth = 460;
        private const double ExpandedHeight = 268;
        private const double ExpandedCorner = 30;

        private const double CompactHeightNormal = 30;
        private const double CompactHeightMinimal = 8;
        private const double CompactWidthMinimal = 110;

        // ---------- Пружина: одна формула для всех анимаций ----------
        private sealed class Spring
        {
            public double Value, Velocity, Target;
            public double Stiffness = 380;   // жёсткость: больше значение, быстрее движение
            public double Damping = 39;      // затухание: меньше значение, сильнее отскок

            public Spring(double value) { Value = Target = value; }

            // Возвращает true, пока пружина ещё движется
            public bool Step(double dt)
            {
                const int substeps = 2;
                double h = dt / substeps;
                for (int i = 0; i < substeps; i++)
                {
                    double acceleration = -Stiffness * (Value - Target) - Damping * Velocity;
                    Velocity += acceleration * h;
                    Value += Velocity * h;
                }

                if (Math.Abs(Value - Target) < 0.001 && Math.Abs(Velocity) < 0.01)
                {
                    Value = Target;
                    Velocity = 0;
                    return false;
                }
                return true;
            }
        }

        private Spring _expand = null!;    // 0 = свёрнут, 1 = раскрыт
        private Spring _compactW = null!;  // ширина свёрнутого острова
        private Spring _compactH = null!;  // высота свёрнутого острова
        private bool _animating;
        private TimeSpan _lastFrame = TimeSpan.Zero;

        private bool _expanded;
        private bool _forceOpen;           // остров держится открытым, пока звонит будильник
        private bool _isPlaying;
        private bool _batteryShown;
        private Color? _lastAccent;
        private int _currentTab = -1;      // 0 медиа, 1 буфер, 2 файлы, 3 таймер, 4 заметки, 5 система, 6 погода, 7 настройки

        // Цвет эквалайзера меняется в зависимости от обложки
        private readonly SolidColorBrush _eqBrush = new(Colors.White);

        // Свечение: два мягких радиальных пятна, которые медленно плавают
        private readonly Stopwatch _glowClock = Stopwatch.StartNew();
        private readonly DispatcherTimer _glowTimer = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        private RadialGradientBrush _glowA = null!, _glowB = null!;
        private GradientStop _glowA0 = null!, _glowA1 = null!, _glowB0 = null!, _glowB1 = null!;

        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        private readonly DispatcherTimer _eqTimer = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        private AudioAnalyzer? _audio;

        // Полноэкранный режим
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);
        private bool _hiddenByFullscreen;

        // Батарея
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        // Медиа
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;

        // Буфер обмена
        [DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int MaxClipItems = 20;
        private readonly ObservableCollection<ClipEntry> _clipItems = new();

        // Файлы
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        private readonly ObservableCollection<ShelfEntry> _shelfItems = new();
        private Point _dragStart;
        private bool _draggingOut;

        public MainWindow()
        {
            InitializeComponent();

            // Окно неподвижное: 500x300 по центру вверху экрана
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;

            _expand = new Spring(0);
            _compactW = new Spring(TargetCompactWidth()) { Stiffness = 320, Damping = 36 };
            _compactH = new Spring(TargetCompactHeight()) { Stiffness = 320, Damping = 36 };

            // Свечение: два слоя под содержимым острова
            _glowA = CreateBlob(out _glowA0, out _glowA1);
            _glowB = CreateBlob(out _glowB0, out _glowB1);
            GlowA.Background = _glowA;
            GlowB.Background = _glowB;
            PlaceGlow(0, 0);
            _glowTimer.Tick += (_, _) => PlaceGlow(_glowClock.Elapsed.TotalSeconds, 1);

            MiniEq.BarBrush = _eqBrush;
            BigEq.BarBrush = _eqBrush;

            ApplyLayout();

            ShowTime();
            _clockTimer.Tick += (_, _) =>
            {
                ShowTime();
                CheckFullscreen();
                UpdateBattery();
            };
            _clockTimer.Start();

            // Наведение определяем опросом курсора: так надёжнее, чем события окна
            _hoverTimer.Tick += (_, _) => HoverTick();
            _hoverTimer.Start();

            ClipList.ItemsSource = _clipItems;
            ShelfList.ItemsSource = _shelfItems;
            SetTab(0);

            TimerPanelView.Alert += () =>
            {
                _forceOpen = true;
                SetExpanded(true);
                SetTab(3, true);
            };
            TimerPanelView.AlertDismissed += () => _forceOpen = false;

            SettingsPanelView.Bind(_settings);
            SettingsPanelView.Changed += () => ApplySettings();

            _eqTimer.Tick += (_, _) =>
            {
                if (_audio == null) return;
                var levels = _audio.GetLevels();
                MiniEq.SetLevels(levels);
                BigEq.SetLevels(levels);
            };

            ApplySettings();
            _ = InitMediaAsync();
        }

        private void ShowTime()
        {
            ClockText.Text = DateTime.Now.ToString("HH:mm");
        }

        // ---------- Анимация острова ----------
        private double TargetCompactWidth()
        {
            if (_settings.MinimalMode) return CompactWidthMinimal;

            // Ширина подстраивается под то, что показано по бокам: обложка, батарея, эквалайзер
            double left = 12;
            if (_isPlaying) left += 28;        // обложка 20 + отступ 8
            if (_batteryShown) left += 58;     // значок батареи с процентами

            double right = (_isPlaying && _settings.EqualizerEnabled) ? 36 : 0;   // эквалайзер

            double side = Math.Max(Math.Max(left, right), 32);
            return 2 * side + 56;              // 56 = место под время
        }

        private double TargetCompactHeight() =>
            _settings.MinimalMode ? CompactHeightMinimal : CompactHeightNormal;

        private void UpdateCompactTargets()
        {
            _compactW.Target = TargetCompactWidth();
            _compactH.Target = TargetCompactHeight();
            StartAnimating();
        }

        private void SetExpanded(bool expanded)
        {
            if (_expanded == expanded) return;
            _expanded = expanded;

            _expand.Target = expanded ? 1 : 0;
            // Раскрывается с лёгким отскоком, сворачивается спокойно, без отскока
            _expand.Stiffness = 380;
            _expand.Damping = expanded ? 28 : 39;

            StartAnimating();
        }

        private void StartAnimating()
        {
            if (_animating) return;
            _animating = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var args = (RenderingEventArgs)e;
            if (args.RenderingTime == _lastFrame) return;

            double dt = _lastFrame == TimeSpan.Zero
                ? 1.0 / 60
                : (args.RenderingTime - _lastFrame).TotalSeconds;
            _lastFrame = args.RenderingTime;
            dt = Math.Clamp(dt, 0.001, 1.0 / 20);

            bool moving = _expand.Step(dt);
            moving |= _compactW.Step(dt);
            moving |= _compactH.Step(dt);

            ApplyLayout();

            if (!moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                _animating = false;
            }
        }

        // Все размеры и прозрачности считаются из значений пружин
        private void ApplyLayout()
        {
            double p = _expand.Value;
            double pc = Math.Max(0, p);

            double cw = _compactW.Value;
            double ch = _compactH.Value;

            double width = cw + (ExpandedWidth - cw) * pc;
            double height = ch + (ExpandedHeight - ch) * pc;
            double corner = ch / 2 + (ExpandedCorner - ch / 2) * Math.Min(pc, 1.0);

            var radius = new CornerRadius(0, 0, corner, corner);
            RootBorder.Width = Math.Max(1, width);
            RootBorder.Height = Math.Max(1, height);
            RootBorder.CornerRadius = radius;
            GlowA.CornerRadius = radius;
            GlowB.CornerRadius = radius;

            // Содержимое проявляется во второй половине раскрытия
            ExpandedContent.Opacity = Math.Clamp((p - 0.45) / 0.4, 0, 1);

            // Свёрнутая строка: в минимальном режиме скрыта, при раскрытии видна
            double compactVisible = Math.Clamp((ch - 12) / (CompactHeightNormal - 12), 0, 1);
            double fromExpand = Math.Clamp((p - 0.15) / 0.3, 0, 1);
            CompactRow.Opacity = Math.Max(compactVisible, fromExpand);
        }

        // ---------- Наведение ----------
        private static bool IsTyping() =>
            Keyboard.FocusedElement is System.Windows.Controls.TextBox;

        private bool IsCursorOverIsland(double pad)
        {
            if (!GetCursorPos(out var p)) return false;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return false;

            var dip = source.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));
            double centerX = Left + ActualWidth / 2;
            double halfWidth = RootBorder.Width / 2;

            return dip.X >= centerX - halfWidth - pad && dip.X <= centerX + halfWidth + pad
                && dip.Y >= Top - 1 && dip.Y <= Top + RootBorder.Height + pad;
        }

        private void HoverTick()
        {
            if (_hiddenByFullscreen) return;

            bool over = IsCursorOverIsland(_expanded ? 6 : 3);

            if (over && !_expanded)
                SetExpanded(true);
            else if (!over && _expanded && !IsTyping() && !_draggingOut && !_forceOpen)
                SetExpanded(false);
        }

        // ---------- Свечение ----------
        private static RadialGradientBrush CreateBlob(out GradientStop inner, out GradientStop outer)
        {
            var brush = new RadialGradientBrush { RadiusX = 0.6, RadiusY = 1.2 };
            inner = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0);
            outer = new GradientStop(Color.FromArgb(0, 0, 0, 0), 1);
            brush.GradientStops.Add(inner);
            brush.GradientStops.Add(outer);
            return brush;
        }

        // t = время в секундах, k = 1 пятна плавают, k = 0 стоят на месте
        private void PlaceGlow(double t, double k)
        {
            var a = new Point(
                0.16 + k * 0.10 * Math.Sin(t * 0.45),
                0.42 + k * 0.28 * Math.Sin(t * 0.31 + 1.3));
            var b = new Point(
                0.84 + k * 0.10 * Math.Sin(t * 0.38 + 2.1),
                0.58 + k * 0.28 * Math.Sin(t * 0.27 + 4.0));

            _glowA.Center = a;
            _glowA.GradientOrigin = a;
            _glowA.RadiusX = 0.62 + k * 0.10 * Math.Sin(t * 0.37);
            _glowA.RadiusY = 1.2 + k * 0.20 * Math.Sin(t * 0.29 + 0.8);

            _glowB.Center = b;
            _glowB.GradientOrigin = b;
            _glowB.RadiusX = 0.55 + k * 0.10 * Math.Sin(t * 0.33 + 1.7);
            _glowB.RadiusY = 1.1 + k * 0.20 * Math.Sin(t * 0.41);
        }

        // Движение идёт, только пока играет музыка и включена анимация
        private void UpdateGlowTimer()
        {
            bool run = _settings.AnimatedGradient && _isPlaying
                && _lastAccent != null && !_hiddenByFullscreen;

            if (run)
            {
                if (!_glowTimer.IsEnabled) _glowTimer.Start();
            }
            else
            {
                if (_glowTimer.IsEnabled) _glowTimer.Stop();
                if (!_settings.AnimatedGradient) PlaceGlow(0, 0);
            }
        }

        // ---------- Цвет обложки ----------
        private static Color WithAlpha(Color c, double alpha) =>
            Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

        private void ApplyAccent(Color? accent)
        {
            var duration = TimeSpan.FromMilliseconds(600);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            Color eqColor = accent ?? Colors.White;
            Color a0 = Color.FromArgb(0, 0, 0, 0), a1 = a0, b0 = a0, b1 = a0;

            if (accent is Color ac)
            {
                var (h, s, _) = ToHsv(ac.R / 255.0, ac.G / 255.0, ac.B / 255.0);
                Color colorA = FromHsv(h, s, 0.9);
                Color colorB = FromHsv((h + 40) % 360, s, 0.85);   // второе пятно чуть другого оттенка

                a0 = WithAlpha(colorA, 0.45);   // яркость основного пятна
                a1 = WithAlpha(colorA, 0);
                b0 = WithAlpha(colorB, 0.30);   // яркость второго пятна
                b1 = WithAlpha(colorB, 0);
            }

            void Animate(Animatable target, DependencyProperty property, Color to) =>
                target.BeginAnimation(property, new ColorAnimation(to, duration) { EasingFunction = ease });

            Animate(_eqBrush, SolidColorBrush.ColorProperty, eqColor);
            Animate(_glowA0, GradientStop.ColorProperty, a0);
            Animate(_glowA1, GradientStop.ColorProperty, a1);
            Animate(_glowB0, GradientStop.ColorProperty, b0);
            Animate(_glowB1, GradientStop.ColorProperty, b1);
        }

        // Находит яркий доминирующий цвет картинки
        private static Color ExtractAccent(BitmapSource source)
        {
            double scale = 32.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            BitmapSource small = scale < 1
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;

            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;

                double B = pixels[i] / 255.0;
                double G = pixels[i + 1] / 255.0;
                double R = pixels[i + 2] / 255.0;

                double max = Math.Max(R, Math.Max(G, B));
                double min = Math.Min(R, Math.Min(G, B));
                double saturation = max <= 0 ? 0 : (max - min) / max;

                // Яркие насыщенные пиксели важнее серых и очень тёмных
                double weight = 0.02 + saturation * saturation * Math.Min(1, max * 2);
                r += R * weight;
                g += G * weight;
                b += B * weight;
                total += weight;
            }

            if (total <= 0) return Colors.White;

            var (hue, sat, val) = ToHsv(r / total, g / total, b / total);
            sat = sat < 0.12 ? sat : Math.Clamp(sat, 0.5, 0.95);   // серые обложки остаются серыми
            val = Math.Clamp(val, 0.8, 1.0);
            return FromHsv(hue, sat, val);
        }

        private static (double H, double S, double V) ToHsv(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double h = 0;

            if (d > 0)
            {
                if (max == r) h = ((g - b) / d) % 6;
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;

                h *= 60;
                if (h < 0) h += 360;
            }
            return (h, max <= 0 ? 0 : d / max, max);
        }

        private static Color FromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;

            if (h < 60) (r, g, b) = (c, x, 0);
            else if (h < 120) (r, g, b) = (x, c, 0);
            else if (h < 180) (r, g, b) = (0, c, x);
            else if (h < 240) (r, g, b) = (0, x, c);
            else if (h < 300) (r, g, b) = (x, 0, c);
            else (r, g, b) = (c, 0, x);

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        // ---------- Настройки ----------
        private void ApplySettings()
        {
            var eqVisibility = _settings.EqualizerEnabled ? Visibility.Visible : Visibility.Collapsed;
            MiniEq.Visibility = eqVisibility;
            BigEq.Visibility = eqVisibility;

            if (_settings.EqualizerEnabled) StartEqualizer();
            else StopEqualizer();

            UpdateBattery();
            UpdateCompactTargets();
            UpdateGlowTimer();
            CheckFullscreen();
        }

        private void StartEqualizer()
        {
            if (_audio == null)
            {
                try { _audio = new AudioAnalyzer(); }
                catch { /* нет устройства вывода звука */ }
            }
            if (_audio != null && !_hiddenByFullscreen) _eqTimer.Start();
        }

        private void StopEqualizer()
        {
            _eqTimer.Stop();
            _audio?.Dispose();
            _audio = null;

            var zeros = new float[AudioAnalyzer.Bands];
            MiniEq.SetLevels(zeros);
            BigEq.SetLevels(zeros);
        }

        // ---------- Полноэкранный режим ----------
        private void CheckFullscreen()
        {
            bool shouldHide = false;

            if (_settings.HideInFullscreen && SHQueryUserNotificationState(out int state) == 0)
            {
                // 2 = полноэкранное приложение, 3 = полноэкранный Direct3D (игры), 4 = презентация
                shouldHide = state == 2 || state == 3 || state == 4;
            }

            if (shouldHide && !_hiddenByFullscreen)
            {
                _hiddenByFullscreen = true;
                _eqTimer.Stop();
                UpdateGlowTimer();
                Hide();
            }
            else if (!shouldHide && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                Show();
                if (_settings.EqualizerEnabled && _audio != null) _eqTimer.Start();
                UpdateGlowTimer();
            }
        }

        // ---------- Батарея ----------
        private void UpdateBattery()
        {
            bool show = false;

            if (_settings.ShowBattery
                && GetSystemPowerStatus(out var status)
                && (status.BatteryFlag & 128) == 0      // 128 = батареи нет (обычный ПК)
                && status.BatteryFlag != 255
                && status.BatteryLifePercent <= 100)
            {
                show = true;
                int percent = status.BatteryLifePercent;
                bool charging = (status.BatteryFlag & 8) != 0;

                BatteryText.Text = percent + "%";
                BatteryFill.Width = Math.Max(1, 15 * percent / 100.0);
                BatteryFill.Background = charging ? (Brush)FindResource("Green")
                    : percent <= 20 ? (Brush)FindResource("Red")
                    : Brushes.White;
            }

            BatteryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // Остров меняет ширину, когда батарея появляется или пропадает
            if (show != _batteryShown)
            {
                _batteryShown = show;
                UpdateCompactTargets();
            }
        }

        // ---------- Вкладки ----------
        private void TabMediaClick(object sender, RoutedEventArgs e) => SetTab(0, true);
        private void TabClipClick(object sender, RoutedEventArgs e) => SetTab(1, true);
        private void TabShelfClick(object sender, RoutedEventArgs e) => SetTab(2, true);
        private void TabTimerClick(object sender, RoutedEventArgs e) => SetTab(3, true);
        private void TabNotesClick(object sender, RoutedEventArgs e) => SetTab(4, true);
        private void TabSystemClick(object sender, RoutedEventArgs e) => SetTab(5, true);
        private void TabWeatherClick(object sender, RoutedEventArgs e) => SetTab(6, true);
        private void TabSettingsClick(object sender, RoutedEventArgs e) => SetTab(7, true);

        private void SetTab(int tab, bool animate = false)
        {
            bool changed = tab != _currentTab;
            _currentTab = tab;

            FrameworkElement[] panels =
            {
                MediaPanel, ClipPanel, ShelfPanel, TimerPanelView,
                NotesPanelView, SystemPanelView, WeatherPanelView, SettingsPanelView
            };
            System.Windows.Controls.Button[] buttons =
            {
                TabMedia, TabClip, TabShelf, TabTimer, TabNotes, TabSystem, TabWeather, TabSettings
            };

            var activeBackground = (Brush)FindResource("Surface2");
            var idleForeground = (Brush)FindResource("TextSecondary");

            for (int i = 0; i < panels.Length; i++)
            {
                bool active = i == tab;
                panels[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                buttons[i].Background = active ? activeBackground : Brushes.Transparent;
                buttons[i].Foreground = active ? Brushes.White : idleForeground;

                // Мягкое появление вкладки при переключении
                if (active && changed && animate)
                {
                    panels[i].BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                }
            }

            // Кнопка справа: "Очистить" для буфера и файлов, "Выйти" в настройках
            ClearButton.Visibility = (tab == 1 || tab == 2 || tab == 7)
                ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.Content = tab == 7 ? "Выйти" : "Очистить";
            ClearButton.Foreground = tab == 7 ? (Brush)FindResource("Red") : (Brush)FindResource("Accent");
        }

        private void ClearClick(object sender, RoutedEventArgs e)
        {
            if (_currentTab == 1)
            {
                _clipItems.Clear();
                ClipEmptyText.Visibility = Visibility.Visible;
            }
            else if (_currentTab == 2)
            {
                _shelfItems.Clear();
                ShelfEmptyText.Visibility = Visibility.Visible;
            }
            else if (_currentTab == 7)
            {
                Application.Current.Shutdown();
            }
        }

        // ---------- Буфер обмена (текст) ----------
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(WndProc);
            AddClipboardFormatListener(handle);
        }

        protected override void OnClosed(EventArgs e)
        {
            _audio?.Dispose();
            RemoveClipboardFormatListener(new WindowInteropHelper(this).Handle);
            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                _ = ReadClipboardAsync();
            }
            return IntPtr.Zero;
        }

        private async Task ReadClipboardAsync()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return;
                    string text = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(text)) return;
                    AddClipEntry(text);
                    return;
                }
                catch (COMException)
                {
                    await Task.Delay(80);
                }
            }
        }

        private void AddClipEntry(string text)
        {
            var existing = _clipItems.FirstOrDefault(c => c.Full == text);
            if (existing != null) _clipItems.Remove(existing);

            string preview = text.Replace("\r", " ").Replace("\n", " ").Trim();
            _clipItems.Insert(0, new ClipEntry(text, preview));

            while (_clipItems.Count > MaxClipItems)
                _clipItems.RemoveAt(_clipItems.Count - 1);

            ClipEmptyText.Visibility = Visibility.Collapsed;
        }

        private void ClipItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string full })
            {
                try { Clipboard.SetText(full); }
                catch (COMException) { }
            }
        }

        // ---------- Файлы: перетаскивание на остров ----------
        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (_draggingOut || !HasFiles(e))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Copy;
            SetExpanded(true);
            SetTab(2, true);
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = (!_draggingOut && HasFiles(e)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_draggingOut || !HasFiles(e)) return;

            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                foreach (var path in paths)
                    AddShelfEntry(path);
            }
        }

        private void AddShelfEntry(string path)
        {
            bool isFile = File.Exists(path);
            bool isDir = Directory.Exists(path);
            if (!isFile && !isDir) return;

            if (_shelfItems.Any(s => s.Path == path)) return;

            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = path;

            var icon = isFile ? LoadIcon(path) : null;
            string glyph = icon != null ? "" : (isDir ? "📁" : "📄");

            _shelfItems.Insert(0, new ShelfEntry(path, name, icon, glyph));
            ShelfEmptyText.Visibility = Visibility.Collapsed;
        }

        private static ImageSource? LoadIcon(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        }

        // ---------- Файлы: плитки на полке ----------
        private void TileMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ShelfEntry entry }) return;

            if (e.ClickCount == 2)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
                }
                catch { }
                return;
            }

            _dragStart = e.GetPosition(null);
        }

        private void TileMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggingOut) return;
            if (sender is not FrameworkElement { DataContext: ShelfEntry entry } tile) return;

            var diff = e.GetPosition(null) - _dragStart;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path))
            {
                _shelfItems.Remove(entry);
                return;
            }

            _draggingOut = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { entry.Path });
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
            }
            finally
            {
                _draggingOut = false;
            }
        }

        private void RemoveShelfClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ShelfEntry entry })
            {
                _shelfItems.Remove(entry);
                if (_shelfItems.Count == 0)
                    ShelfEmptyText.Visibility = Visibility.Visible;
            }
        }

        // ---------- Медиа ----------
        private async Task InitMediaAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Dispatcher.InvokeAsync(AttachSession);
            AttachSession();
        }

        private void AttachSession()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnMediaChanged;
            }

            _session = _manager?.GetCurrentSession();

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaChanged;
                _session.PlaybackInfoChanged += OnMediaChanged;
            }

            _ = UpdateMediaAsync();
        }

        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        {
            Dispatcher.InvokeAsync(UpdateMediaAsync);
        }

        private void SetAccentIfChanged(Color? accent)
        {
            if (_lastAccent == accent) return;
            _lastAccent = accent;
            ApplyAccent(accent);
        }

        private async Task UpdateMediaAsync()
        {
            var defaultCover = (Brush)FindResource("Surface2");
            var session = _session;

            if (session == null)
            {
                TitleText.Text = "Ничего не играет";
                ArtistText.Text = "";
                CoverBorder.Background = defaultCover;
                MiniCover.Visibility = Visibility.Collapsed;
                PlayPauseIcon.Data = (Geometry)FindResource("IconPlay");

                _isPlaying = false;
                SetAccentIfChanged(null);
                UpdateCompactTargets();
                UpdateGlowTimer();
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Ничего не играет" : props.Title;
            ArtistText.Text = props.Artist;

            Brush cover = defaultCover;
            Color? accent = null;

            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream.AsStreamForRead();
                    bitmap.EndInit();
                    bitmap.Freeze();

                    cover = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                    accent = ExtractAccent(bitmap);
                }
                catch { /* обложки нет, оставляем серый фон */ }
            }

            CoverBorder.Background = cover;
            MiniCover.Background = cover;

            bool playing = session.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            PlayPauseIcon.Data = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
            MiniCover.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;

            _isPlaying = playing;
            SetAccentIfChanged(accent);
            UpdateCompactTargets();
            UpdateGlowTimer();
        }

        private async void PrevClick(object sender, RoutedEventArgs e)
        {
            if (_session != null) await _session.TrySkipPreviousAsync();
        }

        private async void PlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_session != null) await _session.TryTogglePlayPauseAsync();
        }

        private async void NextClick(object sender, RoutedEventArgs e)
        {
            if (_session != null) await _session.TrySkipNextAsync();
        }
    }
}