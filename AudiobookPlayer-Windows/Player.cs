using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Storage;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;
using WinPlaybackState = Windows.Media.Playback.MediaPlaybackState;
using WinSource = Windows.Media.Core.MediaSource;
using WinSmtc = Windows.Media.SystemMediaTransportControls;
using WinSmtcButton = Windows.Media.SystemMediaTransportControlsButton;

namespace AudiobookPlayer;

/// Playback engine + all app state. The window binds straight to this.
public sealed class Player : ObservableObject
{
    public static readonly double[] Speeds = { 0.5, 0.6, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5 };

    private readonly Dispatcher ui;
    private readonly Store store;
    private readonly WinMediaPlayer mp = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WinSmtc? smtc;

    // ── library ──────────────────────────────────────────────
    private List<AuthorGroup> authors = new();
    private readonly Dictionary<string, ImageSource> thumbs = new();
    private readonly List<BookRowVM> rows = new();
    private CancellationTokenSource? thumbCts, outlineCts, coverCts;
    private string searchQuery = "";

    public AppSettings Settings => store.State.Settings;
    public string DataFolder => store.BaseDir;
    public int BookCount => authors.Sum(a => a.Books.Count);

    private string? libraryPath;
    public string? LibraryPath { get => libraryPath; private set { if (Set(ref libraryPath, value)) RaiseLibrary(); } }

    private bool libraryLoaded;
    public bool LibraryLoaded { get => libraryLoaded; private set { if (Set(ref libraryLoaded, value)) RaiseLibrary(); } }

    private IReadOnlyList<object> sidebar = Array.Empty<object>();
    public IReadOnlyList<object> Sidebar { get => sidebar; private set => Set(ref sidebar, value); }

