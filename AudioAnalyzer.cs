using NAudio.Wave;

namespace IslandApp
{
    // Слушает звук, который играет в системе, и превращает его в 16 "полосок" частот
    public class AudioAnalyzer : IDisposable
    {
        public const int Bands = 16;
        private const int FftSize = 2048;

        private readonly WasapiLoopbackCapture _capture;
        private readonly int _channels;
        private readonly int _bits;

        private readonly float[] _ring = new float[FftSize];
        private int _ringPos;
        private readonly object _lock = new();
        private long _lastDataTicks;

        private readonly double[] _re = new double[FftSize];
        private readonly double[] _im = new double[FftSize];
        private readonly double[] _window = new double[FftSize];
        private readonly int[] _bandStart = new int[Bands];
        private readonly int[] _bandEnd = new int[Bands];
        private readonly double[] _target = new double[Bands];
        private readonly double[] _smooth = new double[Bands];

        public AudioAnalyzer()
        {
            _capture = new WasapiLoopbackCapture();
            var fmt = _capture.WaveFormat;
            _channels = fmt.Channels;
            _bits = fmt.BitsPerSample;

            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));

            // Границы полосок: от 60 Гц до 12 кГц, шаг логарифмический
            double binHz = fmt.SampleRate / (double)FftSize;
            double fMin = 60;
            double fMax = Math.Min(12000, fmt.SampleRate / 2.0 * 0.95);
            for (int b = 0; b < Bands; b++)
            {
                double f0 = fMin * Math.Pow(fMax / fMin, (double)b / Bands);
                double f1 = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / Bands);
                int start = Math.Max(1, (int)Math.Round(f0 / binHz));
                int end = Math.Max(start, (int)Math.Round(f1 / binHz) - 1);
                _bandStart[b] = Math.Min(start, FftSize / 2 - 1);
                _bandEnd[b] = Math.Min(end, FftSize / 2 - 1);
            }

            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);

            if (_bits != 32 && _bits != 16) return;
            int bytesPerSample = _bits / 8;
            int frameBytes = bytesPerSample * _channels;
            int frames = e.BytesRecorded / frameBytes;

            lock (_lock)
            {
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < _channels; c++)
                    {
                        int offset = f * frameBytes + c * bytesPerSample;
                        sum += _bits == 32
                            ? BitConverter.ToSingle(e.Buffer, offset)
                            : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                    }
                    _ring[_ringPos] = sum / _channels;
                    _ringPos = (_ringPos + 1) % FftSize;
                }
            }
        }

        // Вызывается ~30 раз в секунду, возвращает 16 значений от 0 до 1
        public float[] GetLevels()
        {
            // Если звука нет, Windows вообще перестаёт присылать данные
            bool silent = Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) > 250;

            if (!silent)
            {
                lock (_lock)
                {
                    for (int i = 0; i < FftSize; i++)
                    {
                        _re[i] = _ring[(_ringPos + i) % FftSize] * _window[i];
                        _im[i] = 0;
                    }
                }
                Fft(_re, _im);

                for (int b = 0; b < Bands; b++)
                {
                    double peak = 0;
                    for (int k = _bandStart[b]; k <= _bandEnd[b]; k++)
                    {
                        double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                        if (mag > peak) peak = mag;
                    }

                    double relative = peak / (FftSize / 4.0);
                    // Высокие частоты в музыке тише, поэтому слегка подтягиваем их.
                    // Числа 65 и 50 задают чувствительность: см. подсказку после кода.
                    double db = 20 * Math.Log10(relative + 1e-9) + b * 0.9;
                    _target[b] = Math.Clamp((db + 65) / 50, 0, 1);
                }
            }
            else
            {
                Array.Clear(_target);
            }

            var result = new float[Bands];
            for (int b = 0; b < Bands; b++)
            {
                double t = _target[b];
                // Вверх быстро, вниз плавно
                _smooth[b] = t > _smooth[b]
                    ? _smooth[b] + (t - _smooth[b]) * 0.7
                    : Math.Max(t, _smooth[b] - 0.06);
                result[b] = (float)_smooth[b];
            }
            return result;
        }

        // Быстрое преобразование Фурье
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                double wlenRe = Math.Cos(angle), wlenIm = Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    double wRe = 1, wIm = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int u = i + k, v = i + k + len / 2;
                        double tRe = re[v] * wRe - im[v] * wIm;
                        double tIm = re[v] * wIm + im[v] * wRe;
                        re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                        re[u] += tRe; im[u] += tIm;
                        double nextRe = wRe * wlenRe - wIm * wlenIm;
                        wIm = wRe * wlenIm + wIm * wlenRe;
                        wRe = nextRe;
                    }
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _capture.DataAvailable -= OnData;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch { }
        }
    }
}