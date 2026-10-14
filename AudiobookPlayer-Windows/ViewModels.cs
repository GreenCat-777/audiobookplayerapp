using System.Windows.Media;

namespace AudiobookPlayer;

public sealed class SectionHeaderVM
{
    public string Title { get; }
    public SectionHeaderVM(string title) => Title = title;
}

public sealed class BookRowVM : ObservableObject
{
    public Book Book { get; }
    public bool ShowAuthor { get; }
    public string Title => Book.Title;

    private ImageSource? cover;
    public ImageSource? Cover { get => cover; set => Set(ref cover, value); }

    private string subtitle = "";
    public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }

    private double progress;
    public double Progress { get => progress; set => Set(ref progress, value); }

    private bool showProgress;
    public bool ShowProgress { get => showProgress; set => Set(ref showProgress, value); }

    private bool isActive;
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }

    private bool isPlaying;
    public bool IsPlaying { get => isPlaying; set => Set(ref isPlaying, value); }

    private bool hasCustomCover;
    public bool HasCustomCover { get => hasCustomCover; set => Set(ref hasCustomCover, value); }

    public BookRowVM(Book book, bool showAuthor)
    {
        Book = book;
        ShowAuthor = showAuthor;
    }
}

public sealed class ChapterRowVM : ObservableObject
{
    public int Id { get; }
    public string NumberText => $"{Id + 1:00}";

    private string title = "";
    public string Title { get => title; set => Set(ref title, value); }

    private string durationText = "";
    public string DurationText { get => durationText; set => Set(ref durationText, value); }

    private bool isActive;
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }

    private bool isDone;
    public bool IsDone { get => isDone; set => Set(ref isDone, value); }

    public ChapterRowVM(int id) => Id = id;
}

public sealed class BookmarkVM
{
    public Bookmark Bookmark { get; }
    public string Title => Bookmark.Title;
    public string Note => Bookmark.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Bookmark.Note);
    public string Meta { get; }

    public BookmarkVM(Bookmark b, int chapterNumber)
    {
        Bookmark = b;
        Meta = $"{Fmt.Time(b.Time)}  ·  ch {chapterNumber}";
    }
}