    public string LibraryName => string.IsNullOrEmpty(LibraryPath) ? "" : (Path.GetFileName(LibraryPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : LibraryPath);
    public bool ShowFooter => LibraryPath != null;
    public string BookCountText => LibraryLoaded ? $"{BookCount} book{(BookCount == 1 ? "" : "s")}" : "Scanning...";
    public bool ShowSidebarHint => authors.Count == 0;
    public string SidebarHint =>
        LibraryPath == null ? "Choose a folder organised as\nAuthor \\ Book \\ 01 - track.mp3"
        : !LibraryLoaded ? "Scanning..."
        : $"No books in \"{LibraryName}\" yet.\nAdd audio as Author \\ Book \\ 01 - track.mp3,\nthen rescan (Ctrl+R).";

    // Welcome screen
    public string WelcomeMode => LibraryPath == null ? "none" : !LibraryLoaded ? "scanning" : BookCount == 0 ? "empty" : "ready";
    public string WelcomeGlyph => WelcomeMode switch { "none" => "\uE8F1", "scanning" => "\uE8B7", "empty" => "\uE710", _ => "\uE7F6" };
    public string WelcomeTitle => WelcomeMode switch
    {
        "none" => "Open your library",
        "scanning" => "Scanning your library",
        "empty" => "Library added",
        _ => "Pick something to listen to",
    };
    public string WelcomeMessage => WelcomeMode switch
    {
        "none" => "Choose a folder structured as\nAuthor > Book > audio files\n(single .m4b files with chapters work too).\n\nYou can also drag a folder onto this window.",
        "scanning" => $"Looking through \"{LibraryName}\"...",
        "empty" => $"\"{LibraryName}\" is set as your library but has no books in it yet.\n\nAdd audio as Author > Book > files, then rescan.\nNew books also appear automatically when you come back to this app.",
        _ => $"{BookCount} book{(BookCount == 1 ? "" : "s")} in \"{LibraryName}\".\nChoose one from the sidebar.",
    };

    private void RaiseLibrary()
    {
        foreach (var n in new[] { nameof(LibraryName), nameof(ShowFooter), nameof(BookCountText), nameof(ShowSidebarHint),
                                  nameof(SidebarHint), nameof(WelcomeMode), nameof(WelcomeGlyph), nameof(WelcomeTitle), nameof(WelcomeMessage) })
            Raise(n);
    }

    // ── current book ─────────────────────────────────────────
    private Book? book;
    public Book? Book { get => book; private set { if (Set(ref book, value)) { Raise(nameof(HasBook)); Raise(nameof(BookTitle)); Raise(nameof(BookAuthor)); } } }
    public bool HasBook => Book != null;
    public string BookTitle => Book?.Title ?? "";
    public string BookAuthor => Book?.Author ?? "";

    private int trackIndex;
    public int TrackIndex { get => trackIndex; private set => Set(ref trackIndex, value); }

    private string trackTitle = "";
    public string TrackTitle { get => trackTitle; private set { if (Set(ref trackTitle, value)) Raise(nameof(ChapterTitle)); } }

    private string? narrator;
    public string? Narrator { get => narrator; private set { if (Set(ref narrator, value)) { Raise(nameof(NarratorText)); Raise(nameof(HasNarrator)); } } }
    public bool HasNarrator => !string.IsNullOrEmpty(Narrator);
    public string NarratorText => HasNarrator ? $"Narrated by {Narrator}" : "";

    private ImageSource? cover;
    public ImageSource? Cover { get => cover; private set => Set(ref cover, value); }

    private ImageSource? backdropCover;
    public ImageSource? BackdropCover { get => backdropCover; private set => Set(ref backdropCover, value); }

    private Dictionary<int, TrackInfo> trackInfo = new();
    private List<ChapterEntry> entries = new();
    private int entryIndex;
    public ObservableCollection<ChapterRowVM> ChapterRows { get; private set; } = new();
    public ObservableCollection<BookmarkVM> Bookmarks { get; } = new();
    public bool ShowChapters => entries.Count > 1;
    public string ChaptersLabel => $"CHAPTERS · {entries.Count}";
    public bool HasNoBookmarks => Bookmarks.Count == 0;

    /// Raised when the active chapter changes (so the list can scroll to it).
    public event Action<int>? ChapterChanged;
    /// Raised when the now-playing text changes (tray tooltip etc.).
    public event Action? NowPlayingChanged;

    // ── transport ────────────────────────────────────────────
    private bool isPlaying;
    public bool IsPlaying { get => isPlaying; private set { if (Set(ref isPlaying, value)) { Raise(nameof(PlayGlyph)); } } }
    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    private double currentTime;
    private double duration;
    public double Duration { get => duration; private set => Set(ref duration, value); }

    private double speed = 1.0;
    public double Speed { get => speed; private set { if (Set(ref speed, value)) { Raise(nameof(SpeedLabel)); Raise(nameof(SpeedActive)); } } }
    public string SpeedLabel => $"{Speed:0.##}x";
    public bool SpeedActive => Math.Abs(Speed - 1.0) > 0.001;

    private double volume = 1.0;
    public double Volume
    {
        get => volume;
        set { value = Math.Clamp(value, 0, 1); if (Set(ref volume, value)) { mp.Volume = value; store.State.Volume = value; store.Save(); } }
    }

    private double? chapterDrag, bookDrag;
    public void SetChapterDrag(double? f) { chapterDrag = f; RaiseTime(); }
    public void SetBookDrag(double? f) { bookDrag = f; RaiseTime(); }

    public string SkipBackLabel => $"\u21BA{Settings.SkipSeconds}";
    public string SkipForwardLabel => $"{Settings.SkipSeconds}\u21BB";

    // derived time values
    private ChapterEntry? EntryForTrack() => entryIndex < entries.Count && entries[entryIndex].TrackIndex == trackIndex ? entries[entryIndex] : null;
    public double ChapterStart => EntryForTrack()?.Start ?? 0;
    public double ChapterElapsed => Math.Max(0, currentTime - ChapterStart);
    public double ChapterLength
    {
        get
        {
            var e = EntryForTrack();
            if (e != null && e.End > e.Start) return e.End - e.Start;
            return Math.Max(duration - ChapterStart, 0);
        }
    }
    public double ChapterFraction => ChapterLength > 0 ? Math.Min(1, ChapterElapsed / ChapterLength) : 0;
    public string ChapterElapsedText => Fmt.Time(chapterDrag is { } d ? d * ChapterLength : ChapterElapsed);
    public string ChapterLengthText => Fmt.Time(ChapterLength);

    public bool BookDurationKnown => Book != null && Book.Tracks.Count > 0 && Book.Tracks.All(t => trackInfo.TryGetValue(t.Index, out var i) && i.Duration > 0);
    public double BookTotal => trackInfo.Values.Sum(i => i.Duration);
    public double BookElapsed => TrackOffset(trackIndex) + currentTime;
    public double BookFraction => BookTotal > 0 ? Math.Min(1, BookElapsed / BookTotal) : 0;
    public bool ShowBookBar => BookDurationKnown && BookTotal > ChapterLength + 1;
    public string BookPercentText => $"Book {(int)((bookDrag ?? BookFraction) * 100)}%";
    public string BookLeftText => $"{Fmt.Duration((BookTotal - BookElapsed) / Math.Max(Speed, 0.1))} left";
    public double OverallFraction => BookDurationKnown && BookTotal > 0 ? BookFraction : ChapterFraction;

    public string ChapterTitle => EntryForTrack() is { Embedded: true } e ? e.Title : TrackTitle;
    public string ChapterLabel => $"Chapter {entryIndex + 1} of {Math.Max(entries.Count, 1)}";

    private double TrackOffset(int i)
    {
        double sum = 0;
        for (int k = 0; k < i; k++) if (trackInfo.TryGetValue(k, out var ti)) sum += ti.Duration;
        return sum;
    }

    private void RaiseTime()
    {
        foreach (var n in new[] { nameof(ChapterFraction), nameof(ChapterElapsedText), nameof(ChapterLengthText), nameof(BookFraction),
                                  nameof(BookPercentText), nameof(BookLeftText), nameof(ShowBookBar), nameof(OverallFraction) })
            Raise(n);
    }

    private void RaiseChapterProps()
    {
        Raise(nameof(ChapterLabel)); Raise(nameof(ChapterTitle)); Raise(nameof(ShowChapters)); Raise(nameof(ChaptersLabel));
    }

    // ── sleep timer ──────────────────────────────────────────
    private DispatcherTimer? sleepTimer;
    private DateTime sleepDeadline;
    private int sleepArmedTrack;
    private double sleepArmedStart;

    private int? sleepRemaining;
    public int? SleepRemaining { get => sleepRemaining; private set { if (Set(ref sleepRemaining, value)) RaiseSleep(); } }
    private bool sleepAtChapterEnd;
    public bool SleepAtChapterEnd { get => sleepAtChapterEnd; private set { if (Set(ref sleepAtChapterEnd, value)) RaiseSleep(); } }
    public bool SleepActive => SleepRemaining != null || SleepAtChapterEnd;
    public string SleepLabel => SleepRemaining is int r ? $"{r / 60}:{r % 60:00}" : SleepAtChapterEnd ? "end of ch." : "Sleep";
    private void RaiseSleep() { Raise(nameof(SleepActive)); Raise(nameof(SleepLabel)); }

    // ── misc UI ──────────────────────────────────────────────
    private string? toast;
    public string? Toast { get => toast; private set => Set(ref toast, value); }
    private CancellationTokenSource? toastCts;

    public event Action? BookmarkRequested;
    public void RequestBookmark() => BookmarkRequested?.Invoke();

    // ── internals ────────────────────────────────────────────
    private int loadGen;
    private bool isLoading;
    private DateTime lastSave = DateTime.MinValue;
    private DateTime pausedAt = DateTime.MinValue;
    private TaskCompletionSource<bool>? openedTcs;
    private WinSource? currentSource;
    private Windows.Storage.Streams.IRandomAccessStream? currentStream;
    private string? lastCoverTemp;

    public Player(Dispatcher dispatcher)
    {
        ui = dispatcher;
        store = new Store(ui);

        volume = store.State.Volume;
        speed = store.State.Speed;
        mp.Volume = volume;
        mp.AutoPlay = false;
        mp.CommandManager.IsEnabled = false;     // we drive the system media controls ourselves

        mp.MediaOpened += (_, _) => openedTcs?.TrySetResult(true);
        mp.MediaFailed += (_, _) => openedTcs?.TrySetResult(false);
        mp.MediaEnded += (_, _) => ui.InvokeAsync(ItemEnded);
        mp.PlaybackSession.PlaybackStateChanged += (_, _) => ui.InvokeAsync(OnPlaybackState);

        SetupSmtc();

        timer.Tick += (_, _) => Tick();
        timer.Start();

        if (store.State.LibraryPath is { } path)
        {
            LibraryPath = path;
            LoadLibrary(path, restoreLast: true, silent: false);
        }
    }

    public void Shutdown()
    {
        PersistPosition();
        store.Flush();
        try { mp.Pause(); mp.Dispose(); } catch { }
    }

    // ═══ Library ═════════════════════════════════════════════

    public void ChooseLibrary()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your audiobook library (Author \\ Book \\ chapter files)" };
        if (dlg.ShowDialog() == true) SetLibrary(dlg.FolderName);
    }

