using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudiobookPlayer;

public sealed class Tags
{
    public string? Title { get; set; }
    public string? Narrator { get; set; }
    public byte[]? Art { get; set; }
}

public static class TagService
{
    /// Title, narrator and embedded cover art (via TagLib#).
    public static Tags Read(string path)
    {
        var t = new Tags();
        try
        {
            using var f = TagLib.File.Create(path);
            var tag = f.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) t.Title = tag.Title.Trim();

            if (!string.IsNullOrWhiteSpace(tag.Conductor)) t.Narrator = tag.Conductor.Trim();
            else if (!string.IsNullOrWhiteSpace(tag.Comment) && tag.Comment.TrimStart().StartsWith("narrat", StringComparison.OrdinalIgnoreCase))
            {
                var c = tag.Comment.Trim();
                int colon = c.IndexOf(':');
                t.Narrator = colon >= 0 && colon < 20 ? c[(colon + 1)..].Trim() : c;
            }

            if (tag.Pictures.Length > 0) t.Art = tag.Pictures[0].Data.Data;
        }
        catch { /* unreadable tags are fine */ }
        return t;
    }

    public static byte[]? ReadArt(string path) => Read(path).Art;

    /// Duration plus chapter markers embedded in the file (ID3 CHAP for mp3, chpl / text track for m4a/m4b).
    public static TrackInfo ReadOutline(string path)
    {
        var info = new TrackInfo();
        try
        {
            using var f = TagLib.File.Create(path);
            info.Duration = f.Properties.Duration.TotalSeconds;
        }
        catch { }

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            List<ChapterMark> marks = ext switch
            {
                ".mp3" => Id3Chapters.Read(path),
                ".m4a" or ".m4b" => Mp4Chapters.Read(path),
                _ => new List<ChapterMark>(),
            };
            marks = marks.OrderBy(m => m.Start).ToList();
            if (marks.Count > 1) info.Chapters = marks;
        }
        catch { }
        return info;
    }
}

public static class Imaging
{
    public static BitmapSource? Load(byte[] data, int width)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = ms;
            bi.DecodePixelWidth = width;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static BitmapSource? Load(string path, int width)
    {
        try { return Load(System.IO.File.ReadAllBytes(path), width); }
        catch { return null; }
    }

    public static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }
}
