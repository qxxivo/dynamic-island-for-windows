using System.Windows;
using System.Windows.Media;

namespace IslandApp
{
    // Рисует ряд закруглённых полосок
    public class EqualizerBars : FrameworkElement
    {
        private double[] _levels = Array.Empty<double>();

        public int BarCount { get; set; } = 16;
        public Brush BarBrush { get; set; } = Brushes.White;

        // Какую часть "ячейки" занимает полоска: меньше значение, тоньше полоски
        public double BarRatio { get; set; } = 0.6;

        public void SetLevels(float[] bands)
        {
            int n = BarCount;
            if (n <= 0 || bands.Length == 0) return;
            if (_levels.Length != n) _levels = new double[n];

            for (int i = 0; i < n; i++)
            {
                if (n <= bands.Length)
                {
                    // Полосок меньше, чем частот: усредняем группы
                    int start = i * bands.Length / n;
                    int end = (i + 1) * bands.Length / n;
                    if (end <= start) end = start + 1;

                    double sum = 0;
                    for (int k = start; k < end && k < bands.Length; k++) sum += bands[k];
                    _levels[i] = sum / (end - start);
                }
                else
                {
                    // Полосок больше: плавно интерполируем между соседними частотами
                    double pos = i * (bands.Length - 1.0) / (n - 1);
                    int k = (int)pos;
                    double frac = pos - k;
                    _levels[i] = k + 1 < bands.Length
                        ? bands[k] * (1 - frac) + bands[k + 1] * frac
                        : bands[k];
                }
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            int n = BarCount;
            double w = ActualWidth, h = ActualHeight;
            if (n <= 0 || w <= 0 || h <= 0) return;

            double slot = w / n;
            double barWidth = slot * BarRatio;
            double offset = (slot - barWidth) / 2;

            for (int i = 0; i < n; i++)
            {
                double level = i < _levels.Length ? _levels[i] : 0;
                double barHeight = Math.Max(barWidth, level * h);
                var rect = new Rect(i * slot + offset, h - barHeight, barWidth, barHeight);
                dc.DrawRoundedRectangle(BarBrush, null, rect, barWidth / 2, barWidth / 2);
            }
        }
    }
}