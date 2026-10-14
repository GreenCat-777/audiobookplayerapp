using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AudiobookPlayer;

/// Reads chapter markers from .m4b / .m4a files:
///   1. Nero "chpl" atom (moov/udta/chpl), written by ffmpeg, mp4v2, m4b-tool
///   2. QuickTime chapter text track (moov/trak/tref/chap), written by Audible-style tools
public static class Mp4Chapters
{
    private readonly record struct Atom(string Type, int Start, int End);

    public static List<ChapterMark> Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var moov = ReadMoov(fs);
            if (moov == null) return new List<ChapterMark>();

            var chpl = ReadChpl(moov);
            if (chpl.Count > 1) return chpl;

            var qt = ReadTextTrack(moov, fs);
            return qt.Count > 1 ? qt : new List<ChapterMark>();
        }
        catch { return new List<ChapterMark>(); }
    }

    // ── atom plumbing ───────────────────────────────────────────

    private static byte[]? ReadMoov(FileStream fs)
    {
        long len = fs.Length, pos = 0;
        var hdr = new byte[16];
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            fs.ReadExactly(hdr, 0, 8);
            long size = Be32(hdr, 0);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            int hs = 8;
            if (size == 1) { fs.ReadExactly(hdr, 8, 8); size = (long)Be64(hdr, 8); hs = 16; }
            else if (size == 0) size = len - pos;
            if (size < hs) return null;

            if (type == "moov")
            {
                long n = size - hs;
                if (n <= 0 || n > 300_000_000) return null;
                var buf = new byte[n];
                fs.Position = pos + hs;
                fs.ReadExactly(buf, 0, (int)n);
                return buf;
            }
            pos += size;
        }
        return null;
    }

    private static IEnumerable<Atom> Children(byte[] b, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = Be32(b, pos);
            string type = Encoding.ASCII.GetString(b, pos + 4, 4);
            int hs = 8;
            if (size == 1)
            {
                if (pos + 16 > end) yield break;
                size = (long)Be64(b, pos + 8);
                hs = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < hs || pos + size > end) yield break;
            yield return new Atom(type, pos + hs, (int)(pos + size));
            pos += (int)size;
        }
    }

    private static Atom? Find(byte[] b, Atom parent, string type)
    {
        foreach (var a in Children(b, parent.Start, parent.End))
            if (a.Type == type) return a;
        return null;
    }

    private static Atom? Find(byte[] b, Atom? parent, string type) => parent is { } p ? Find(b, p, type) : null;

    // ── Nero chpl ───────────────────────────────────────────────

    private static List<ChapterMark> ReadChpl(byte[] moov)
    {
        var list = new List<ChapterMark>();
        var root = new Atom("moov", 0, moov.Length);
        var chpl = Find(moov, Find(moov, root, "udta"), "chpl");
        if (chpl is not { } c) return list;

        int p = c.Start + 8;                    // version/flags (4) + reserved (4)
        if (p >= c.End) return list;
        int count = moov[p++];
        for (int i = 0; i < count; i++)
        {
            if (p + 9 > c.End) break;
            double start = Be64(moov, p) / 10_000_000.0;   // 100 ns units
            p += 8;
            int sl = moov[p++];
            if (p + sl > c.End) break;
            list.Add(new ChapterMark(start, Encoding.UTF8.GetString(moov, p, sl).Trim()));
            p += sl;
        }
        return list;
    }

    // ── QuickTime chapter text track ────────────────────────────

    private static List<ChapterMark> ReadTextTrack(byte[] b, FileStream fs)
    {
        var traks = Children(b, 0, b.Length).Where(a => a.Type == "trak").ToList();
        var byId = new Dictionary<uint, Atom>();
        var targets = new List<uint>();

        foreach (var t in traks)
        {
            if (Find(b, t, "tkhd") is { } tkhd)
            {
                int ver = b[tkhd.Start];
                int off = tkhd.Start + 4 + (ver == 1 ? 16 : 8);
                if (off + 4 <= tkhd.End) byId[Be32(b, off)] = t;
            }
            if (Find(b, Find(b, t, "tref"), "chap") is { } chap)
                for (int p = chap.Start; p + 4 <= chap.End; p += 4) targets.Add(Be32(b, p));
        }

        foreach (var id in targets)
        {
            if (!byId.TryGetValue(id, out var trak)) continue;
            var marks = ParseTextTrack(b, trak, fs);
            if (marks.Count > 1) return marks;
        }
        return new List<ChapterMark>();
    }

    private static List<ChapterMark> ParseTextTrack(byte[] b, Atom trak, FileStream fs)
    {
        var result = new List<ChapterMark>();
        var mdia = Find(b, trak, "mdia");
        if (Find(b, mdia, "mdhd") is not { } mdhd) return result;
        int mv = b[mdhd.Start];
        uint timescale = Be32(b, mdhd.Start + 4 + (mv == 1 ? 16 : 8));
        if (timescale == 0) return result;

        var stbl = Find(b, Find(b, mdia, "minf"), "stbl");
        if (stbl is not { } st) return result;
        var stts = Find(b, st, "stts");
        var stsz = Find(b, st, "stsz");
        var stsc = Find(b, st, "stsc");
        var stco = Find(b, st, "stco");
        var co64 = Find(b, st, "co64");
        if (stts is null || stsz is null || stsc is null || (stco is null && co64 is null)) return result;

        // sample start times
        var times = new List<double>();
        {
            var a = stts.Value;
            int p = a.Start + 4;
            uint n = Be32(b, p); p += 4;
            long t = 0;
            for (uint i = 0; i < n && p + 8 <= a.End; i++, p += 8)
            {
                uint cnt = Be32(b, p), delta = Be32(b, p + 4);
                for (uint k = 0; k < cnt && times.Count < 20000; k++) { times.Add(t / (double)timescale); t += delta; }
            }
        }

        // sample sizes
        var sizes = new List<int>();
        {
            var a = stsz.Value;
            int p = a.Start + 4;
            uint fixedSize = Be32(b, p); p += 4;
            uint n = Be32(b, p); p += 4;
            for (uint i = 0; i < n && i < 20000; i++)
            {
                if (fixedSize != 0) sizes.Add((int)fixedSize);
                else { if (p + 4 > a.End) break; sizes.Add((int)Be32(b, p)); p += 4; }
            }
        }

        // chunk offsets
        var chunkOffsets = new List<long>();
        if (stco is { } sc)
        {
            int p = sc.Start + 4; uint n = Be32(b, p); p += 4;
            for (uint i = 0; i < n && p + 4 <= sc.End; i++, p += 4) chunkOffsets.Add(Be32(b, p));
        }
        else if (co64 is { } c6)
        {
            int p = c6.Start + 4; uint n = Be32(b, p); p += 4;
            for (uint i = 0; i < n && p + 8 <= c6.End; i++, p += 8) chunkOffsets.Add((long)Be64(b, p));
        }

        // samples-per-chunk runs
        var runs = new List<(uint first, uint per)>();
        {
            var a = stsc.Value;
            int p = a.Start + 4; uint n = Be32(b, p); p += 4;
            for (uint i = 0; i < n && p + 12 <= a.End; i++, p += 12) runs.Add((Be32(b, p), Be32(b, p + 4)));
        }
        if (runs.Count == 0 || chunkOffsets.Count == 0) return result;

        // walk chunks -> sample file offsets
        int sample = 0;
        for (int chunk = 0; chunk < chunkOffsets.Count && sample < sizes.Count; chunk++)
        {
            uint per = runs[0].per;
            foreach (var r in runs) if (r.first <= chunk + 1) per = r.per;
            long off = chunkOffsets[chunk];
            for (uint s = 0; s < per && sample < sizes.Count; s++, sample++)
            {
                int size = sizes[sample];
                if (size >= 2 && size < 4096 && off + size <= fs.Length)
                {
                    var buf = new byte[size];
                    fs.Position = off;
                    fs.ReadExactly(buf, 0, size);
                    int tl = Math.Min((buf[0] << 8) | buf[1], size - 2);
                    string title = DecodeText(buf, 2, tl);
                    double start = sample < times.Count ? times[sample] : 0;
                    result.Add(new ChapterMark(start, title));
                }
                off += size;
            }
        }
        return result;
    }

    private static string DecodeText(byte[] b, int off, int len)
    {
        if (len <= 0) return "";
        if (len >= 2 && b[off] == 0xFE && b[off + 1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, off + 2, len - 2).Trim('\0', ' ');
        if (len >= 2 && b[off] == 0xFF && b[off + 1] == 0xFE) return Encoding.Unicode.GetString(b, off + 2, len - 2).Trim('\0', ' ');
        return Encoding.UTF8.GetString(b, off, len).Trim('\0', ' ');
    }

    private static uint Be32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    private static ulong Be64(byte[] b, int o) => ((ulong)Be32(b, o) << 32) | Be32(b, o + 4);
}