    public void SetLibrary(string path)
    {
        store.State.LibraryPath = path;
        store.Save();
        LibraryPath = path;
        LibraryLoaded = false;
        LoadLibrary(path, restoreLast: false, silent: false);
    }

    public void OpenDropped(string path)
    {
        if (Directory.Exists(path)) SetLibrary(path);
        else Notify("Drop a library folder, not a single file");
    }

    public void Rescan()
    {
        if (LibraryPath == null) { ChooseLibrary(); return; }
        LoadLibrary(LibraryPath, restoreLast: false, silent: false);
    }

    /// Quiet rescan when the window is re-activated, so new books just appear.
    public void RefreshLibrary()
    {
        if (!LibraryLoaded || LibraryPath == null) return;
        LoadLibrary(LibraryPath, restoreLast: false, silent: true);
    }

    private async void LoadLibrary(string path, bool restoreLast, bool silent)
    {
        var found = await Task.Run(() => LibraryScanner.Scan(path));
        if (silent)
        {
            if (found.Count == 0 && authors.Count > 0 && !Directory.Exists(path)) return;   // drive unplugged: keep what we had
            if (LibraryScanner.Same(found, authors)) return;                                 // nothing new: don't disturb the list
        }

        authors = found;
        LibraryLoaded = true;
        var all = found.SelectMany(a => a.Books).ToList();
        RebuildSidebar();
        RaiseLibrary();

        if (restoreLast && Book == null && store.State.LastBookId is { } id && all.FirstOrDefault(b => b.Id == id) is { } last)
            Open(last);
        else if (!restoreLast && !silent)
            Notify(all.Count == 0 ? "Folder added. No books in it yet" : $"{all.Count} book{(all.Count == 1 ? "" : "s")} found");

        LoadThumbs(all);
    }

    public void SetSearch(string q)
    {
        searchQuery = q ?? "";
        RebuildSidebar();
    }

    private void RebuildSidebar()
    {
        rows.Clear();
        var list = new List<object>();
        string q = searchQuery.Trim().ToLowerInvariant();

        void AddRow(Book b, bool showAuthor)
        {
            var r = new BookRowVM(b, showAuthor);
            UpdateRow(r);
            rows.Add(r);
            list.Add(r);
        }

        if (q.Length == 0)
        {
            var recent = RecentBooks();
            if (recent.Count > 0)
            {
                list.Add(new SectionHeaderVM("CONTINUE LISTENING"));
                foreach (var b in recent) AddRow(b, true);
            }
        }

        foreach (var a in authors)
        {
            List<Book> books = q.Length == 0 || a.Name.ToLowerInvariant().Contains(q)
                ? a.Books
                : a.Books.Where(b => b.Title.ToLowerInvariant().Contains(q)).ToList();
            if (books.Count == 0) continue;
            list.Add(new SectionHeaderVM(a.Name.ToUpperInvariant()));
            foreach (var b in books) AddRow(b, false);
        }
        Sidebar = list;
    }

    private List<Book> RecentBooks()
    {
        return authors.SelectMany(a => a.Books)
            .Select(b => (book: b, played: PeekState(b.Id)?.LastPlayed))
            .Where(x => x.played != null && !(ProgressFor(x.book) is >= 0.99))
            .OrderByDescending(x => x.played)
            .Take(3)
            .Select(x => x.book)
            .ToList();
    }

