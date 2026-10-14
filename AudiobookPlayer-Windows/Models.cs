using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AudiobookPlayer;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Library model ───────────────────────────────────────────────

public sealed record Track(int Index, string Path, string Title);

public sealed record Book(string Author, string Title, string Folder, string? FolderArt, IReadOnlyList<Track> Tracks)
{
    public string Id => $"{Author}|||{Title}";
}

public sealed record AuthorGroup(string Name, List<Book> Books);

// ── Chapters ────────────────────────────────────────────────────

public sealed record ChapterMark(double Start, string Title);

public sealed class TrackInfo
{
    public double Duration { get; set; }
    public List<ChapterMark> Chapters { get; set; } = new();
}

/// One row in the chapter list: a whole file, or one embedded chapter marker inside a file.
public sealed record ChapterEntry(int Id, int TrackIndex, double Start, double End, string Title, bool Embedded);

// ── Persisted state ─────────────────────────────────────────────

public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public double Time { get; set; }
    public int TrackIndex { get; set; }
}

public sealed class BookState
{
    public int LastTrack { get; set; }
    public Dictionary<int, double> Positions { get; set; } = new();
    public List<Bookmark> Bookmarks { get; set; } = new();
    public string? CoverFile { get; set; }
    public double? Speed { get; set; }
    public DateTime? LastPlayed { get; set; }
    public List<double>? TrackDurations { get; set; }
}

public sealed class AppSettings
{
    public int SkipSeconds { get; set; } = 10;
    public int AutoRewind { get; set; } = 5;
    public bool PerBookSpeed { get; set; } = true;
    public bool ShowTray { get; set; } = true;
    public bool CloseToTray { get; set; } = false;
}

public sealed class AppState
{
    public string? LibraryPath { get; set; }
    public string? LastBookId { get; set; }
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    public Dictionary<string, BookState> Books { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

// ── Formatting ──────────────────────────────────────────────────

public static class Fmt
{
    public static string Time(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) s = 0;
        int t = (int)s;
        return $"{t / 3600}:{(t % 3600) / 60:00}:{t % 60:00}";
    }

    public static string Duration(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
        int t = (int)Math.Max(s, 0), h = t / 3600, m = (t % 3600) / 60;
        if (h > 0) return $"{h}h {m}m";
        if (m > 0) return $"{m}m";
        return "<1m";
    }
}

/// Sorts like Explorer does ("2" before "10").
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
