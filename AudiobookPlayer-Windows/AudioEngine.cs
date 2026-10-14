using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudiobookPlayer;

/// Plays a file at 0.5x–2.5x with pitch held. WinRT MediaPlayer.PlaybackRate changes pitch
/// and silently rejects rates the file will not play, so speed goes through a time stretcher
/// and the tempo actually in use is the only one reported.
internal sealed class AudioEngine : IDisposable
{
    public event Action? Ended;
    public event Action<string>? Failed;

    private readonly object gate = new();
    private readonly WsolaTempo wsola = new();
    private readonly PullProvider pull;
    private IWavePlayer? output;
    private WaveFormat? activeFormat;
    private MediaFoundationReader? reader;
    private ISampleProvider? samples;
    private float[] scratch = Array.Empty<float>();
    private bool suppressStop;
    private bool hitEnd;
    private bool inputExhausted;
    private bool disposed;
    private string? readError;
    private double pendingVolume = 1;
    private double segmentStart;
    private long framesDelivered;
    private int sampleRate = 44100;
    private int channels = 2;

    public double Tempo { get; private set; } = 1;
    public double DurationSeconds { get; private set; }
    public bool HasFile => reader != null;
    public string? LastError { get; private set; }

    public double OutputVolume
    {
        get => pendingVolume;
        set
        {
            pendingVolume = Math.Clamp(value, 0, 1);
            if (output != null)
            {
                try { output.Volume = (float)pendingVolume; } catch { /* device already gone */ }
            }
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (gate)
                return PositionUnlocked();
        }
    }

    public AudioEngine()
    {
        pull = new PullProvider(this);
    }

    public static string ShortError(Exception ex)
    {
        string msg = (ex.Message ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        if (msg.Length > 180) msg = msg[..177] + "...";
        return string.IsNullOrWhiteSpace(msg) ? "Couldn't play that file." : msg;
    }

    /// Opens the file. Safe to call off the UI thread. Caller disposes the result if <see cref="Use"/> is not called.
    public DecodedAudio Decode(string path)
    {
        MediaFoundationReader decoded;
        try
        {
            decoded = new MediaFoundationReader(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ShortError(ex), ex);
        }

        if (decoded.WaveFormat.SampleRate <= 0 || decoded.WaveFormat.Channels <= 0)
        {
            decoded.Dispose();
            throw new InvalidOperationException("This file has no playable audio.");
        }

        try
        {
            var samplesProvider = ToSamples(decoded);
            return new DecodedAudio(decoded, samplesProvider);
        }
        catch (Exception ex)
        {
            decoded.Dispose();
            throw new InvalidOperationException(ShortError(ex), ex);
        }
    }

    private static ISampleProvider ToSamples(IWaveProvider wave)
    {
        var format = wave.WaveFormat;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return new WaveToSampleProvider(wave);
        return format.BitsPerSample switch
        {
            8 => new Pcm8BitToSampleProvider(wave),
            16 => new Pcm16BitToSampleProvider(wave),
            24 => new Pcm24BitToSampleProvider(wave),
            32 when format.Encoding == WaveFormatEncoding.Pcm => new Pcm32BitToSampleProvider(wave),
            _ => throw new InvalidOperationException($"Unsupported audio format ({format.BitsPerSample}-bit {format.Encoding})."),
        };
    }

    /// Takes ownership of <paramref name="source"/> and cues it. Does not start playback.
    public string? Use(DecodedAudio source, double seekSeconds)
    {
        try
        {
            if (seekSeconds > 1 && source.Reader.TotalTime.TotalSeconds > 1)
            {
                double dur = source.Reader.TotalTime.TotalSeconds;
                source.Reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(seekSeconds, Math.Max(0, dur - 0.2)));
            }
        }
        catch (Exception ex)
        {
            source.Dispose();
            LastError = ShortError(ex);
            return LastError;
        }

        try
        {
            Attach(source);
            return null;
        }
        catch (Exception ex)
        {
            LastError = ShortError(ex);
            return LastError;
        }
    }

