using System.Windows;
using System.Windows.Controls;

namespace IslandApp
{
    public partial class SettingsPanel : UserControl
    {
        // Сообщаем главному окну, что настройки изменились
        public event Action? Changed;

        private AppSettings? _settings;
        private bool _loading;

        public SettingsPanel()
        {
            InitializeComponent();
        }

        public void Bind(AppSettings settings)
        {
            _settings = settings;
            _loading = true;
            EqToggle.IsChecked = settings.EqualizerEnabled;
            GradientToggle.IsChecked = settings.AnimatedGradient;
            BatteryToggle.IsChecked = settings.ShowBattery;
            FullscreenToggle.IsChecked = settings.HideInFullscreen;
            MinimalToggle.IsChecked = settings.MinimalMode;
            _loading = false;
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _loading) return;

            _settings.EqualizerEnabled = EqToggle.IsChecked == true;
            _settings.AnimatedGradient = GradientToggle.IsChecked == true;
            _settings.ShowBattery = BatteryToggle.IsChecked == true;
            _settings.HideInFullscreen = FullscreenToggle.IsChecked == true;
            _settings.MinimalMode = MinimalToggle.IsChecked == true;
            _settings.Save();

            Changed?.Invoke();
        }
    }
}