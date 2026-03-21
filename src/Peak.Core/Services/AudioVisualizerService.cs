using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Peak.Core.Services;

public class AudioVisualizerService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly float[] _fftBuffer = new float[2048];
    private readonly Complex[] _fftComplex = new Complex[2048];
    private int _fftPos;
    private readonly object _lock = new();
    private bool _isRunning;

    public const int BarCount = 5;
    private readonly float[] _barLevels = new float[BarCount];
    private readonly float[] _smoothed = new float[BarCount];

    public event Action<float[]>? LevelsUpdated;

    /// <summary>Amplification factor for FFT output. Higher = more sensitive. Default 40.</summary>
    public float Amplification { get; set; } = 40f;

    public static List<(string Id, string Name)> GetAudioDevices()
    {
        var result = new List<(string Id, string Name)>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
                result.Add((dev.ID, dev.FriendlyName));
        }
        catch { }
        return result;
    }

    public void Start(string? deviceId = null)
    {
        if (_isRunning) return;

        try
        {
            _fftPos = 0;
            Array.Clear(_smoothed);
            Array.Clear(_barLevels);

            var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;

            // Try to find the requested device by ID
            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    device = enumerator.GetDevice(deviceId);
                    if (device.State != DeviceState.Active)
                        device = null;
                }
                catch { device = null; }
            }

            // Fallback to default
            if (device == null)
            {
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                catch { }
            }

            // Fallback to any active device
            if (device == null)
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (devices.Count > 0)
                    device = devices[0];
            }

            if (device == null) return;

            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
            _isRunning = true;
        }
        catch
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch { }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || !_isRunning || _capture == null) return;

        var format = _capture.WaveFormat;
        int channels = format.Channels;
        int floatCount = e.BytesRecorded / 4; // always 32-bit float for WASAPI

        for (int i = 0; i < floatCount; i += channels)
        {
            int bytePos = i * 4;
            if (bytePos + 4 > e.BytesRecorded) break;

            float sample = BitConverter.ToSingle(e.Buffer, bytePos);

            if (channels >= 2 && bytePos + 8 <= e.BytesRecorded)
            {
                float right = BitConverter.ToSingle(e.Buffer, bytePos + 4);
                sample = (sample + right) * 0.5f;
            }

            lock (_lock)
            {
                _fftBuffer[_fftPos] = sample;
                _fftPos++;

                if (_fftPos >= _fftBuffer.Length)
                {
                    _fftPos = 0;
                    ProcessFft();
                }
            }
        }
    }

    private void ProcessFft()
    {
        // Check if there's actual audio (not silence)
        float rms = 0;
        for (int i = 0; i < _fftBuffer.Length; i++)
            rms += _fftBuffer[i] * _fftBuffer[i];
        rms = MathF.Sqrt(rms / _fftBuffer.Length);

        // If pure silence, set bars to minimum
        if (rms < 0.0001f)
        {
            for (int b = 0; b < BarCount; b++)
            {
                _smoothed[b] *= 0.85f;
                _barLevels[b] = _smoothed[b];
            }
            LevelsUpdated?.Invoke(_barLevels);
            return;
        }

        // Apply Hanning window
        for (int i = 0; i < _fftBuffer.Length; i++)
        {
            float window = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / _fftBuffer.Length));
            _fftComplex[i].X = _fftBuffer[i] * window;
            _fftComplex[i].Y = 0;
        }

        // FFT — log2(2048) = 11
        FastFourierTransform.FFT(true, 11, _fftComplex);

        // Logarithmic frequency bands
        int[] bandEdges = [1, 4, 10, 24, 60, 170];

        for (int b = 0; b < BarCount; b++)
        {
            float maxMag = 0;
            for (int i = bandEdges[b]; i < bandEdges[b + 1] && i < _fftComplex.Length / 2; i++)
            {
                float mag = MathF.Sqrt(_fftComplex[i].X * _fftComplex[i].X +
                                        _fftComplex[i].Y * _fftComplex[i].Y);
                if (mag > maxMag) maxMag = mag;
            }

            float level = Math.Clamp(maxMag * Amplification, 0f, 1f);

            if (level > _smoothed[b])
                _smoothed[b] = level;
            else
                _smoothed[b] = _smoothed[b] * 0.88f + level * 0.12f;

            _barLevels[b] = _smoothed[b];
        }

        LevelsUpdated?.Invoke(_barLevels);
    }

    public void Dispose()
    {
        Stop();
    }
}
