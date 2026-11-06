using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace IslandApp
{
    public partial class TimerPanel : UserControl
    {
        // Сообщаем главному окну, что нужно раскрыться / можно сворачиваться
        public event Action? Alert;
        public event Action? AlertDismissed;

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _beep = new() { Interval = TimeSpan.FromSeconds(1.5) };

        private TimeSpan _timerDuration = TimeSpan.FromMinutes(5);
        private TimeSpan _timerRemaining = TimeSpan.FromMinutes(5);
        private DateTime? _timerEnd;   // не null, пока таймер идёт
        private DateTime? _alarmAt;    // не null, пока будильник установлен
        private DateTime _ringStarted;

        public TimerPanel()
        {
            InitializeComponent();

            _tick.Tick += OnTick;
            _tick.Start();
            _beep.Tick += OnBeep;

            RefreshTimerUi();
        }

        // ---------- Таймер ----------
        private static string Format(TimeSpan t)
        {
            int total = Math.Max((int)Math.Ceiling(t.TotalSeconds), 0);
            var ts = TimeSpan.FromSeconds(total);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
        }

        private void RefreshTimerUi()
        {
            TimerText.Text = Format(_timerRemaining);

            if (_timerEnd != null) StartButton.Content = "Пауза";
            else if (_timerRemaining < _timerDuration) StartButton.Content = "Продолжить";
            else StartButton.Content = "Старт";
        }

        private void AdjustClick(object sender, RoutedEventArgs e)
        {
            if (_timerEnd != null) return; // пока таймер идёт, время не меняем

            int delta = int.Parse((string)((Button)sender).Tag);
            var newDuration = _timerDuration + TimeSpan.FromMinutes(delta);
            if (newDuration < TimeSpan.FromMinutes(1)) newDuration = TimeSpan.FromMinutes(1);

            _timerDuration = newDuration;
            _timerRemaining = newDuration;
            RefreshTimerUi();
        }

        private void StartClick(object sender, RoutedEventArgs e)
        {
            if (_timerEnd != null)
            {
                // Пауза
                _timerRemaining = _timerEnd.Value - DateTime.Now;
                if (_timerRemaining < TimeSpan.Zero) _timerRemaining = TimeSpan.Zero;
                _timerEnd = null;
            }
            else
            {
                if (_timerRemaining <= TimeSpan.Zero) _timerRemaining = _timerDuration;
                _timerEnd = DateTime.Now + _timerRemaining;
            }
            RefreshTimerUi();
        }

        private void ResetClick(object sender, RoutedEventArgs e)
        {
            _timerEnd = null;
            _timerRemaining = _timerDuration;
            RefreshTimerUi();
        }

        // ---------- Будильник ----------
        private void AlarmClick(object sender, RoutedEventArgs e)
        {
            // Если будильник уже стоит, эта же кнопка его отменяет
            if (_alarmAt != null)
            {
                _alarmAt = null;
                RefreshAlarmUi();
                return;
            }

            if (!TimeSpan.TryParse(AlarmBox.Text.Trim(), out var time)
                || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            {
                AlarmStatus.Text = "Введи время так: 07:30";
                return;
            }

            var at = DateTime.Today + time;
            if (at <= DateTime.Now) at = at.AddDays(1); // время уже прошло, значит завтра

            _alarmAt = at;
            RefreshAlarmUi();
        }

        private void RefreshAlarmUi()
        {
            if (_alarmAt is DateTime at)
            {
                string day = at.Date == DateTime.Today ? "сегодня" : "завтра";
                AlarmStatus.Text = $"Сработает {day} в {at:HH:mm}";
                AlarmButton.Content = "Отменить";
                AlarmBox.IsEnabled = false;
            }
            else
            {
                AlarmStatus.Text = "Не установлен";
                AlarmButton.Content = "Поставить";
                AlarmBox.IsEnabled = true;
            }
        }

        // ---------- Проверка раз в 250 мс ----------
        private void OnTick(object? sender, EventArgs e)
        {
            if (_timerEnd is DateTime end)
            {
                var left = end - DateTime.Now;
                if (left <= TimeSpan.Zero)
                {
                    _timerEnd = null;
                    _timerRemaining = _timerDuration;
                    RefreshTimerUi();
                    Ring("⏱ Таймер завершён");
                }
                else
                {
                    TimerText.Text = Format(left);
                }
            }

            if (_alarmAt is DateTime at && DateTime.Now >= at)
            {
                _alarmAt = null;
                RefreshAlarmUi();
                Ring("⏰ Будильник");
            }
        }

        // ---------- Сигнал ----------
        private void Ring(string title)
        {
            RingTitle.Text = title;
            RingOverlay.Visibility = Visibility.Visible;
            _ringStarted = DateTime.Now;

            SystemSounds.Exclamation.Play();
            _beep.Start();

            Alert?.Invoke();
        }

        private void OnBeep(object? sender, EventArgs e)
        {
            // Если никто не отреагировал минуту, замолкаем сами
            if (DateTime.Now - _ringStarted > TimeSpan.FromSeconds(60))
            {
                StopRinging();
                return;
            }
            SystemSounds.Exclamation.Play();
        }

        private void StopClick(object sender, RoutedEventArgs e) => StopRinging();

        private void StopRinging()
        {
            _beep.Stop();
            RingOverlay.Visibility = Visibility.Collapsed;
            AlertDismissed?.Invoke();
        }
    }
}