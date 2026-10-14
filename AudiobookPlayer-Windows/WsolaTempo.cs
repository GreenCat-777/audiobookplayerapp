using System;

namespace AudiobookPlayer;

/// Overlap-add tempo change that keeps the original pitch (WSOLA).
/// Tempo 1 copies samples through. Tempo 2 plays twice as fast at the same pitch.
internal sealed class WsolaTempo
{
    private int sampleRate = 44100;
    private int channels = 1;
    private int window = 1024;
    private int hop = 512;
    private int radius = 256;
    private float[] hann = Array.Empty<float>();

    private double tempo = 1;
    private float[] input = Array.Empty<float>();
    private int inputFrames;
    private int baseFrame;
    private bool inputEnded;

    private float[] overlap = Array.Empty<float>();
    private float[]? prevMono;
    private int nominal;
    private double nominalExact;
    private bool started;
    private bool flushedTail;

    private float[] pending = Array.Empty<float>();
    private int pendingFrame;
    private int pendingCount;

    public double Tempo => tempo;
    public int SampleRate => sampleRate;
    public int Channels => channels;

    public void Reset(int newSampleRate, int newChannels, double newTempo)
    {
        sampleRate = Math.Max(8000, newSampleRate);
        channels = Math.Max(1, newChannels);
        tempo = newTempo <= 0 ? 1 : newTempo;
        window = Math.Max(32, (sampleRate * 40 / 1000) / 2 * 2);
        hop = window / 2;
        radius = Math.Max(8, sampleRate / 100);
        hann = new float[window];
        for (int i = 0; i < window; i++)
            hann[i] = 0.5f * (1f - MathF.Cos((2f * MathF.PI * i) / window));

        input = Array.Empty<float>();
        inputFrames = 0;
        baseFrame = 0;
        inputEnded = false;
        overlap = new float[hop * channels];
        prevMono = null;
        nominal = 0;
        nominalExact = 0;
        started = false;
        flushedTail = false;
        pending = Array.Empty<float>();
        pendingFrame = 0;
        pendingCount = 0;
    }

    public void Add(float[] interleaved, int offset, int floatCount)
    {
        if (floatCount <= 0) return;
        int frames = floatCount / channels;
        EnsureInput(inputFrames + frames);
        Buffer.BlockCopy(interleaved, offset * sizeof(float), input, inputFrames * channels * sizeof(float), frames * channels * sizeof(float));
        inputFrames += frames;
    }

    public void Complete() => inputEnded = true;

    /// Writes up to <paramref name="maxFrames"/> interleaved frames at <paramref name="destFloatOffset"/>.
    public int Read(float[] dest, int destFloatOffset, int maxFrames)
    {
        if (maxFrames <= 0) return 0;
        if (Math.Abs(tempo - 1.0) < 0.0005)
            return ReadPassthrough(dest, destFloatOffset, maxFrames);

        int guard = 0;
        while (pendingCount < maxFrames && guard++ < 64)
        {
            if (!Synthesize()) break;
        }

        int n = Math.Min(maxFrames, pendingCount);
        if (n <= 0) return 0;
        Buffer.BlockCopy(pending, pendingFrame * channels * sizeof(float), dest, destFloatOffset * sizeof(float), n * channels * sizeof(float));
        pendingFrame += n;
        pendingCount -= n;
        if (pendingCount == 0) pendingFrame = 0;
        else if (pendingFrame > 8192)
            CompactPending();
        return n;
    }

    private int ReadPassthrough(float[] dest, int destFloatOffset, int maxFrames)
    {
        int have = inputFrames;
        int n = Math.Min(maxFrames, have);
        if (n <= 0) return 0;
        Buffer.BlockCopy(input, 0, dest, destFloatOffset * sizeof(float), n * channels * sizeof(float));
        int remain = inputFrames - n;
        if (remain > 0)
            Buffer.BlockCopy(input, n * channels * sizeof(float), input, 0, remain * channels * sizeof(float));
        inputFrames = remain;
        return n;
    }

    private bool Synthesize()
    {
        int end = baseFrame + inputFrames;
        bool full = end >= nominal + window + radius && nominal - radius >= baseFrame;
        bool padded = inputEnded && end > nominal && nominal < end + window;
        if (!full && !padded)
        {
            if (inputEnded && started && !flushedTail)
            {
                flushedTail = true;
                Enqueue(overlap, 0, hop);
                Array.Clear(overlap, 0, overlap.Length);
                return pendingCount > 0;
            }
            return false;
        }

        int best = 0;
        if (prevMono != null && full)
        {
            float bestScore = float.NegativeInfinity;
            int step = Math.Max(1, radius / 32);
            for (int k = -radius; k <= radius; k += step)
            {
                int start = nominal + k;
                if (start < baseFrame || start + hop > end) continue;
                float score = Correlate(start);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = k;
                }
            }
        }