    private void UpdateRow(BookRowVM r)
    {
        var b = r.Book;
        double? p = ProgressFor(b);
        r.IsActive = Book?.Id == b.Id;
        r.IsPlaying = r.IsActive && IsPlaying;
        r.HasCustomCover = PeekState(b.Id)?.CoverFile != null;
        r.Cover = thumbs.TryGetValue(b.Id, out var img) ? img : null;

        string files = b.Tracks.Count == 1 ? "1 file" : $"{b.Tracks.Count} chapters";
        if (r.ShowAuthor) r.Subtitle = b.Author;
        else if (p is >= 0.99) r.Subtitle = "Finished ✓";
        else if (p is > 0.005) r.Subtitle = $"{(int)(p.Value * 100)}% · {files}";
        else r.Subtitle = files;

        r.ShowProgress = p is > 0.005 and < 0.99;
        r.Progress = p ?? 0;
    }

    private void RefreshRowsFor(string id)
    {
        foreach (var r in rows) if (r.Book.Id == id) UpdateRow(r);
    }

    private void RefreshAllRows() { foreach (var r in rows) UpdateRow(r); }

    public double? ProgressFor(Book b)
    {
        if (b.Id == Book?.Id && BookDurationKnown && BookTotal > 0) return Math.Min(1, BookElapsed / BookTotal);
        var st = PeekState(b.Id);
        if (st?.TrackDurations is not { } d || d.Count != b.Tracks.Count) return null;
        double total = d.Sum();
        if (total <= 0) return null;
        int idx = Math.Clamp(st.LastTrack, 0, d.Count - 1);
        double elapsed = d.Take(idx).Sum() + Math.Min(st.Positions.GetValueOrDefault(idx), d[idx]);
        return Math.Min(1, elapsed / total);
    }

    // ═══ State helpers ═══════════════════════════════════════

    private BookState State(string id)
    {
        if (!store.State.Books.TryGetValue(id, out var s)) store.State.Books[id] = s = new BookState();
        return s;
    }
    private BookState? PeekState(string id) => store.State.Books.TryGetValue(id, out var s) ? s : null;
    private void Mutate(string id, Action<BookState> change) { change(State(id)); store.Save(); }

    // ═══ Covers ══════════════════════════════════════════════

    private string? CustomCoverPath(string id)
        => PeekState(id)?.CoverFile is { } f ? Path.Combine(store.CoversDir, f) : null;

    private static byte[]? FindCoverBytes(Book b, string? custom)
    {
        try
        {
            if (custom != null && System.IO.File.Exists(custom)) return System.IO.File.ReadAllBytes(custom);
            if (b.FolderArt != null) return System.IO.File.ReadAllBytes(b.FolderArt);
            if (b.Tracks.Count > 0) return TagService.ReadArt(b.Tracks[0].Path);
        }
        catch { }
        return null;
    }

    /// Small thumbnails for the sidebar, filled in on a background thread.
    private async void LoadThumbs(List<Book> books)
    {
        thumbCts?.Cancel();
        var cts = thumbCts = new CancellationTokenSource();
        var ct = cts.Token;
        foreach (var b in books)
        {
            if (ct.IsCancellationRequested) return;
            if (thumbs.ContainsKey(b.Id)) continue;
            string? custom = CustomCoverPath(b.Id);
            BitmapSourceBox box;
            try { box = await Task.Run(() => new BitmapSourceBox(FindCoverBytes(b, custom) is { } bytes ? Imaging.Load(bytes, 120) : null), ct); }
            catch (OperationCanceledException) { return; }
            if (box.Image == null || ct.IsCancellationRequested) continue;
            thumbs[b.Id] = box.Image;
            foreach (var r in rows) if (r.Book.Id == b.Id) r.Cover = box.Image;
        }
    }

    private sealed record BitmapSourceBox(ImageSource? Image);

    /// Full-size cover + tiny blurred backdrop for the book that's open.
    private async void LoadCoverFor(Book b)
    {
        coverCts?.Cancel();
        var cts = coverCts = new CancellationTokenSource();
        string? custom = CustomCoverPath(b.Id);
        var result = await Task.Run(() =>
        {
            var bytes = FindCoverBytes(b, custom);
            System.Windows.Media.Imaging.BitmapSource? large = null, tiny = null;
            if (bytes != null) { large = Imaging.Load(bytes, 700); tiny = Imaging.Load(bytes, 40); }
            return (large, tiny);
        });
        if (cts.IsCancellationRequested || Book?.Id != b.Id) return;
        SetCoverImages(result.large, result.tiny);
    }

    private void SetCoverImages(ImageSource? large, ImageSource? tiny)
    {
        Cover = large;
        BackdropCover = tiny;
        if (large is System.Windows.Media.Imaging.BitmapSource bs) UpdateSmtcThumbnail(bs);
    }

