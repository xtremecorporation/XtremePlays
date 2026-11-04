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

    private readonly List<FloatAudioBuffer> _voiceBuffers = [];
    private readonly List<FloatAudioBuffer> _musicBuffers = [];
    private readonly List<MixingWaveProvider> _mixers = [];
    private readonly List<MMDevice> _microphoneDevices = [];
    private readonly List<MMDevice> _outputDevices = [];
    private readonly List<WasapiRecorder> _microphoneRecorders = [];
    private readonly List<WasapiPlayer> _players = [];
    private MMDeviceEnumerator? _deviceEnumerator;
    private WasapiRecorder? _browserAudioRecorder;

    public bool IsRunning { get; private set; }
    public float VoiceLevel => _mixers.FirstOrDefault()?.VoiceLevel ?? 0;
    public float MusicLevel => _mixers.FirstOrDefault()?.MusicLevel ?? 0;
    public float OutputLevel => _mixers.FirstOrDefault()?.OutputLevel ?? 0;

    [SupportedOSPlatform("windows10.0.19041")]
    public async Task StartAsync(
        IReadOnlyCollection<string> microphoneIds,
        string callOutputId,
        IReadOnlyCollection<string> monitorOutputIds,
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
            var selectedMicrophoneIds = microphoneIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (selectedMicrophoneIds.Length == 0)
                throw new InvalidOperationException("Choose at least one active microphone for the voice input.");

            var selectedOutputIds = new[] { callOutputId }
                .Concat(monitorOutputIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (selectedOutputIds.Length == 0)
                throw new InvalidOperationException("Choose an output device for the mixed audio.");

            _deviceEnumerator = new MMDeviceEnumerator();
            foreach (var microphoneId in selectedMicrophoneIds)
            {
                var microphone = _deviceEnumerator.GetDevice(microphoneId);
                if (microphone.DataFlow != DataFlow.Capture)
                {
                    microphone.Dispose();
                    throw new InvalidOperationException("Each selected voice input must be a Capture microphone.");
                }
                _microphoneDevices.Add(microphone);
            }

            foreach (var outputId in selectedOutputIds)
            {
                var output = _deviceEnumerator.GetDevice(outputId);
                if (output.DataFlow != DataFlow.Render)
                {
                    output.Dispose();
                    throw new InvalidOperationException("Each selected audio output must be a Render device.");
                }
                _outputDevices.Add(output);
            }

            foreach (var _ in _outputDevices)
            {
                _voiceBuffers.Add(new FloatAudioBuffer(SampleRate * Channels));
                _musicBuffers.Add(new FloatAudioBuffer(SampleRate * Channels));
            }

            foreach (var microphone in _microphoneDevices)
            {
                var recorder = new WasapiRecorderBuilder()
                    .WithDevice(microphone)
                    .WithFormat(MixFormat)
                    .WithBufferLength(50)
                    .Build();
                // Voice goes to the virtual call cable only. Local monitor speakers
                // should play the browser music without feeding the microphone back.
                var callVoiceBuffer = _voiceBuffers[0];
                recorder.DataAvailable += (buffer, _, _, _) =>
                {
                    var samples = MemoryMarshal.Cast<byte, float>(buffer);
                    callVoiceBuffer.Write(samples);
                };
                _microphoneRecorders.Add(recorder);
            }

            _browserAudioRecorder = await new WasapiRecorderBuilder()
                .WithFormat(MixFormat)
                .WithBufferLength(50)
                .WithProcessLoopback(browserProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .BuildAsync();

            for (var index = 0; index < _outputDevices.Count; index++)
            {
                var mixer = new MixingWaveProvider(_voiceBuffers[index], _musicBuffers[index]);
                mixer.SetGains(voiceGain, musicGain, masterGain);
                mixer.SetMutes(voiceMuted, musicMuted);
                var player = new WasapiPlayerBuilder()
                    .WithDevice(_outputDevices[index])
                    .WithSharedMode()
                    .WithLatency(100)
                    .Build();
                player.Init(mixer);
                _mixers.Add(mixer);
                _players.Add(player);
            }

            var musicTargets = _musicBuffers.ToArray();
            _browserAudioRecorder.DataAvailable += (buffer, _, _, _) =>
            {
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                foreach (var target in musicTargets) target.Write(samples);
            };

            foreach (var player in _players) player.Play();
            foreach (var recorder in _microphoneRecorders) recorder.StartRecording();
            _browserAudioRecorder.StartRecording();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void SetGains(float voiceGain, float musicGain, float masterGain)
    {
        foreach (var mixer in _mixers) mixer.SetGains(voiceGain, musicGain, masterGain);
    }

    public void SetMutes(bool voiceMuted, bool musicMuted)
    {
        foreach (var mixer in _mixers) mixer.SetMutes(voiceMuted, musicMuted);
    }

    public void Stop()
    {
        IsRunning = false;
        foreach (var recorder in _microphoneRecorders)
            try { recorder.StopRecording(); } catch { }
        try { _browserAudioRecorder?.StopRecording(); } catch { }
        foreach (var player in _players)
            try { player.Stop(); } catch { }

        foreach (var recorder in _microphoneRecorders) TryDispose(recorder);
        TryDispose(_browserAudioRecorder);
        foreach (var player in _players) TryDispose(player);
        foreach (var device in _microphoneDevices) TryDispose(device);
        foreach (var device in _outputDevices) TryDispose(device);
        TryDispose(_deviceEnumerator);

        _microphoneRecorders.Clear();
        _browserAudioRecorder = null;
        _players.Clear();
        _microphoneDevices.Clear();
        _outputDevices.Clear();
        _deviceEnumerator = null;
        _mixers.Clear();
        _voiceBuffers.Clear();
        _musicBuffers.Clear();
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
