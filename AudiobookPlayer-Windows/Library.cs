using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AudiobookPlayer;

/// Understands:
///   Library\Author\Book\01.mp3          (the classic layout)
///   Library\Author\Book\CD1\01.mp3      (multi-disc, searched recursively)
///   Library\Author\Book.m4b             (single-file books)
///   Library\Book\01.mp3                 (no author level, filed under "Unsorted")
public static class LibraryScanner
{
    public static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".wma" };

    private static readonly string[] ArtNames = { "cover", "folder", "front", "albumart", "artwork", "book" };
    private static readonly string[] ArtExtensions = { ".jpg", ".jpeg", ".png" };
    private static readonly Regex LeadingNumber = new(@"^\d+[\s\-\.]+", RegexOptions.Compiled);

    private static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    private static string[] Dirs(string path)
    {
        try
        {
            var a = Directory.GetDirectories(path, "*", new EnumerationOptions());
            Array.Sort(a, NaturalComparer.Instance);
            return a;
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] LooseAudio(string path)
    {
        try
        {
            var a = Directory.GetFiles(path, "*", new EnumerationOptions()).Where(IsAudio).ToArray();
            Array.Sort(a, NaturalComparer.Instance);
            return a;
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] AudioRecursive(string path)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var a = Directory.GetFiles(path, "*", opts).Where(IsAudio).ToArray();
            Array.Sort(a, NaturalComparer.Instance);
            return a;
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? FolderArt(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "*", new EnumerationOptions());
            foreach (var name in ArtNames)
                foreach (var ext in ArtExtensions)
                {
                    var hit = files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit;
                }
        }
        catch { }
        return null;
    }

    private static Book MakeBook(string author, string title, string[] files, string folder)
    {
        var tracks = files.Select((f, i) => new Track(i, f, CleanTitle(f))).ToList();
        var book = new Book(author, title, folder, FolderArt(folder), tracks)
        {
            Id = $"{author}|||{title}",
        };
        return book;
    }

    /// First book keeps author|||title so saved progress still matches. Later collisions get the folder path.
    internal static void DisambiguateIds(IReadOnlyList<Book> books)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var seenBase = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            string baseId = $"{book.Author}|||{book.Title}";
            if (seenBase.Add(baseId))
            {
                book.Id = baseId;
                used.Add(baseId);
                continue;
            }

            string folder = book.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string id = $"{baseId}|||{folder}";
            int n = 2;
            while (!used.Add(id))
                id = $"{baseId}|||{folder}|||{n++}";
            book.Id = id;
        }
    }

    private static string CleanTitle(string file)
        => LeadingNumber.Replace(Path.GetFileNameWithoutExtension(file), "");

    public static List<AuthorGroup> Scan(string root)
    {
        var byAuthor = new Dictionary<string, List<Book>>();
        void Add(Book b)
        {
            if (!byAuthor.TryGetValue(b.Author, out var list)) byAuthor[b.Author] = list = new List<Book>();
            list.Add(b);
        }

        if (!Directory.Exists(root)) return new List<AuthorGroup>();

        foreach (var item in Dirs(root))
        {
            string name = Path.GetFileName(item);
            bool foundBooks = false;
            foreach (var d in Dirs(item))
            {
                var files = AudioRecursive(d);
                if (files.Length == 0) continue;
                Add(MakeBook(name, Path.GetFileName(d), files, d));
                foundBooks = true;
            }

            var loose = LooseAudio(item);
            if (loose.Length > 0)
            {
                bool allM4b = loose.All(f => string.Equals(Path.GetExtension(f), ".m4b", StringComparison.OrdinalIgnoreCase));
                if (foundBooks || allM4b)
                    foreach (var f in loose) Add(MakeBook(name, Path.GetFileNameWithoutExtension(f), new[] { f }, item));
                else
                    Add(MakeBook("Unsorted", name, loose, item));
            }
        }

        foreach (var f in LooseAudio(root))
            Add(MakeBook("Unsorted", Path.GetFileNameWithoutExtension(f), new[] { f }, root));

        DisambiguateIds(byAuthor.Values.SelectMany(list => list).ToList());

        return byAuthor.Keys
            .OrderBy(k => k, NaturalComparer.Instance)
            .Select(k => new AuthorGroup(k, byAuthor[k].OrderBy(b => b.Title, NaturalComparer.Instance).ToList()))
            .ToList();
    }

    /// True when two scans describe the same library (used to skip pointless UI refreshes).
    public static bool Same(IReadOnlyList<AuthorGroup> a, IReadOnlyList<AuthorGroup> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name || a[i].Books.Count != b[i].Books.Count) return false;
            for (int j = 0; j < a[i].Books.Count; j++)
                if (a[i].Books[j].Id != b[i].Books[j].Id || a[i].Books[j].Tracks.Count != b[i].Books[j].Tracks.Count) return false;
        }
        return true;
    }
}
