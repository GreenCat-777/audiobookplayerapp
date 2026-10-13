import Foundation
import AppKit
import ImageIO

// MARK: - Library model

struct Track: Identifiable, Hashable {
    let index: Int
    let url: URL
    let title: String
    var id: Int { index }
}

struct Book: Identifiable, Hashable {
    let author: String
    let title: String
    let folder: URL
    let folderArt: URL?
    let tracks: [Track]
    var id: String { "\(author)|||\(title)" }
}

struct Author: Identifiable {
    let name: String
    let books: [Book]
    var id: String { name }
}

// MARK: - Chapters

struct ChapterMark {
    let start: Double
    let title: String
}

/// Per-file info: duration plus any chapter markers embedded in the file (m4b etc.)
struct TrackInfo {
    var duration: Double
    var chapters: [ChapterMark]
}

/// One row in the chapter list. A file without markers is one entry; a file with markers is many.
struct ChapterEntry: Identifiable, Hashable {
    let id: Int
    let trackIndex: Int
    let start: Double
    let end: Double
    let title: String
    let embedded: Bool
}

// MARK: - Persisted state

struct Bookmark: Codable, Identifiable, Hashable {
    var id = UUID()
    var title: String
    var note: String
    var time: Double
    var trackIndex: Int
}

struct BookState: Codable {
    var lastTrack = 0
    var positions: [Int: Double] = [:]
    var bookmarks: [Bookmark] = []
    var coverFile: String?
    var speed: Double?
    var lastPlayed: Date?
    var trackDurations: [Double]?
}

struct AppState: Codable {
    var libraryPath: String?
    var lastBookID: String?
    var speed = 1.0
    var volume = 1.0
    var books: [String: BookState] = [:]
}

// MARK: - Preferences (backed by UserDefaults, edited in Settings)

enum Prefs {
    static var skipSeconds: Double {
        let v = UserDefaults.standard.integer(forKey: "skipSeconds")
        return v == 0 ? 10 : Double(v)
    }
    static var autoRewind: Double {
        UserDefaults.standard.object(forKey: "autoRewind") == nil ? 5 : Double(UserDefaults.standard.integer(forKey: "autoRewind"))
    }
    static var perBookSpeed: Bool {
        UserDefaults.standard.object(forKey: "perBookSpeed") == nil ? true : UserDefaults.standard.bool(forKey: "perBookSpeed")
    }
}

// MARK: - Formatting

func formatTime(_ s: Double) -> String {
    guard s.isFinite, s > 0 else { return "0:00:00" }
    let t = Int(s)
    return String(format: "%d:%02d:%02d", t / 3600, (t % 3600) / 60, t % 60)
}

func formatDuration(_ s: Double) -> String {
    guard s.isFinite else { return "–" }
    let t = Int(max(s, 0)), h = t / 3600, m = (t % 3600) / 60
    if h > 0 { return "\(h)h \(m)m" }
    if m > 0 { return "\(m)m" }
    return "<1m"
}

// MARK: - Image helpers

enum Imaging {
    static func downsample(data: Data, maxPixel: Int) -> NSImage? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return thumbnail(src, maxPixel)
    }

    static func downsample(url: URL, maxPixel: Int) -> NSImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return thumbnail(src, maxPixel)
    }

    private static func thumbnail(_ src: CGImageSource, _ maxPixel: Int) -> NSImage? {
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
    }
}

// MARK: - Library scanning

/// Understands:
///   Library/Author/Book/01.mp3            (the classic layout)
///   Library/Author/Book/CD1/01.mp3        (multi-disc, searched recursively)
///   Library/Author/Book.m4b               (single-file books)
///   Library/Book/01.mp3                   (no author level, filed under "Unsorted")
enum LibraryScanner {
    static let audioExtensions: Set<String> = ["mp3", "m4a", "m4b", "aac", "flac", "wav", "aiff", "caf"]
    private static let artNames = ["cover", "folder", "front", "albumart", "artwork", "book"]
    private static let artExtensions = ["jpg", "jpeg", "png"]

