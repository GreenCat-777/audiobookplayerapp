import SwiftUI
import UniformTypeIdentifiers

struct NowPlayingView: View {
    var body: some View {
        ZStack {
            Backdrop()
            ScrollView {
                VStack(spacing: 26) {
                    CoverView()
                    MetaView()
                    TransportView()
                    SecondaryRow()
                    ChaptersView()
                    BookmarksView()
                }
                .frame(maxWidth: 500)
                .padding(.horizontal, 32).padding(.vertical, 36)
                .frame(maxWidth: .infinity)
            }
            .scrollIndicators(.hidden)
        }
    }
}

struct Backdrop: View {
    @EnvironmentObject var p: Player

    var body: some View {
        Color.ink
            .overlay {
                if let img = p.cover {
                    Image(nsImage: img).resizable().scaledToFill()
                        .blur(radius: 90).saturation(1.4).opacity(0.55)
                }
            }
            .overlay(LinearGradient(colors: [Color.ink.opacity(0.1), Color.ink.opacity(0.78)], startPoint: .top, endPoint: .bottom))
            .clipped()
            .ignoresSafeArea()
            .animation(.easeInOut(duration: 0.7), value: p.book?.id)
    }
}

// MARK: - Cover

struct CoverView: View {
    @EnvironmentObject var p: Player
    @State private var picking = false

    var body: some View {
        ZStack {
            TimelineView(.animation(paused: !p.isPlaying)) { ctx in
                let angle = (ctx.date.timeIntervalSinceReferenceDate * 90).truncatingRemainder(dividingBy: 360)
                RoundedRectangle(cornerRadius: 22)
                    .stroke(AngularGradient(colors: [.clear, .clear, .gold], center: .center, angle: .degrees(angle)), lineWidth: 3)
                    .padding(-4)
                    .opacity(p.isPlaying ? 0.8 : 0)
                    .animation(.easeInOut(duration: 0.5), value: p.isPlaying)
            }
            .frame(width: 250, height: 250)

            Group {
                if let img = p.cover {
                    Image(nsImage: img).resizable().scaledToFill()
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "book.closed").font(.system(size: 46, weight: .ultraLight))
                        Text("No cover art").font(.system(size: 13, design: .serif).italic())
                    }
                    .foregroundStyle(.tertiary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.white.opacity(0.05))
                }
            }
            .frame(width: 250, height: 250)
            .clipShape(RoundedRectangle(cornerRadius: 18))
            .overlay(RoundedRectangle(cornerRadius: 18).stroke(Color.white.opacity(0.12)))
            .shadow(color: .black.opacity(0.55), radius: 30, y: 18)
            .scaleEffect(p.isPlaying ? 1.0 : 0.96)
            .animation(.spring(duration: 0.5, bounce: 0.25), value: p.isPlaying)
            .overlay(alignment: .bottomTrailing) {
                Button { picking = true } label: {
                    Image(systemName: "photo")
                        .font(.system(size: 12))
                        .frame(width: 28, height: 28)
                        .background(.ultraThinMaterial, in: Circle())
                }
                .buttonStyle(.plain).padding(8)
                .help("Change cover art")
            }
        }
        .fileImporter(isPresented: $picking, allowedContentTypes: [.image]) { result in
            if case .success(let url) = result {
                let scoped = url.startAccessingSecurityScopedResource()
                p.setCover(from: url)
                if scoped { url.stopAccessingSecurityScopedResource() }
            }
        }
    }
}

struct MetaView: View {
    @EnvironmentObject var p: Player

    var body: some View {
        VStack(spacing: 4) {
            Text(p.book?.title ?? "")
                .font(.system(size: 28, weight: .medium, design: .serif)).foregroundStyle(Color.paper)
                .multilineTextAlignment(.center)
            Text(p.book?.author ?? "")
                .font(.system(size: 15, design: .serif).italic()).foregroundStyle(Color.paper.opacity(0.7))
            if let n = p.narrator {
                Text("Narrated by \(n)").font(.system(size: 11)).foregroundStyle(.secondary).padding(.top, 1)
            }
            Text(p.chapterTitle)
                .font(.system(size: 14, design: .serif).italic()).foregroundStyle(Color.gold)
                .lineLimit(1).padding(.top, 8)
            Text("Chapter \(p.entryIndex + 1) of \(max(p.entries.count, 1))")
                .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
        }
    }
}

// MARK: - Transport

struct TransportView: View {
    @EnvironmentObject var p: Player
    @AppStorage("skipSeconds") private var skip = 10
    @State private var chapterDrag: Double?
    @State private var bookDrag: Double?