    public void SetCover(string imagePath)
    {
        if (Book is not { } b) return;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(imagePath);
            var large = Imaging.Load(bytes, 700);
            if (large == null) { Notify("Couldn't read that image"); return; }

            if (PeekState(b.Id)?.CoverFile is { } old)
                try { System.IO.File.Delete(Path.Combine(store.CoversDir, old)); } catch { }
            string name = Guid.NewGuid().ToString("N") + Path.GetExtension(imagePath).ToLowerInvariant();
            System.IO.File.WriteAllBytes(Path.Combine(store.CoversDir, name), bytes);
            Mutate(b.Id, s => s.CoverFile = name);

            var thumb = Imaging.Load(bytes, 120);
            if (thumb != null) thumbs[b.Id] = thumb;
            SetCoverImages(large, Imaging.Load(bytes, 40));
            RefreshRowsFor(b.Id);
            Notify("Cover updated");
        }
        catch { Notify("Couldn't read that image"); }
    }

    public void RemoveCustomCover(Book b)
    {
        if (PeekState(b.Id)?.CoverFile is { } old)
            try { System.IO.File.Delete(Path.Combine(store.CoversDir, old)); } catch { }
        Mutate(b.Id, s => s.CoverFile = null);
        thumbs.Remove(b.Id);
        foreach (var r in rows) if (r.Book.Id == b.Id) r.Cover = null;
        LoadThumbs(new List<Book> { b });
        if (Book?.Id == b.Id) LoadCoverFor(b);
        RefreshRowsFor(b.Id);
        Notify("Custom cover removed");
    }

    public bool HasCustomCover(Book b) => PeekState(b.Id)?.CoverFile != null;

    // ═══ Opening & loading ═══════════════════════════════════

    public void Open(Book b)
    {
        if (Book?.Id == b.Id) return;
        PersistPosition();
        outlineCts?.Cancel();

        Book = b;
        store.State.LastBookId = b.Id;
        var st = State(b.Id);

        Bookmarks.Clear();
        trackInfo = new Dictionary<int, TrackInfo>();
        if (st.TrackDurations is { } d && d.Count == b.Tracks.Count)
            for (int i = 0; i < d.Count; i++) trackInfo[i] = new TrackInfo { Duration = d[i] };
        foreach (var bm in st.Bookmarks) Bookmarks.Add(new BookmarkVM(bm, 1));

        entryIndex = 0;
        ChapterRows = new ObservableCollection<ChapterRowVM>();
        RebuildEntries();
        Raise(nameof(ChapterRows));
        ApplySpeed(Settings.PerBookSpeed ? (st.Speed ?? 1.0) : store.State.Speed);
        pausedAt = DateTime.MinValue;

        SetCoverImages(thumbs.TryGetValue(b.Id, out var t) ? t : null, null);   // instant placeholder while the real one loads
        LoadCoverFor(b);

        int idx = Math.Clamp(st.LastTrack, 0, b.Tracks.Count - 1);
        LoadTrack(idx, st.Positions.GetValueOrDefault(idx), play: false);
        LoadOutline(b);
        RefreshBookmarkMeta();
        RefreshAllRows();
        Raise(nameof(HasNoBookmarks));
        store.Save();
    }

    private async void LoadTrack(int index, double seek, bool play)
    {
        if (Book is not { } b || index < 0 || index >= b.Tracks.Count) return;
        int gen = ++loadGen;
        isLoading = true;
        TrackIndex = index;
        currentTime = seek;
        Duration = 0;
        TrackTitle = b.Tracks[index].Title;
        Narrator = null;
        RefreshEntryIndex();
        RaiseTime();

        var tcs = openedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = b.Tracks[index].Path;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var stream = await file.OpenReadAsync();
            if (gen != loadGen) { stream.Dispose(); return; }
            var src = WinSource.CreateFromStream(stream, ContentTypeFor(path));
            var oldSource = currentSource; var oldStream = currentStream;
            currentSource = src; currentStream = stream;
            mp.Source = src;
            oldSource?.Dispose();
            oldStream?.Dispose();
        }
        catch
        {
            if (gen == loadGen) { isLoading = false; Notify("Couldn't open that file"); }
            return;
        }

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (gen != loadGen) return;
        if (winner != tcs.Task || !tcs.Task.Result)
        {
            isLoading = false;
            Notify($"Couldn't play \"{b.Tracks[index].Title}\"");
            return;
        }

        double dur = SafeSeconds(mp.PlaybackSession.NaturalDuration);
        if (dur <= 0 && trackInfo.TryGetValue(index, out var known)) dur = known.Duration;
        Duration = dur;
        if (seek > 1) SafeSetPosition(Math.Min(seek, Math.Max(dur - 1, 0)));
        try { mp.PlaybackSession.PlaybackRate = Speed; } catch { }

        isLoading = false;
        RefreshEntryIndex();
        RaiseTime();
        if (play) Play();
        UpdateSmtc();

        var tags = await Task.Run(() => TagService.Read(path));
        if (gen != loadGen) return;
        if (!string.IsNullOrWhiteSpace(tags.Title)) TrackTitle = tags.Title!;
        Narrator = tags.Narrator;
        UpdateSmtc();
    }

    private static double SafeSeconds(TimeSpan t)
    {
        double s = t.TotalSeconds;
        return double.IsNaN(s) || double.IsInfinity(s) || s < 0 || s > 1e7 ? 0 : s;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        _ => "audio/mpeg",
    };

    private void SafeSetPosition(double seconds)
    {
        try { mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(seconds, 0)); } catch { }
    }

    /// Reads durations + embedded chapter markers for every file in the book, in the background.
    private async void LoadOutline(Book b)
    {
        outlineCts?.Cancel();
        var cts = outlineCts = new CancellationTokenSource();
        var ct = cts.Token;
        var infos = new Dictionary<int, TrackInfo>(trackInfo);

        for (int n = 0; n < b.Tracks.Count; n++)
        {
            if (ct.IsCancellationRequested || Book?.Id != b.Id) return;
            string path = b.Tracks[n].Path;
            TrackInfo info;
            try { info = await Task.Run(() => TagService.ReadOutline(path), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested || Book?.Id != b.Id) return;
            infos[b.Tracks[n].Index] = info;

            if (n % 6 == 5 || n == b.Tracks.Count - 1)
            {
                trackInfo = new Dictionary<int, TrackInfo>(infos);
                RebuildEntries();
                RaiseTime();
            }
        }

        var durations = b.Tracks.Select(t => infos.TryGetValue(t.Index, out var i) ? i.Duration : 0).ToList();
        Mutate(b.Id, s => s.TrackDurations = durations);
        RefreshBookmarkMeta();
        RefreshRowsFor(b.Id);
    }

    // ═══ Chapters ════════════════════════════════════════════

    private void RebuildEntries()
    {
        if (Book is not { } b) { entries = new List<ChapterEntry>(); return; }
        var list = new List<ChapterEntry>();
        foreach (var t in b.Tracks)
        {
            trackInfo.TryGetValue(t.Index, out var info);
            double dur = info?.Duration ?? 0;
            if (info != null && info.Chapters.Count > 1)
            {
                for (int i = 0; i < info.Chapters.Count; i++)
                {
                    var m = info.Chapters[i];
                    double end = i + 1 < info.Chapters.Count ? info.Chapters[i + 1].Start : dur;
                    list.Add(new ChapterEntry(list.Count, t.Index, m.Start, end,
                        string.IsNullOrWhiteSpace(m.Title) ? $"Chapter {i + 1}" : m.Title, true));
                }
            }
            else
            {
                list.Add(new ChapterEntry(list.Count, t.Index, 0, dur, t.Title, false));
            }
        }
        entries = list;

        if (ChapterRows.Count == list.Count)
        {
            for (int i = 0; i < list.Count; i++) FillRow(ChapterRows[i], list[i]);
        }
        else
        {
            var rowsNew = new ObservableCollection<ChapterRowVM>();
            foreach (var e in list) { var r = new ChapterRowVM(e.Id); FillRow(r, e); rowsNew.Add(r); }
            ChapterRows = rowsNew;
            Raise(nameof(ChapterRows));
        }
        RefreshEntryIndex();
        RaiseChapterProps();
    }

    private void FillRow(ChapterRowVM r, ChapterEntry e)
    {
        r.Title = e.Title;
        r.DurationText = e.End > e.Start ? Fmt.Duration(e.End - e.Start) : "";
        r.IsActive = e.Id == entryIndex;
        r.IsDone = e.Id < entryIndex;
    }

    private void RefreshEntryIndex()
    {
        int idx = 0;
        foreach (var e in entries)
            if (e.TrackIndex == trackIndex && e.Start <= currentTime + 0.05) idx = e.Id;
        if (idx < entries.Count && entries[idx].TrackIndex != trackIndex)
        {
            var first = entries.FirstOrDefault(x => x.TrackIndex == trackIndex);
            if (first != null) idx = first.Id;
        }
        if (idx == entryIndex) return;

        entryIndex = idx;
        foreach (var r in ChapterRows) { r.IsActive = r.Id == idx; r.IsDone = r.Id < idx; }
        RaiseChapterProps();
        UpdateSmtc();
        ChapterChanged?.Invoke(idx);
    }

    public int ChapterNumber(int track, double time)
    {
        int idx = 0;
        foreach (var e in entries) if (e.TrackIndex == track && e.Start <= time + 0.05) idx = e.Id;
        return idx + 1;
    }

    private void RefreshBookmarkMeta()
    {
        if (Book is not { } b) return;
        Bookmarks.Clear();
        foreach (var bm in State(b.Id).Bookmarks) Bookmarks.Add(new BookmarkVM(bm, ChapterNumber(bm.TrackIndex, bm.Time)));
        Raise(nameof(HasNoBookmarks));
    }

    // ═══ Transport ═══════════════════════════════════════════

    public void Play()
    {
        if (Book == null) { Notify("Open a library first"); return; }
        int rewind = Settings.AutoRewind;
        if (rewind > 0 && !IsPlaying && currentTime > rewind && (DateTime.UtcNow - pausedAt).TotalSeconds > 10)
        {
            currentTime = Math.Max(currentTime - rewind, 0);
            SafeSetPosition(currentTime);
        }
        mp.Play();
    }

    public void Pause()
    {
        mp.Pause();
        pausedAt = DateTime.UtcNow;
        PersistPosition();
    }

    public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

    public void Seek(double t)
    {
        if (Book == null) return;
        double target = Math.Max(0, duration > 0 ? Math.Min(t, duration) : t);
        currentTime = target;
        SafeSetPosition(target);
        RefreshEntryIndex();
        PersistPosition();
        RaiseTime();
    }

    public void SeekInChapter(double fraction) => Seek(ChapterStart + fraction * ChapterLength);

    public void SeekBook(double fraction)
    {
        if (Book is not { } b || !BookDurationKnown) return;
        double target = fraction * BookTotal;
        foreach (var t in b.Tracks)
        {
            double d = trackInfo.TryGetValue(t.Index, out var i) ? i.Duration : 0;
            if (target <= d || t.Index == b.Tracks.Count - 1)
            {
                target = Math.Min(target, d);
                if (t.Index == trackIndex) Seek(target);
                else { PersistPosition(); LoadTrack(t.Index, target, IsPlaying); }
                return;
            }
            target -= d;
        }
    }

    /// Skips by `seconds`, rolling over into the next / previous chapter file at the edges.
    public void Skip(double seconds)
    {
        if (Book == null) return;
        double target = currentTime + seconds;
        if (seconds > 0 && duration > 0 && target > duration) SkipForwardAcrossFiles(target - duration);
        else if (seconds < 0 && target < 0) SkipBackAcrossFiles(-target);
        else Seek(target);
    }

    public void SkipConfigured(int direction) => Skip(direction * (double)Settings.SkipSeconds);

    private void SkipForwardAcrossFiles(double overflow)
    {
        if (Book is not { } b) return;
        int idx = trackIndex + 1;
        if (idx >= b.Tracks.Count) { Seek(duration); return; }
        double remaining = overflow;
        while (idx < b.Tracks.Count - 1)
        {
            double d = trackInfo.TryGetValue(idx, out var i) ? i.Duration : 0;
            if (d <= 0 || remaining < d) break;
            remaining -= d;
            idx++;
        }
        PersistPosition();
        LoadTrack(idx, remaining, IsPlaying);
    }

    private async void SkipBackAcrossFiles(double deficit)
    {
        if (Book is not { } b) return;
        if (trackIndex == 0) { Seek(0); return; }
        int startTrack = trackIndex;
        string bookId = b.Id;
        int idx = startTrack - 1;
        double remaining = deficit;
        while (idx >= 0)
        {
            double d = trackInfo.TryGetValue(idx, out var i) ? i.Duration : 0;
            if (d <= 0)
            {
                string p = b.Tracks[idx].Path;
                d = await Task.Run(() => TagService.ReadOutline(p).Duration);
            }
            if (Book?.Id != bookId || trackIndex != startTrack) return;   // user moved on while we looked
            if (d <= 0) { PersistPosition(); LoadTrack(idx, 0, IsPlaying); return; }
            if (remaining <= d || idx == 0)
            {
                PersistPosition();
                LoadTrack(idx, Math.Max(d - remaining, 0), IsPlaying);
                return;
            }
            remaining -= d;
            idx--;
        }
    }

    public void Next()
    {
        if (entryIndex + 1 < entries.Count) GoTo(entries[entryIndex + 1]);
    }

    public void Previous()
    {
        if (entryIndex >= entries.Count) return;
        var e = entries[entryIndex];
        if (currentTime - e.Start > 5 || entryIndex == 0) { Seek(e.TrackIndex == trackIndex ? e.Start : 0); return; }
        GoTo(entries[entryIndex - 1]);
    }

    public void SelectChapter(int id)
    {
        if (id >= 0 && id < entries.Count) GoTo(entries[id]);
    }

    private void GoTo(ChapterEntry e)
    {
        if (e.TrackIndex == trackIndex) Seek(e.Start);
        else { PersistPosition(); LoadTrack(e.TrackIndex, e.Start, IsPlaying); }
    }

    // ═══ Speed ═══════════════════════════════════════════════

    private void ApplySpeed(double s)
    {
        Speed = s;
        try { mp.PlaybackSession.PlaybackRate = s; } catch { }
    }

    public void SetSpeed(double s)
    {
        ApplySpeed(s);
        if (Settings.PerBookSpeed && Book is { } b) Mutate(b.Id, st => st.Speed = s);
        else { store.State.Speed = s; store.Save(); }
        RaiseTime();
    }

    public void StepSpeed(int direction)
    {
        if (direction > 0) { var s = Speeds.FirstOrDefault(x => x > Speed + 0.001); if (s > 0) SetSpeed(s); }
        else { var s = Speeds.LastOrDefault(x => x < Speed - 0.001); if (s > 0) SetSpeed(s); }
    }

    public void ChangeVolume(double delta) => Volume += delta;

    // ═══ Events from the media engine ════════════════════════

    private void OnPlaybackState()
    {
        var st = mp.PlaybackSession.PlaybackState;
        bool playing = st == WinPlaybackState.Playing || st == WinPlaybackState.Buffering;
        if (playing && !IsPlaying && Book is { } b) Mutate(b.Id, s => s.LastPlayed = DateTime.UtcNow);
        IsPlaying = playing;
        foreach (var r in rows) r.IsPlaying = r.IsActive && playing;
        UpdateSmtc();
    }

    private void Tick()
    {
        if (isLoading || Book == null) return;
        double pos;
        try { pos = mp.PlaybackSession.Position.TotalSeconds; } catch { return; }
        if (double.IsNaN(pos) || double.IsInfinity(pos)) return;
        currentTime = pos;
        RefreshEntryIndex();

        // "Stop at end of chapter" for chapters that sit inside a longer file
        if (sleepAtChapterEnd && trackIndex == sleepArmedTrack && EntryForTrack() is { } e)
        {
            if (e.Start > sleepArmedStart + 0.01)
            {
                FinishChapterSleep(e.Start);
                return;
            }
            if (Math.Abs(e.Start - sleepArmedStart) < 0.01 && e.End > e.Start &&
                entryIndex + 1 < entries.Count && entries[entryIndex + 1].TrackIndex == trackIndex &&
                currentTime >= e.End - 0.6)
            {
                FinishChapterSleep(entries[entryIndex + 1].Start);
                return;
            }
        }

        if (IsPlaying && (DateTime.UtcNow - lastSave).TotalSeconds > 2) PersistPosition();
        RaiseTime();
    }

    private void ItemEnded()
    {
        if (isLoading || Book is not { } b) return;
        bool hasNext = trackIndex < b.Tracks.Count - 1;
        if (duration > 0) currentTime = duration;
        PersistPosition();
        if (sleepAtChapterEnd)
        {
            SleepAtChapterEnd = false;
            Notify("Goodnight");
            if (hasNext) LoadTrack(trackIndex + 1, 0, play: false);
        }
        else if (hasNext) LoadTrack(trackIndex + 1, 0, play: true);
        else Notify("End of book. Well done");
    }

    // ═══ Persistence ═════════════════════════════════════════

    private void PersistPosition()
    {
        if (Book is not { } b || isLoading) return;
        int idx = trackIndex; double t = currentTime;
        Mutate(b.Id, s => { s.LastTrack = idx; s.Positions[idx] = t; });
        lastSave = DateTime.UtcNow;
        RefreshRowsFor(b.Id);
    }

    public void ResetProgress(Book b)
    {
        Mutate(b.Id, s => { s.Positions.Clear(); s.LastTrack = 0; s.LastPlayed = null; });
        if (b.Id == Book?.Id) LoadTrack(0, 0, play: false);
        RefreshRowsFor(b.Id);
        Notify("Progress reset");
    }

    public void SettingsChanged()
    {
        store.Save();
        Raise(nameof(SkipBackLabel)); Raise(nameof(SkipForwardLabel));
    }

    // ═══ Sleep timer ═════════════════════════════════════════

    public void StartSleep(int minutes)
    {
        CancelSleep();
        sleepDeadline = DateTime.UtcNow.AddMinutes(minutes);
        SleepRemaining = minutes * 60;
        sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        sleepTimer.Tick += (_, _) => TickSleep();
        sleepTimer.Start();
        Notify($"Sleep timer: {minutes} min");
    }

    public void SleepAtEndOfChapter()
    {
        CancelSleep();
        SleepAtChapterEnd = true;
        sleepArmedTrack = trackIndex;
        sleepArmedStart = ChapterStart;
        Notify("Will stop at the end of this chapter");
    }

    public void CancelSleep()
    {
        sleepTimer?.Stop();
        sleepTimer = null;
        SleepRemaining = null;
        SleepAtChapterEnd = false;
    }

    private void FinishChapterSleep(double start)
    {
        SleepAtChapterEnd = false;
        Pause();
        Seek(start);
        Notify("Goodnight");
    }

    private void TickSleep()
    {
        int remaining = (int)Math.Ceiling((sleepDeadline - DateTime.UtcNow).TotalSeconds);
        if (remaining <= 0) { CancelSleep(); FadeOutAndPause(); }
        else SleepRemaining = remaining;
    }

    private async void FadeOutAndPause()
    {
        Notify("Goodnight");
        double original = volume;
        for (int step = 19; step >= 0; step--)
        {
            mp.Volume = original * step / 20.0;
            await Task.Delay(200);
        }
        Pause();
        mp.Volume = original;
    }

    // ═══ Bookmarks ═══════════════════════════════════════════

    public void AddBookmark(string title, string note)
    {
        if (Book is not { } b) return;
        var bm = new Bookmark
        {
            Title = string.IsNullOrWhiteSpace(title) ? Fmt.Time(currentTime) : title,
            Note = note,
            Time = currentTime,
            TrackIndex = trackIndex,
        };
        Mutate(b.Id, s => s.Bookmarks.Insert(0, bm));
        Bookmarks.Insert(0, new BookmarkVM(bm, ChapterNumber(bm.TrackIndex, bm.Time)));
        Raise(nameof(HasNoBookmarks));
        Notify("Bookmark saved");
    }

    public void DeleteBookmark(BookmarkVM vm)
    {
        if (Book is not { } b) return;
        Mutate(b.Id, s => s.Bookmarks.RemoveAll(x => x.Id == vm.Bookmark.Id));
        Bookmarks.Remove(vm);
        Raise(nameof(HasNoBookmarks));
    }

    public void JumpTo(BookmarkVM vm)
    {
        var bm = vm.Bookmark;
        if (bm.TrackIndex == trackIndex) Seek(bm.Time);
        else { PersistPosition(); LoadTrack(bm.TrackIndex, bm.Time, IsPlaying); }
    }

    public string DefaultBookmarkTitle => $"{ChapterTitle} - {Fmt.Time(currentTime)}";

    // ═══ Toast ═══════════════════════════════════════════════

    public async void Notify(string message)
    {
        Toast = message;
        toastCts?.Cancel();
        var cts = toastCts = new CancellationTokenSource();
        try { await Task.Delay(2500, cts.Token); }
        catch (OperationCanceledException) { return; }
        Toast = null;
    }

    // ═══ System media controls (media keys, lock screen) ═════

    private void SetupSmtc()
    {
        try
        {
            smtc = mp.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.IsFastForwardEnabled = true;
            smtc.IsRewindEnabled = true;
            smtc.DisplayUpdater.Type = Windows.Media.MediaPlaybackType.Music;
            smtc.ButtonPressed += (_, args) =>
            {
                var button = args.Button;
                ui.InvokeAsync(() =>
                {
                    switch (button)
                    {
                        case WinSmtcButton.Play: Play(); break;
                        case WinSmtcButton.Pause: Pause(); break;
                        case WinSmtcButton.Next: Next(); break;
                        case WinSmtcButton.Previous: Previous(); break;
                        case WinSmtcButton.FastForward: SkipConfigured(1); break;
                        case WinSmtcButton.Rewind: SkipConfigured(-1); break;
                    }
                });
            };
        }
        catch { smtc = null; }
    }

    private void UpdateSmtc()
    {
        NowPlayingChanged?.Invoke();
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = Book == null ? Windows.Media.MediaPlaybackStatus.Closed
                : IsPlaying ? Windows.Media.MediaPlaybackStatus.Playing : Windows.Media.MediaPlaybackStatus.Paused;
            if (Book is { } b)
            {
                var m = smtc.DisplayUpdater.MusicProperties;
                m.Title = ChapterTitle;
                m.Artist = b.Author;
                m.AlbumTitle = b.Title;
            }
            smtc.DisplayUpdater.Update();
        }
        catch { }
    }

    private async void UpdateSmtcThumbnail(System.Windows.Media.Imaging.BitmapSource image)
    {
        if (smtc == null) return;
        try
        {
            string temp = Path.Combine(Path.GetTempPath(), $"abp_cover_{Guid.NewGuid():N}.png");
            Imaging.SavePng(image, temp);
            var file = await StorageFile.GetFileFromPathAsync(temp);
            smtc.DisplayUpdater.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file);
            smtc.DisplayUpdater.Update();
            if (lastCoverTemp != null) try { System.IO.File.Delete(lastCoverTemp); } catch { }
            lastCoverTemp = temp;
        }
        catch { }
    }
}