    /// False when <paramref name="value"/> is outside 0.5x–2.5x. The previous tempo keeps playing.
    public bool TrySetTempo(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.499 || value > 2.501)
            return false;

        double previous;
        double at;
        bool reopen;
        bool wasPlaying = false;
        try { wasPlaying = output?.PlaybackState == PlaybackState.Playing; } catch { /* no device yet */ }
        lock (gate)
        {
            previous = Tempo;
            reopen = reader != null;
            at = reopen ? PositionUnlocked() : 0;
            Tempo = value;
        }
        if (!reopen) return true;
        if (Seek(at)) return Math.Abs(Tempo - value) < 0.001;

        lock (gate)
        {
            Tempo = previous;
            if (reader != null)
                wsola.Reset(sampleRate, channels, previous);
        }
        if (wasPlaying) Play();
        return false;
    }

    public bool Play()
    {
        if (output == null || reader == null)
        {
            LastError = "Nothing is loaded.";
            return false;
        }
        try
        {
            output.Play();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ShortError(ex);
            return false;
        }
    }

    public void Pause()
    {
        try { output?.Pause(); } catch { /* already stopped */ }
    }

    public bool Seek(double seconds)
    {
        bool wasPlaying = false;
        try { wasPlaying = output?.PlaybackState == PlaybackState.Playing; } catch { /* no device yet */ }

        suppressStop = true;
        try { output?.Stop(); }
        catch { /* stop is best-effort before a seek */ }
        suppressStop = false;

        bool ok;
        lock (gate)
            ok = SeekUnlocked(seconds);
        if (!ok) return false;

        if (wasPlaying)
        {
            try { output?.Play(); }
            catch (Exception ex)
            {
                LastError = ShortError(ex);
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        suppressStop = true;
        try { output?.Stop(); } catch { /* shutting down */ }
        if (output != null) output.PlaybackStopped -= OnStopped;
        try { output?.Dispose(); } catch { /* shutting down */ }
        output = null;
        lock (gate)
        {
            reader?.Dispose();
            reader = null;
            samples = null;
        }
    }

    private void Attach(DecodedAudio source)
    {
        suppressStop = true;
        try { output?.Stop(); } catch { /* replacing the current file */ }
        suppressStop = false;

        var format = source.Samples.WaveFormat;
        lock (gate)
        {
            reader?.Dispose();
            reader = source.Reader;
            samples = source.Samples;
            sampleRate = format.SampleRate;
            channels = format.Channels;
            scratch = new float[Math.Max(channels * 4096, channels)];
            inputExhausted = false;
            hitEnd = false;
            readError = null;
            wsola.Reset(sampleRate, channels, Tempo);
            segmentStart = reader.CurrentTime.TotalSeconds;
            if (double.IsNaN(segmentStart) || segmentStart < 0) segmentStart = 0;
            framesDelivered = 0;
            double dur = reader.TotalTime.TotalSeconds;
            DurationSeconds = double.IsFinite(dur) && dur > 0 ? dur : 0;
        }

        bool newDevice = output == null || activeFormat == null || !SameFormat(activeFormat, format);
        try
        {
            if (newDevice)
            {
                if (output != null)
                {
                    output.PlaybackStopped -= OnStopped;
                    try { output.Dispose(); } catch { /* replaced by a device that matches this file */ }
                    output = null;
                }
                activeFormat = format;
                output = OpenDevice(pull);
            }
            else if (output != null)
            {
                output.Volume = (float)pendingVolume;
            }
        }
        catch
        {
            lock (gate)
            {
                reader?.Dispose();
                reader = null;
                samples = null;
            }
            throw;
        }
    }

    private IWavePlayer OpenDevice(ISampleProvider provider)
    {
        var wave = new SampleToWaveProvider(provider);
        WasapiOut? wasapi = null;
        try
        {
            wasapi = new WasapiOut(AudioClientShareMode.Shared, 120);
            wasapi.Init(wave);
            wasapi.PlaybackStopped += OnStopped;
            wasapi.Volume = (float)pendingVolume;
            return wasapi;
        }
        catch (Exception)
        {
            try { wasapi?.Dispose(); } catch { /* falling back to waveOut */ }
            var waveOut = new WaveOutEvent { DesiredLatency = 120 };
            waveOut.Init(wave);
            waveOut.PlaybackStopped += OnStopped;
            waveOut.Volume = (float)pendingVolume;
            return waveOut;
        }
    }

    private bool SeekUnlocked(double seconds)
    {
        if (reader == null) return false;
        try
        {
            double dur = DurationSeconds;
            if (dur > 0) seconds = Math.Clamp(seconds, 0, Math.Max(0, dur - 0.01));
            else seconds = Math.Max(0, seconds);
            reader.CurrentTime = TimeSpan.FromSeconds(seconds);
            double actual = reader.CurrentTime.TotalSeconds;
            if (double.IsNaN(actual) || actual < 0) actual = seconds;
            wsola.Reset(sampleRate, channels, Tempo);
            inputExhausted = false;
            hitEnd = false;
            readError = null;
            segmentStart = actual;
            framesDelivered = 0;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ShortError(ex);
            return false;
        }
    }

    private double PositionUnlocked()
    {
        if (reader == null || sampleRate <= 0) return 0;
        double seconds = segmentStart + (framesDelivered * Tempo) / sampleRate;
        if (DurationSeconds > 0) seconds = Math.Min(seconds, DurationSeconds);
        return Math.Max(0, seconds);
    }

    private int ReadSamples(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            if (disposed || reader == null || samples == null || channels <= 0)
                return 0;

            int want = count / channels;
            int got = 0;
            int spins = 0;
            while (got < want && spins++ < 12)
            {
                Pump();
                int n = wsola.Read(buffer, offset + got * channels, want - got);
                if (n <= 0) break;
                got += n;
            }

            if (got == 0)
            {
                if (!inputExhausted && readError == null)
                    readError = "Playback stalled before the end of the file.";
                hitEnd = true;
                return 0;
            }

            framesDelivered += got;
            return got * channels;
        }
    }

    private void Pump()
    {
        if (samples == null || inputExhausted) return;
        int read;
        try { read = samples.Read(scratch, 0, scratch.Length); }
        catch (Exception ex)
        {
            readError = ShortError(ex);
            inputExhausted = true;
            wsola.Complete();
            return;
        }
        if (read <= 0)
        {
            inputExhausted = true;
            wsola.Complete();
            return;
        }
        wsola.Add(scratch, 0, read);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (suppressStop || disposed) return;
        string? readFailure;
        bool end;
        lock (gate)
        {
            readFailure = readError;
            end = hitEnd;
            readError = null;
        }
        if (e.Exception != null)
        {
            LastError = ShortError(e.Exception);
            Failed?.Invoke(LastError);
            return;
        }
        if (readFailure != null)
        {
            LastError = readFailure;
            Failed?.Invoke(readFailure);
            return;
        }
        if (end) Ended?.Invoke();
    }

    private static bool SameFormat(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.Encoding == b.Encoding && a.BitsPerSample == b.BitsPerSample;

    private sealed class PullProvider : ISampleProvider
    {
        private readonly AudioEngine owner;
        public PullProvider(AudioEngine owner) => this.owner = owner;
        public WaveFormat WaveFormat => owner.activeFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public int Read(float[] buffer, int offset, int count) => owner.ReadSamples(buffer, offset, count);
    }
}

internal sealed class DecodedAudio : IDisposable
{
    public MediaFoundationReader Reader { get; }
    public ISampleProvider Samples { get; }

    public DecodedAudio(MediaFoundationReader reader, ISampleProvider samples)
    {
        Reader = reader;
        Samples = samples;
    }

    public void Dispose() => Reader.Dispose();
}