    private var chapterFraction: Double { p.chapterLength > 0 ? min(1, p.chapterElapsed / p.chapterLength) : 0 }
    private var bookFraction: Double { p.bookTotal > 0 ? min(1, p.bookElapsed / p.bookTotal) : 0 }
    private var showBookBar: Bool { p.bookDurationKnown && p.bookTotal > p.chapterLength + 1 }

    var body: some View {
        VStack(spacing: 18) {
            VStack(spacing: 4) {
                HStack {
                    Text(formatTime(chapterDrag.map { $0 * p.chapterLength } ?? p.chapterElapsed))
                    Spacer()
                    Text(formatTime(p.chapterLength))
                }
                .font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                Scrubber(fraction: chapterDrag ?? chapterFraction,
                         onDrag: { chapterDrag = $0 },
                         onEnd: { p.seekInChapter(fraction: $0); chapterDrag = nil })
            }

            HStack(spacing: 6) {
                IconButton(symbol: "backward.end.fill", help: "Previous chapter  [", size: 16) { p.previous() }
                IconButton(symbol: "gobackward.60", help: "Back 1 minute  ⇧←") { p.skip(-60) }
                IconButton(symbol: "gobackward.\(skip)", help: "Back \(skip) seconds  ←") { p.skip(-Double(skip)) }
                PlayButton().padding(.horizontal, 6)
                IconButton(symbol: "goforward.\(skip)", help: "Forward \(skip) seconds  →") { p.skip(Double(skip)) }
                IconButton(symbol: "goforward.60", help: "Forward 1 minute  ⇧→") { p.skip(60) }
                IconButton(symbol: "forward.end.fill", help: "Next chapter  ]", size: 16) { p.next() }
            }

            if showBookBar {
                VStack(spacing: 2) {
                    Scrubber(fraction: bookDrag ?? bookFraction, thickness: 3, thumb: 10,
                             onDrag: { bookDrag = $0 },
                             onEnd: { p.seekBook(fraction: $0); bookDrag = nil })
                    HStack {
                        Text("Book \(Int((bookDrag ?? bookFraction) * 100))%")
                        Spacer()
                        Text("\(formatDuration((p.bookTotal - p.bookElapsed) / max(p.speed, 0.1))) left")
                    }
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                }
            }
        }
    }
}

// MARK: - Speed / volume / sleep

struct SecondaryRow: View {
    @EnvironmentObject var p: Player
    @State private var showSpeed = false

    var body: some View {
        HStack {
            Button { showSpeed.toggle() } label: {
                Text(String(format: "%g×", p.speed)).pill(active: p.speed != 1)
            }
            .buttonStyle(.plain)
            .popover(isPresented: $showSpeed, arrowEdge: .bottom) { SpeedPicker(isShown: $showSpeed) }

            Spacer()

            HStack(spacing: 8) {
                Image(systemName: "speaker.fill").font(.system(size: 10)).foregroundStyle(.secondary)
                Slider(value: $p.volume, in: 0...1).frame(width: 90).controlSize(.small)
                Image(systemName: "speaker.wave.3.fill").font(.system(size: 10)).foregroundStyle(.secondary)
            }

            Spacer()

            Menu {
                Button("Off") { p.cancelSleep() }
                Divider()
                ForEach([5, 10, 15, 30, 45, 60], id: \.self) { m in
                    Button("\(m) minutes") { p.startSleep(minutes: m) }
                }
                Divider()
                Button("End of chapter") { p.sleepAtEndOfChapter() }
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: "moon.zzz.fill")
                    if let r = p.sleepRemaining {
                        Text(String(format: "%d:%02d", r / 60, r % 60))
                    } else if p.sleepAtChapterEnd {
                        Text("end of ch.")
                    } else {
                        Text("Sleep")
                    }
                }
                .pill(active: p.sleepRemaining != nil || p.sleepAtChapterEnd)
            }
            .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        }
    }
}

struct SpeedPicker: View {
    @EnvironmentObject var p: Player
    @Binding var isShown: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Playback speed").font(.system(size: 15, design: .serif).italic())
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(58), spacing: 6), count: 4), spacing: 6) {
                ForEach(Player.speeds, id: \.self) { s in
                    Button { p.setSpeed(s); isShown = false } label: {
                        Text(String(format: "%g×", s)).pill(active: s == p.speed).frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .padding(16)
    }
}

// MARK: - Chapters

struct ChaptersView: View {
    @EnvironmentObject var p: Player

    var body: some View {
        if p.entries.count > 1 {
            VStack(alignment: .leading, spacing: 8) {
                SectionLabel("Chapters · \(p.entries.count)")
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 2) {
                            ForEach(p.entries) { ChapterRow(entry: $0).id($0.id) }
                        }
                    }
                    .frame(maxHeight: 240)
                    .onChange(of: p.entryIndex) { _, new in
                        withAnimation { proxy.scrollTo(new, anchor: .center) }
                    }
                    .onAppear { proxy.scrollTo(p.entryIndex, anchor: .center) }
                }
            }
            .padding(.top, 6)
        }
    }
}