        int frameStart = nominal + best;
        if (frameStart < baseFrame) frameStart = baseFrame;
        var frame = new float[window * channels];
        CopyFrames(frameStart, frame, window);

        var monoTail = new float[hop];
        for (int i = 0; i < hop; i++)
            monoTail[i] = MonoAt(frame, hop + i);
        prevMono = monoTail;

        var mixed = new float[hop * channels];
        for (int i = 0; i < hop; i++)
        {
            float w = hann[i];
            int o = i * channels;
            for (int c = 0; c < channels; c++)
                mixed[o + c] = overlap[o + c] + frame[o + c] * w;
        }
        for (int i = 0; i < hop; i++)
        {
            float w = hann[hop + i];
            int o = i * channels;
            int f = (hop + i) * channels;
            for (int c = 0; c < channels; c++)
                overlap[o + c] = frame[f + c] * w;
        }

        Enqueue(mixed, 0, hop);
        // Keep the long-run tempo exact; each hop's integer jump absorbs the fractional remainder.
        nominalExact += hop * tempo;
        int advanced = (int)Math.Round(nominalExact) - nominal;
        if (advanced < 1) advanced = 1;
        nominal += advanced;
        started = true;
        DiscardOldInput();
        return true;
    }

    private float Correlate(int frameStart)
    {
        double dot = 0, a = 0, b = 0;
        var prev = prevMono;
        if (prev == null) return 0;
        for (int i = 0; i < hop; i++)
        {
            float m = MonoSample(frameStart + i);
            float p = prev[i];
            dot += m * p;
            a += m * m;
            b += p * p;
        }
        if (a < 1e-8 || b < 1e-8) return 0;
        return (float)(dot / Math.Sqrt(a * b));
    }

    private float MonoSample(int frame)
    {
        int rel = frame - baseFrame;
        if (rel < 0 || rel >= inputFrames) return 0;
        int o = rel * channels;
        float s = 0;
        for (int c = 0; c < channels; c++) s += input[o + c];
        return s / channels;
    }

    private float MonoAt(float[] interleavedFrame, int index)
    {
        int o = index * channels;
        float s = 0;
        for (int c = 0; c < channels; c++) s += interleavedFrame[o + c];
        return s / channels;
    }

    private void CopyFrames(int absoluteStart, float[] dest, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int rel = absoluteStart + i - baseFrame;
            int o = i * channels;
            if (rel < 0 || rel >= inputFrames)
            {
                for (int c = 0; c < channels; c++) dest[o + c] = 0;
                continue;
            }
            int s = rel * channels;
            for (int c = 0; c < channels; c++) dest[o + c] = input[s + c];
        }
    }

    private void Enqueue(float[] interleaved, int frameOffset, int frames)
    {
        int floats = frames * channels;
        int start = frameOffset * channels;
        int need = pendingFrame + pendingCount + frames;
        if (pending.Length < need * channels)
        {
            var grown = new float[Math.Max(need * channels, Math.Max(4096, pending.Length * 2))];
            if (pendingCount > 0)
                Buffer.BlockCopy(pending, pendingFrame * channels * sizeof(float), grown, 0, pendingCount * channels * sizeof(float));
            pending = grown;
            pendingFrame = 0;
        }
        else if (pendingFrame + pendingCount + frames > pending.Length / channels)
        {
            CompactPending();
        }
        Buffer.BlockCopy(interleaved, start * sizeof(float), pending, (pendingFrame + pendingCount) * channels * sizeof(float), floats * sizeof(float));
        pendingCount += frames;
    }

    private void CompactPending()
    {
        if (pendingFrame == 0) return;
        Buffer.BlockCopy(pending, pendingFrame * channels * sizeof(float), pending, 0, pendingCount * channels * sizeof(float));
        pendingFrame = 0;
    }

    private void DiscardOldInput()
    {
        int keep = nominal - radius - hop;
        int drop = keep - baseFrame;
        if (drop <= 0 || drop >= inputFrames) return;
        int remain = inputFrames - drop;
        Buffer.BlockCopy(input, drop * channels * sizeof(float), input, 0, remain * channels * sizeof(float));
        inputFrames = remain;
        baseFrame += drop;
    }

    private void EnsureInput(int frames)
    {
        int floats = frames * channels;
        if (input.Length >= floats) return;
        int size = Math.Max(floats, Math.Max(input.Length * 2, sampleRate * channels));
        var grown = new float[size];
        if (inputFrames > 0)
            Buffer.BlockCopy(input, 0, grown, 0, inputFrames * channels * sizeof(float));
        input = grown;
    }
}
