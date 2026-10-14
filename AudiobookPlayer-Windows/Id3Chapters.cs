using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AudiobookPlayer;

/// Reads ID3v2.3 / v2.4 "CHAP" frames (chapter markers inside a single big mp3).
public static class Id3Chapters
{
    public static List<ChapterMark> Read(string path)
    {
        var result = new List<ChapterMark>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var h = new byte[10];
            if (fs.Read(h, 0, 10) < 10 || h[0] != 'I' || h[1] != 'D' || h[2] != '3') return result;
            int ver = h[3];
            if (ver != 3 && ver != 4) return result;
            bool extHeader = (h[5] & 0x40) != 0;
            int size = SyncSafe(h, 6);
            if (size <= 0 || size > 80 * 1024 * 1024) return result;

            var buf = new byte[size];
            fs.ReadExactly(buf, 0, size);

            int pos = 0;
            if (extHeader && size >= 4) pos = ver == 4 ? SyncSafe(buf, 0) : (int)Be32(buf, 0) + 4;

            while (pos + 10 <= size)
            {
                if (buf[pos] == 0) break; // padding
                string id = Encoding.ASCII.GetString(buf, pos, 4);
                int fsize = ver == 4 ? SyncSafe(buf, pos + 4) : (int)Be32(buf, pos + 4);
                pos += 10;
                if (fsize <= 0 || pos + fsize > size) break;
                if (id == "CHAP") ParseChap(buf, pos, pos + fsize, ver, result);
                pos += fsize;
            }
        }
        catch { }
        return result;
    }

    private static void ParseChap(byte[] b, int start, int end, int ver, List<ChapterMark> output)
    {
        int p = start;
        while (p < end && b[p] != 0) p++;   // element id
        p++;
        if (p + 16 > end) return;
        double startSec = Be32(b, p) / 1000.0;
        p += 16;                            // start, end, start offset, end offset

        string title = "";
        while (p + 10 <= end)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int fsize = ver == 4 ? SyncSafe(b, p + 4) : (int)Be32(b, p + 4);
            p += 10;
            if (fsize <= 0 || p + fsize > end) break;
            if (id == "TIT2") title = DecodeText(b, p, fsize);
            p += fsize;
        }
        output.Add(new ChapterMark(startSec, title));
    }

    private static string DecodeText(byte[] b, int offset, int length)
    {
        if (length < 2) return "";
        byte enc = b[offset];
        int o = offset + 1, n = length - 1;
        string s;
        switch (enc)
        {
            case 1:
                if (n >= 2 && b[o] == 0xFF && b[o + 1] == 0xFE) s = Encoding.Unicode.GetString(b, o + 2, n - 2);
                else if (n >= 2 && b[o] == 0xFE && b[o + 1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(b, o + 2, n - 2);
                else s = Encoding.Unicode.GetString(b, o, n);
                break;
            case 2: s = Encoding.BigEndianUnicode.GetString(b, o, n); break;
            case 3: s = Encoding.UTF8.GetString(b, o, n); break;
            default: s = Encoding.Latin1.GetString(b, o, n); break;
        }
        return s.Trim('\0', ' ');
    }

    private static int SyncSafe(byte[] b, int o) => (b[o] << 21) | (b[o + 1] << 14) | (b[o + 2] << 7) | b[o + 3];
    private static uint Be32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
}