    static func scan(_ root: URL) -> [Author] {
        let fm = FileManager.default

        func natural(_ a: URL, _ b: URL) -> Bool { a.path.localizedStandardCompare(b.path) == .orderedAscending }
        func isAudio(_ u: URL) -> Bool { audioExtensions.contains(u.pathExtension.lowercased()) }
        func isDir(_ u: URL) -> Bool { (try? u.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true }
        func children(_ u: URL) -> [URL] {
            (try? fm.contentsOfDirectory(at: u, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        }
        func audioFiles(in folder: URL) -> [URL] {
            guard let en = fm.enumerator(at: folder, includingPropertiesForKeys: nil,
                                         options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { return [] }
            var out: [URL] = []
            for case let u as URL in en where isAudio(u) { out.append(u) }
            return out.sorted(by: natural)
        }
        func folderArt(_ folder: URL) -> URL? {
            let items = children(folder)
            for name in artNames {
                for ext in artExtensions {
                    if let hit = items.first(where: {
                        $0.deletingPathExtension().lastPathComponent.lowercased() == name && $0.pathExtension.lowercased() == ext
                    }) { return hit }
                }
            }
            return nil
        }
        func makeBook(author: String, title: String, files: [URL], folder: URL) -> Book {
            let tracks = files.enumerated().map { Track(index: $0.offset, url: $0.element, title: cleanTitle($0.element)) }
            return Book(author: author, title: title, folder: folder, folderArt: folderArt(folder), tracks: tracks)
        }

        var byAuthor: [String: [Book]] = [:]
        func add(_ b: Book) { byAuthor[b.author, default: []].append(b) }

        for item in children(root).sorted(by: natural) {
            if isDir(item) {
                let kids = children(item)
                var foundBooks = false
                for d in kids.filter(isDir).sorted(by: natural) {
                    let files = audioFiles(in: d)
                    if !files.isEmpty {
                        add(makeBook(author: item.lastPathComponent, title: d.lastPathComponent, files: files, folder: d))
                        foundBooks = true
                    }
                }
                let loose = kids.filter(isAudio).sorted(by: natural)
                if !loose.isEmpty {
                    let allM4B = loose.allSatisfy { $0.pathExtension.lowercased() == "m4b" }
                    if foundBooks || allM4B {
                        for f in loose {
                            add(makeBook(author: item.lastPathComponent, title: f.deletingPathExtension().lastPathComponent, files: [f], folder: item))
                        }
                    } else {
                        add(makeBook(author: "Unsorted", title: item.lastPathComponent, files: loose, folder: item))
                    }
                }
            } else if isAudio(item) {
                add(makeBook(author: "Unsorted", title: item.deletingPathExtension().lastPathComponent, files: [item], folder: root))
            }
        }

        return byAuthor.keys
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
            .map { name in
                Author(name: name, books: (byAuthor[name] ?? []).sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending })
            }
    }

    private static func cleanTitle(_ url: URL) -> String {
        url.deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: #"^\d+[\s\-\.]+"#, with: "", options: .regularExpression)
    }
}

// MARK: - Persistence (JSON in Application Support)

final class Store {
    var state = AppState()
    let baseDir: URL
    let coversDir: URL
    private let file: URL
    private var pending: DispatchWorkItem?

    init() {
        baseDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Audiobook Player", isDirectory: true)
        coversDir = baseDir.appendingPathComponent("Covers", isDirectory: true)
        try? FileManager.default.createDirectory(at: coversDir, withIntermediateDirectories: true)
        file = baseDir.appendingPathComponent("state.json")
        if let data = try? Data(contentsOf: file), let s = try? JSONDecoder().decode(AppState.self, from: data) {
            state = s
        }
    }

    /// Debounced save (1 s).
    func save() {
        pending?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.flush() }
        pending = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: work)
    }

    func flush() {
        pending?.cancel()
        if let data = try? JSONEncoder().encode(state) {
            try? data.write(to: file, options: .atomic)
        }
    }
}
