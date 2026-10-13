import SwiftUI
import AppKit
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject var p: Player
    @State private var query = ""
    @State private var dropTargeted = false

    var body: some View {
        NavigationSplitView {
            SidebarView(query: $query)
                .navigationSplitViewColumnWidth(min: 250, ideal: 290, max: 380)
                .toolbar {
                    ToolbarItem {
                        Button { p.chooseLibrary() } label: { Label("Open Library Folder", systemImage: "folder.badge.plus") }
                            .help("Open Library Folder (⌘O)")
                    }
                }
        } detail: {
            if p.book != nil { NowPlayingView() } else { WelcomeView() }
        }
        .navigationTitle(p.book?.title ?? "Audiobook Player")
        .navigationSubtitle(p.book?.author ?? "")
        .frame(minWidth: 900, minHeight: 700)
        .preferredColorScheme(.dark)
        .tint(.gold)
        .sheet(isPresented: $p.showingBookmarkSheet) { BookmarkSheet() }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            guard let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                Task { @MainActor in p.openDropped(url) }
            }
            return true
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 14)
                    .strokeBorder(Color.gold, style: StrokeStyle(lineWidth: 3, dash: [10, 6]))
                    .background(Color.gold.opacity(0.08))
                    .overlay(Text("Drop your library folder").font(.system(size: 22, design: .serif).italic()).foregroundStyle(Color.goldLight))
                    .padding(10)
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.15), value: dropTargeted)
        .overlay(alignment: .bottom) {
            if let t = p.toast {
                Text(t)
                    .font(.callout)
                    .padding(.horizontal, 16).padding(.vertical, 9)
                    .background(.regularMaterial, in: Capsule())
                    .overlay(Capsule().stroke(Color.white.opacity(0.1)))
                    .padding(.bottom, 22)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.spring(duration: 0.35), value: p.toast)
    }
}

struct WelcomeView: View {
    @EnvironmentObject var p: Player

    private enum Mode { case noLibrary, scanning, empty, ready }
    private var mode: Mode {
        if p.libraryURL == nil { return .noLibrary }
        if !p.libraryLoaded { return .scanning }
        return p.bookCount == 0 ? .empty : .ready
    }
    private var folderName: String { p.libraryURL?.lastPathComponent ?? "" }

    private var icon: String {
        switch mode {
        case .noLibrary: return "books.vertical"
        case .scanning: return "folder"
        case .empty: return "folder.badge.plus"
        case .ready: return "headphones"
        }
    }
    private var title: String {
        switch mode {
        case .noLibrary: return "Open your library"
        case .scanning: return "Scanning your library"
        case .empty: return "Library added"
        case .ready: return "Pick something to listen to"
        }
    }
    private var message: String {
        switch mode {
        case .noLibrary:
            return "Choose a folder structured as\nAuthor > Book > audio files\n(single .m4b files with chapters work too).\n\nYou can also drag a folder onto this window."
        case .scanning:
            return "Looking through \"\(folderName)\"..."
        case .empty:
            return "\"\(folderName)\" is set as your library but has no books in it yet.\n\nAdd audio as Author > Book > files, then rescan.\nNew books also appear automatically when you come back to this app."
        case .ready:
            return "\(p.bookCount) book\(p.bookCount == 1 ? "" : "s") in \"\(folderName)\".\nChoose one from the sidebar."
        }
    }

    var body: some View {
        ZStack {
            RadialGradient(colors: [Color(white: 0.11), Color.ink], center: .init(x: 0.5, y: 0.4), startRadius: 0, endRadius: 600)
                .ignoresSafeArea()
            VStack(spacing: 18) {
                Image(systemName: icon)
                    .font(.system(size: 60, weight: .ultraLight))
                    .foregroundStyle(LinearGradient(colors: [.goldLight, .gold], startPoint: .top, endPoint: .bottom))
                Text(title)
                    .font(.system(size: 32, design: .serif).italic())
                    .foregroundStyle(Color.paper)
                Text(message)
                    .font(.callout).multilineTextAlignment(.center).foregroundStyle(.secondary).lineSpacing(4)

                switch mode {
                case .noLibrary:
                    Button { p.chooseLibrary() } label: {
                        Label("Open Library Folder...", systemImage: "folder").padding(.horizontal, 10).padding(.vertical, 4)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large).tint(.gold).foregroundStyle(Color.ink)
                    .padding(.top, 6)
                case .empty:
                    HStack(spacing: 12) {
                        Button { p.rescan() } label: {
                            Label("Rescan", systemImage: "arrow.clockwise").padding(.horizontal, 8).padding(.vertical, 4)
                        }
                        .buttonStyle(.borderedProminent).controlSize(.large).tint(.gold).foregroundStyle(Color.ink)
                        Button("Change Folder...") { p.chooseLibrary() }.controlSize(.large)
                        Button("Show in Finder") {
                            if let u = p.libraryURL { NSWorkspace.shared.open(u) }
                        }
                        .controlSize(.large)
                    }
                    .padding(.top, 6)
                case .scanning:
                    ProgressView().controlSize(.small)
                case .ready:
                    EmptyView()
                }
            }
            .padding(30)
        }
    }
}
