using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IslandApp
{
    public partial class WeatherPanel : UserControl
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly CultureInfo Ru = new("ru-RU");

        private static readonly string CityPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IslandApp", "city.txt");

        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(20) };
        private string _city = "";
        private bool _loading;

        public WeatherPanel()
        {
            InitializeComponent();

            try
            {
                if (File.Exists(CityPath))
                    _city = File.ReadAllText(CityPath).Trim();
            }
            catch { }

            if (_city.Length > 0)
            {
                CityBox.Text = _city;
                _ = LoadAsync(_city);
            }
            else
            {
                ShowStatus("Введи город и нажми Enter");
            }

            _refresh.Tick += (_, _) =>
            {
                if (_city.Length > 0) _ = LoadAsync(_city);
            };
            _refresh.Start();
        }

        private void CityBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = LoadAsync(CityBox.Text.Trim());
                Keyboard.ClearFocus();
            }
        }

        private void SearchClick(object sender, RoutedEventArgs e)
        {
            _ = LoadAsync(CityBox.Text.Trim());
            Keyboard.ClearFocus();
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadAsync(string city)
        {
            if (string.IsNullOrWhiteSpace(city) || _loading) return;
            _loading = true;

            try
            {
                ShowStatus("Загрузка…");

                // 1. Находим координаты города
                string geoUrl = "https://geocoding-api.open-meteo.com/v1/search?name="
                    + Uri.EscapeDataString(city) + "&count=1&language=ru&format=json";
                using var geo = JsonDocument.Parse(await Http.GetStringAsync(geoUrl));

                if (!geo.RootElement.TryGetProperty("results", out var results)
                    || results.GetArrayLength() == 0)
                {
                    ShowStatus("Город не найден");
                    return;
                }

                var place = results[0];
                double lat = place.GetProperty("latitude").GetDouble();
                double lon = place.GetProperty("longitude").GetDouble();
                string name = place.TryGetProperty("name", out var n) ? (n.GetString() ?? city) : city;

                // 2. Запрашиваем погоду
                string url = "https://api.open-meteo.com/v1/forecast"
                    + "?latitude=" + lat.ToString(CultureInfo.InvariantCulture)
                    + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture)
                    + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m"
                    + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                    + "&wind_speed_unit=ms&timezone=auto&forecast_days=4";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));

                var cur = doc.RootElement.GetProperty("current");
                double temp = cur.GetProperty("temperature_2m").GetDouble();
                double feels = cur.GetProperty("apparent_temperature").GetDouble();
                double humidity = cur.GetProperty("relative_humidity_2m").GetDouble();
                double wind = cur.GetProperty("wind_speed_10m").GetDouble();
                int code = (int)cur.GetProperty("weather_code").GetDouble();

                var (icon, text) = Describe(code);
                IconText.Text = icon;
                TempText.Text = Deg(temp);
                DescText.Text = text;
                DetailText.Text = $"Ощущается {Deg(feels)} · ветер {wind:0} м/с · {humidity:0}%";

                // 3. Прогноз на 3 дня (первый элемент массива это сегодня, пропускаем)
                var daily = doc.RootElement.GetProperty("daily");
                var times = daily.GetProperty("time");
                var codes = daily.GetProperty("weather_code");
                var maxes = daily.GetProperty("temperature_2m_max");
                var mins = daily.GetProperty("temperature_2m_min");

                var nameBlocks = new[] { D1Name, D2Name, D3Name };
                var iconBlocks = new[] { D1Icon, D2Icon, D3Icon };
                var tempBlocks = new[] { D1Temp, D2Temp, D3Temp };

                for (int i = 0; i < 3; i++)
                {
                    int idx = i + 1;
                    if (idx >= times.GetArrayLength()) break;

                    var date = DateTime.ParseExact(times[idx].GetString()!, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                    nameBlocks[i].Text = i == 0 ? "Завтра" : DayName(date);
                    iconBlocks[i].Text = Describe((int)codes[idx].GetDouble()).Icon;
                    tempBlocks[i].Text = $"{Deg(maxes[idx].GetDouble())} / {Deg(mins[idx].GetDouble())}";
                }

                // 4. Запоминаем город
                _city = name;
                CityBox.Text = name;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CityPath)!);
                    File.WriteAllText(CityPath, name);
                }
                catch { }

                ShowStatus("");
            }
            catch (Exception)
            {
                ShowStatus("Нет связи с сервисом погоды");
            }
            finally
            {
                _loading = false;
            }
        }

        private static string Deg(double value) => $"{(int)Math.Round(value)}°";

        private static string DayName(DateTime date)
        {
            string s = Ru.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek);
            return s.Length > 0 ? char.ToUpper(s[0], Ru) + s[1..] : s;
        }

        private static (string Icon, string Text) Describe(int code) => code switch
        {
            0 => ("☀️", "Ясно"),
            1 => ("🌤", "Малооблачно"),
            2 => ("⛅", "Переменная облачность"),
            3 => ("☁️", "Пасмурно"),
            45 or 48 => ("🌫", "Туман"),
            >= 51 and <= 57 => ("🌦", "Морось"),
            >= 61 and <= 67 => ("🌧", "Дождь"),
            >= 71 and <= 77 => ("❄️", "Снег"),
            >= 80 and <= 82 => ("🌧", "Ливень"),
            85 or 86 => ("🌨", "Снегопад"),
            95 => ("⛈", "Гроза"),
            96 or 99 => ("⛈", "Гроза с градом"),
            _ => ("🌡", "—")
        };
    }
}