struct ChapterRow: View {
    @EnvironmentObject var p: Player
    let entry: ChapterEntry
    @State private var hover = false

    private var active: Bool { p.entryIndex == entry.id }

    var body: some View {
        Button { p.selectChapter(entry.id) } label: {
            HStack(spacing: 10) {
                Text(String(format: "%02d", entry.id + 1))
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                    .frame(width: 24, alignment: .leading)
                Text(entry.title).font(.system(size: 12.5))
                    .foregroundStyle(active ? Color.gold : Color.paper.opacity(0.85)).lineLimit(1)
                Spacer()
                if entry.end > entry.start {
                    Text(formatDuration(entry.end - entry.start))
                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                }
                if active {
                    Image(systemName: "waveform").font(.system(size: 11)).foregroundStyle(Color.gold)
                        .symbolEffect(.variableColor.iterative, isActive: p.isPlaying)
                } else if entry.id < p.entryIndex {
                    Image(systemName: "checkmark.circle.fill").font(.system(size: 11)).foregroundStyle(Color.gold.opacity(0.5))
                }
            }
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 7)
                .fill(active ? Color.gold.opacity(0.14) : (hover ? Color.white.opacity(0.06) : .clear)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

// MARK: - Bookmarks

struct BookmarksView: View {
    @EnvironmentObject var p: Player

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                SectionLabel("Bookmarks")
                Spacer()
                Button { p.showingBookmarkSheet = true } label: {
                    Label("Add", systemImage: "plus").labelStyle(.titleAndIcon).pill()
                }
                .buttonStyle(.plain).help("Add bookmark  B")
            }
            if p.bookmarks.isEmpty {
                Text("No bookmarks yet.").font(.system(size: 12, design: .serif).italic()).foregroundStyle(.secondary)
            } else {
                VStack(spacing: 6) { ForEach(p.bookmarks) { BookmarkRow(bm: $0) } }
            }
        }
        .padding(.top, 12)
        .overlay(alignment: .top) { Divider().opacity(0.4) }
    }
}

struct BookmarkRow: View {
    @EnvironmentObject var p: Player
    let bm: Bookmark
    @State private var hover = false

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "bookmark.fill").font(.system(size: 12)).foregroundStyle(Color.gold.opacity(0.8))
            VStack(alignment: .leading, spacing: 2) {
                Text(bm.title).font(.system(size: 12.5)).foregroundStyle(Color.paper.opacity(0.9)).lineLimit(1)
                Text("\(formatTime(bm.time)) · ch \(p.chapterNumber(track: bm.trackIndex, time: bm.time))")
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                if !bm.note.isEmpty {
                    Text(bm.note).font(.system(size: 11, design: .serif).italic()).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer()
            Button { p.deleteBookmark(bm) } label: { Image(systemName: "xmark").font(.system(size: 10)) }
                .buttonStyle(.plain).foregroundStyle(.secondary).opacity(hover ? 1 : 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 9).fill(Color.white.opacity(hover ? 0.09 : 0.05)))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(Color.white.opacity(0.08)))
        .contentShape(Rectangle())
        .onTapGesture { p.jump(to: bm) }
        .onHover { hover = $0 }
    }
}

struct BookmarkSheet: View {
    @EnvironmentObject var p: Player
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var note = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Add bookmark").font(.system(size: 20, design: .serif).italic())
            VStack(alignment: .leading, spacing: 4) {
                SectionLabel("Title")
                TextField("Bookmark…", text: $title).textFieldStyle(.roundedBorder)
            }
            VStack(alignment: .leading, spacing: 4) {
                SectionLabel("Note (optional)")
                TextField("Your note…", text: $note, axis: .vertical).lineLimit(3...5).textFieldStyle(.roundedBorder)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    p.addBookmark(title: title.trimmingCharacters(in: .whitespaces), note: note.trimmingCharacters(in: .whitespaces))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).tint(.gold).foregroundStyle(Color.ink)
            }
        }
        .padding(22).frame(width: 360)
        .onAppear { title = "\(p.chapterTitle) — \(formatTime(p.currentTime))" }
    }
}
