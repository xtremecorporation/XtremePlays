using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace XtremePlays.App.Services;

public sealed class AudioMixingRouter : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public static WaveFormat MixFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private readonly FloatAudioBuffer _voiceBuffer = new(SampleRate * Channels);
    private readonly FloatAudioBuffer _musicBuffer = new(SampleRate * Channels);
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _microphoneDevice;
    private MMDevice? _outputDevice;
    private WasapiRecorder? _microphoneRecorder;
    private WasapiRecorder? _browserAudioRecorder;
    private WasapiPlayer? _player;
    private MixingWaveProvider? _mixingWaveProvider;

    public bool IsRunning { get; private set; }
    public float VoiceLevel => _mixingWaveProvider?.VoiceLevel ?? 0;
    public float MusicLevel => _mixingWaveProvider?.MusicLevel ?? 0;
    public float OutputLevel => _mixingWaveProvider?.OutputLevel ?? 0;

    [SupportedOSPlatform("windows10.0.19041")]
    public async Task StartAsync(
        string microphoneId,
        string outputId,
        uint browserProcessId,
        float voiceGain,
        float musicGain,
        float masterGain,
        bool voiceMuted,
        bool musicMuted)
    {
        Stop();

        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _microphoneDevice = _deviceEnumerator.GetDevice(microphoneId);
            _outputDevice = _deviceEnumerator.GetDevice(outputId);
            if (_microphoneDevice.DataFlow != DataFlow.Capture)
            {
                throw new InvalidOperationException("Choose a Capture microphone for the voice input.");
            }

            if (_outputDevice.DataFlow != DataFlow.Render)
            {
                throw new InvalidOperationException("Choose a virtual playback endpoint for the mixed output.");
            }

            _voiceBuffer.Clear();
            _musicBuffer.Clear();
            _microphoneRecorder = new WasapiRecorderBuilder()
                .WithDevice(_microphoneDevice)
                .WithFormat(MixFormat)
                .WithBufferLength(50)
                .Build();

            _browserAudioRecorder = await new WasapiRecorderBuilder()
                .WithFormat(MixFormat)
                .WithBufferLength(50)
                .WithProcessLoopback(browserProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .BuildAsync();

            _mixingWaveProvider = new MixingWaveProvider(_voiceBuffer, _musicBuffer);
            _mixingWaveProvider.SetGains(voiceGain, musicGain, masterGain);
            _mixingWaveProvider.SetMutes(voiceMuted, musicMuted);
            _player = new WasapiPlayerBuilder()
                .WithDevice(_outputDevice)
                .WithSharedMode()
                .WithLatency(100)
                .Build();
            _player.Init(_mixingWaveProvider);

            _microphoneRecorder.DataAvailable += (buffer, _, _, _) =>
                _voiceBuffer.Write(MemoryMarshal.Cast<byte, float>(buffer));
            _browserAudioRecorder.DataAvailable += (buffer, _, _, _) =>
                _musicBuffer.Write(MemoryMarshal.Cast<byte, float>(buffer));

            _player.Play();
            _microphoneRecorder.StartRecording();
            _browserAudioRecorder.StartRecording();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void SetGains(float voiceGain, float musicGain, float masterGain) =>
        _mixingWaveProvider?.SetGains(voiceGain, musicGain, masterGain);

    public void SetMutes(bool voiceMuted, bool musicMuted) =>
        _mixingWaveProvider?.SetMutes(voiceMuted, musicMuted);

    public void Stop()
    {
        IsRunning = false;
        try { _microphoneRecorder?.StopRecording(); } catch { }
        try { _browserAudioRecorder?.StopRecording(); } catch { }
        try { _player?.Stop(); } catch { }

        TryDispose(_microphoneRecorder);
        TryDispose(_browserAudioRecorder);
        TryDispose(_player);
        TryDispose(_microphoneDevice);
        TryDispose(_outputDevice);
        TryDispose(_deviceEnumerator);

        _microphoneRecorder = null;
        _browserAudioRecorder = null;
        _player = null;
        _microphoneDevice = null;
        _outputDevice = null;
        _deviceEnumerator = null;
        _mixingWaveProvider = null;
        _voiceBuffer.Clear();
        _musicBuffer.Clear();
    }

    public void Dispose() => Stop();

    private static void TryDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); } catch { }
    }

    private sealed class MixingWaveProvider(FloatAudioBuffer voiceBuffer, FloatAudioBuffer musicBuffer) : IWaveProvider
    {
        private float _voiceGain = 1;
        private float _musicGain = 1;
        private float _masterGain = 1;
        private int _voiceMuted;
        private int _musicMuted;
        private float _voiceLevel;
        private float _musicLevel;
        private float _outputLevel;

        public WaveFormat WaveFormat => MixFormat;
        public float VoiceLevel => Volatile.Read(ref _voiceLevel);
        public float MusicLevel => Volatile.Read(ref _musicLevel);
        public float OutputLevel => Volatile.Read(ref _outputLevel);

        public void SetGains(float voiceGain, float musicGain, float masterGain)
        {
            Volatile.Write(ref _voiceGain, ClampGain(voiceGain));
            Volatile.Write(ref _musicGain, ClampGain(musicGain));
            Volatile.Write(ref _masterGain, ClampGain(masterGain));
        }

        public void SetMutes(bool voiceMuted, bool musicMuted)
        {
            Volatile.Write(ref _voiceMuted, voiceMuted ? 1 : 0);
            Volatile.Write(ref _musicMuted, musicMuted ? 1 : 0);
        }

        public int Read(Span<byte> destination)
        {
            int sampleBytes = destination.Length - destination.Length % sizeof(float);
            Span<float> samples = MemoryMarshal.Cast<byte, float>(destination[..sampleBytes]);
            samples.Clear();

            float voiceGain = Volatile.Read(ref _voiceMuted) == 1 ? 0 : Volatile.Read(ref _voiceGain);
            float musicGain = Volatile.Read(ref _musicMuted) == 1 ? 0 : Volatile.Read(ref _musicGain);
            float masterGain = Volatile.Read(ref _masterGain);
            float voicePeak = voiceBuffer.MixInto(samples, voiceGain);
            float musicPeak = musicBuffer.MixInto(samples, musicGain);
            float outputPeak = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                float mixed = samples[i] * masterGain;
                mixed = Math.Clamp(mixed, -1f, 1f);
                samples[i] = mixed;
                outputPeak = Math.Max(outputPeak, Math.Abs(mixed));
            }

            Volatile.Write(ref _voiceLevel, voicePeak);
            Volatile.Write(ref _musicLevel, musicPeak);
            Volatile.Write(ref _outputLevel, outputPeak);
            destination[sampleBytes..].Clear();
            return destination.Length;
        }

        private static float ClampGain(float gain) => Math.Clamp(gain, 0f, 2f);
    }

    private sealed class FloatAudioBuffer(int capacitySamples)
    {
        private readonly object _gate = new();
        private readonly float[] _samples = new float[capacitySamples];
        private int _readIndex;
        private int _sampleCount;

        public void Write(ReadOnlySpan<float> source)
        {
            int count = source.Length - source.Length % Channels;
            if (count == 0) return;
            ReadOnlySpan<float> alignedSource = source[..count];
            lock (_gate)
            {
                if (count >= _samples.Length)
                {
                    alignedSource = alignedSource[^_samples.Length..];
                    count = _samples.Length;
                    _readIndex = 0;
                    _sampleCount = 0;
                }

                int overflow = Math.Max(0, _sampleCount + count - _samples.Length);
                overflow += overflow % Channels;
                _readIndex = (_readIndex + overflow) % _samples.Length;
                _sampleCount -= overflow;

                int writeIndex = (_readIndex + _sampleCount) % _samples.Length;
                int firstPart = Math.Min(count, _samples.Length - writeIndex);
                alignedSource[..firstPart].CopyTo(_samples.AsSpan(writeIndex, firstPart));
                int remaining = count - firstPart;
                if (remaining > 0)
                {
                    alignedSource[firstPart..].CopyTo(_samples.AsSpan(0, remaining));
                }

                _sampleCount += count;
            }
        }

        public float MixInto(Span<float> destination, float gain)
        {
            float peak = 0;
            lock (_gate)
            {
                int count = Math.Min(destination.Length - destination.Length % Channels, _sampleCount);
                for (int i = 0; i < count; i++)
                {
                    float sample = _samples[_readIndex];
                    float amplified = sample * gain;
                    destination[i] += amplified;
                    peak = Math.Max(peak, Math.Abs(amplified));
                    _readIndex = (_readIndex + 1) % _samples.Length;
                }

                _sampleCount -= count;
            }

            return peak;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _readIndex = 0;
                _sampleCount = 0;
            }
        }
    }
}
