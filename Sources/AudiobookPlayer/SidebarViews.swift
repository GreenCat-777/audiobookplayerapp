import SwiftUI
import AppKit

struct SidebarView: View {
    @EnvironmentObject var p: Player
    @Binding var query: String

    private var filtered: [Author] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return p.authors }
        return p.authors.compactMap { a in
            if a.name.lowercased().contains(q) { return a }
            let books = a.books.filter { $0.title.lowercased().contains(q) }
            return books.isEmpty ? nil : Author(name: a.name, books: books)
        }
    }

    var body: some View {
        List {
            if query.isEmpty {
                let recent = p.recentBooks
                if !recent.isEmpty {
                    Section("Continue listening") {
                        ForEach(recent) { BookRow(book: $0, showAuthor: true) }
                    }
                }
            }
            ForEach(filtered) { author in
                Section(author.name) {
                    ForEach(author.books) { BookRow(book: $0, showAuthor: false) }
                }
            }
        }
        .listStyle(.sidebar)
        .searchable(text: $query, placement: .sidebar, prompt: "Search library")
        .overlay {
            if p.authors.isEmpty { EmptyLibraryHint() }
        }
        .safeAreaInset(edge: .bottom) {
            if p.libraryURL != nil { LibraryFooter() }
        }
    }
}

/// Shown in the sidebar when there are no books (yet).
struct EmptyLibraryHint: View {
    @EnvironmentObject var p: Player

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: p.libraryURL == nil ? "books.vertical" : "tray")
                .font(.system(size: 28, weight: .ultraLight)).foregroundStyle(.tertiary)
            if let url = p.libraryURL {
                if p.libraryLoaded {
                    Text("No books in \"\(url.lastPathComponent)\" yet.\nAdd audio as Author / Book / 01 - track.mp3,\nthen rescan (⌘R).")
                } else {
                    Text("Scanning...")
                }
            } else {
                Text("Choose a folder organised as\nAuthor / Book / 01 - track.mp3")
            }
        }
        .font(.caption).multilineTextAlignment(.center).foregroundStyle(.secondary)
        .padding()
    }
}

/// Always-visible strip showing which folder is your library.
struct LibraryFooter: View {
    @EnvironmentObject var p: Player

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "folder.fill").foregroundStyle(Color.gold)
            VStack(alignment: .leading, spacing: 1) {
                Text(p.libraryURL?.lastPathComponent ?? "")
                    .font(.system(size: 12, weight: .medium)).lineLimit(1)
                Text(p.libraryLoaded ? "\(p.bookCount) book\(p.bookCount == 1 ? "" : "s")" : "Scanning...")
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            Button { p.rescan() } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.plain).foregroundStyle(.secondary).help("Rescan library (⌘R)")
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) { Divider().opacity(0.5) }
    }
}

struct BookRow: View {
    @EnvironmentObject var p: Player
    let book: Book
    let showAuthor: Bool
    @State private var hover = false

    private var active: Bool { p.book?.id == book.id }

    private var subtitle: String {
        if showAuthor { return book.author }
        if let prog = p.progress(for: book) {
            if prog >= 0.99 { return "Finished ✓" }
            if prog > 0.005 { return "\(Int(prog * 100))% · \(book.tracks.count == 1 ? "1 file" : "\(book.tracks.count) chapters")" }
        }
        return book.tracks.count == 1 ? "1 file" : "\(book.tracks.count) chapters"
    }

    var body: some View {
        Button { p.open(book) } label: {
            HStack(spacing: 10) {
                Group {
                    if let img = p.covers[book.id] {
                        Image(nsImage: img).resizable().scaledToFill()
                    } else {
                        ZStack {
                            Color.white.opacity(0.06)
                            Image(systemName: "book.closed.fill").font(.system(size: 13)).foregroundStyle(.tertiary)
                        }
                    }
                }
                .frame(width: 34, height: 44)
                .clipShape(RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.white.opacity(0.1)))

                VStack(alignment: .leading, spacing: 2) {
                    Text(book.title)
                        .font(.system(size: 12.5, weight: active ? .semibold : .regular))
                        .foregroundStyle(active ? Color.gold : Color.primary).lineLimit(2)
                    Text(subtitle).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                    if let prog = p.progress(for: book), prog > 0.005, prog < 0.99 {
                        GeometryReader { g in
                            ZStack(alignment: .leading) {
                                Capsule().fill(Color.white.opacity(0.1))
                                Capsule().fill(Color.gold.opacity(0.85)).frame(width: g.size.width * prog)
                            }
                        }
                        .frame(height: 2).padding(.top, 2)
                    }
                }
                Spacer(minLength: 0)
                if active && p.isPlaying {
                    Image(systemName: "waveform").foregroundStyle(Color.gold)
                        .symbolEffect(.variableColor.iterative, isActive: true)
                }
            }
            .padding(.vertical, 3).padding(.horizontal, 6)
            .background(RoundedRectangle(cornerRadius: 8)
                .fill(active ? Color.gold.opacity(0.14) : (hover ? Color.white.opacity(0.06) : .clear)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .contextMenu {
            Button("Play") { p.open(book); p.play() }
            Button("Reveal in Finder") { NSWorkspace.shared.activateFileViewerSelecting([book.tracks[0].url]) }
            Divider()
            if p.hasCustomCover(book) { Button("Remove Custom Cover") { p.removeCustomCover(book) } }
            Button("Reset Progress") { p.resetProgress(book) }
        }
    }
}
