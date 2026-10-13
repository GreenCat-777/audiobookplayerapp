import AVFoundation
import AppKit
import Combine
import MediaPlayer

@MainActor
final class Player: ObservableObject {
    // Library
    @Published private(set) var authors: [Author] = []
    @Published private(set) var covers: [String: NSImage] = [:]
    @Published private(set) var libraryURL: URL?
    @Published private(set) var libraryLoaded = false

    // Current book
    @Published private(set) var book: Book?
    @Published private(set) var trackIndex = 0
    @Published private(set) var trackTitle = ""
    @Published private(set) var narrator: String?
    @Published private(set) var bookmarks: [Bookmark] = []
    @Published private(set) var trackInfo: [Int: TrackInfo] = [:]
    @Published private(set) var entries: [ChapterEntry] = []
    @Published private(set) var entryIndex = 0

    // Transport
    @Published private(set) var isPlaying = false
    @Published private(set) var currentTime = 0.0
    @Published private(set) var duration = 0.0
    @Published private(set) var speed = 1.0
    @Published var volume = 1.0 {
        didSet { avPlayer.volume = Float(volume); store.state.volume = volume; store.save() }
    }

    // Sleep timer
    @Published private(set) var sleepRemaining: Int?
    @Published private(set) var sleepAtChapterEnd = false

    // UI
    @Published var showingBookmarkSheet = false
    @Published private(set) var toast: String?

    static let speeds: [Double] = [0.5, 0.6, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5]

    private let avPlayer = AVPlayer()
    private let store = Store()
    private var bag = Set<AnyCancellable>()
    private var timeToken: Any?
    private var loadGeneration = 0
    private var isLoading = false
    private var lastSave = Date.distantPast
    private var pausedAt = Date.distantPast
    private var sleepTimer: Timer?
    private var sleepDeadline: Date?
    private var sleepArmedTrack = 0
    private var sleepArmedStart = 0.0
    private var toastTask: Task<Void, Never>?
    private var outlineTask: Task<Void, Never>?
    private var artTask: Task<Void, Never>?

    // MARK: Derived

    var libraryPath: String? { libraryURL?.path }
    var bookCount: Int { authors.reduce(0) { $0 + $1.books.count } }
    var dataFolder: URL { store.baseDir }
    var cover: NSImage? { book.flatMap { covers[$0.id] } }

    func hasCustomCover(_ b: Book) -> Bool { store.state.books[b.id]?.coverFile != nil }

    var currentEntry: ChapterEntry? { entries.indices.contains(entryIndex) ? entries[entryIndex] : nil }

    private var entryIsCurrentTrack: ChapterEntry? {
        if let e = currentEntry, e.trackIndex == trackIndex { return e }
        return nil
    }

    /// Title to show: embedded chapter name if the file has markers, otherwise the file/ID3 title.
    var chapterTitle: String {
        if let e = entryIsCurrentTrack, e.embedded { return e.title }
        return trackTitle
    }

    var chapterStart: Double { entryIsCurrentTrack?.start ?? 0 }
    var chapterElapsed: Double { max(0, currentTime - chapterStart) }
    var chapterLength: Double {
        if let e = entryIsCurrentTrack, e.end > e.start { return e.end - e.start }
        return max(duration - chapterStart, 0)
    }

    var bookDurationKnown: Bool {
        guard let b = book, !b.tracks.isEmpty else { return false }
        return b.tracks.allSatisfy { (trackInfo[$0.index]?.duration ?? 0) > 0 }
    }
    var bookTotal: Double { trackInfo.values.reduce(0) { $0 + $1.duration } }
    var bookElapsed: Double { trackOffset(trackIndex) + currentTime }
    var overallFraction: Double {
        if bookDurationKnown, bookTotal > 0 { return min(1, bookElapsed / bookTotal) }
        return chapterLength > 0 ? min(1, chapterElapsed / chapterLength) : 0
    }

    private func trackOffset(_ i: Int) -> Double {
        (0..<i).reduce(0) { $0 + (trackInfo[$1]?.duration ?? 0) }
    }

    func chapterNumber(track: Int, time: Double) -> Int {
        var idx = 0
        for e in entries where e.trackIndex == track && e.start <= time + 0.05 { idx = e.id }
        return idx + 1
    }

    /// 0…1 listening progress for any book (live for the current one, cached for the rest).
    func progress(for b: Book) -> Double? {
        if b.id == book?.id, bookDurationKnown, bookTotal > 0 { return min(1, bookElapsed / bookTotal) }
        guard let st = store.state.books[b.id], let d = st.trackDurations, d.count == b.tracks.count else { return nil }
        let total = d.reduce(0, +)
        guard total > 0 else { return nil }
        let idx = max(0, min(st.lastTrack, d.count - 1))
        let elapsed = d.prefix(idx).reduce(0, +) + min(st.positions[idx] ?? 0, d[idx])
        return min(1, elapsed / total)
    }

    var recentBooks: [Book] {
        authors.flatMap(\.books)
            .compactMap { b -> (Book, Date)? in
                guard let d = store.state.books[b.id]?.lastPlayed else { return nil }
                if let p = progress(for: b), p >= 0.99 { return nil }
                return (b, d)
            }
            .sorted { $0.1 > $1.1 }
            .prefix(3)
            .map { $0.0 }
    }

    // MARK: Init

    init() {
        speed = store.state.speed
        volume = store.state.volume
        avPlayer.volume = Float(volume)
        avPlayer.defaultRate = Float(speed)
        avPlayer.actionAtItemEnd = .pause

        avPlayer.publisher(for: \.timeControlStatus)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] status in
                guard let self else { return }
                let playing = status != .paused
                if playing && !self.isPlaying, let b = self.book { self.mutateBook(b.id) { $0.lastPlayed = Date() } }
                self.isPlaying = playing
                self.updateNowPlaying()
            }
            .store(in: &bag)

        timeToken = avPlayer.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main
        ) { [weak self] time in
            Task { @MainActor in self?.tick(time) }
        }

        NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: nil, queue: .main) { [weak self] note in
            let item = note.object as? AVPlayerItem
            Task { @MainActor in self?.itemEnded(item) }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.persistPosition()
                self?.store.flush()
            }
        }

        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.refreshLibrary() }
        }

        setupRemoteCommands()

        if let path = store.state.libraryPath {
            let url = URL(fileURLWithPath: path)
            libraryURL = url
            loadLibrary(url, restoreLast: true)
        }
    }

    // MARK: Library

    func chooseLibrary() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Open Library"
        panel.message = "Choose a folder organised as Author / Book / chapter files"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        setLibrary(url)
    }

    func setLibrary(_ url: URL) {
        store.state.libraryPath = url.path
        store.save()
        libraryURL = url
        libraryLoaded = false
        loadLibrary(url, restoreLast: false)
    }

    func openDropped(_ url: URL) {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
            setLibrary(url)
        } else {
            notify("Drop a library folder, not a single file")
        }
    }

    func rescan() {
        guard let url = libraryURL else { return chooseLibrary() }
        loadLibrary(url, restoreLast: false)
    }

    /// Quiet rescan when you switch back to the app, so new books just appear.
    private func refreshLibrary() {
        guard libraryLoaded, let url = libraryURL else { return }
        loadLibrary(url, restoreLast: false, silent: true)
    }

    private func loadLibrary(_ url: URL, restoreLast: Bool, silent: Bool = false) {
        Task {
            let found = await Task.detached(priority: .userInitiated) { LibraryScanner.scan(url) }.value
            // Folder temporarily gone (unplugged drive)? Keep what we had.
            if silent, found.isEmpty, !authors.isEmpty, !FileManager.default.fileExists(atPath: url.path) { return }
            authors = found
            libraryLoaded = true
            loadCustomCovers()
            let all = found.flatMap(\.books)
            if restoreLast, book == nil,
               let id = store.state.lastBookID, let b = all.first(where: { $0.id == id }) {
                open(b)
            } else if !restoreLast && !silent {
                notify(all.isEmpty ? "Folder added. No books in it yet"
                                   : "\(all.count) book\(all.count == 1 ? "" : "s") found")
            }
            loadArt(for: all)
        }
    }

    private func loadCustomCovers() {
        for (id, st) in store.state.books {
            if let f = st.coverFile,
               let img = Imaging.downsample(url: store.coversDir.appendingPathComponent(f), maxPixel: 1200) {
                covers[id] = img
            }
        }
    }

    /// Fills sidebar thumbnails in the background: folder art first, then embedded tags.
    private func loadArt(for books: [Book]) {
        artTask?.cancel()
        artTask = Task {
            for b in books {
                if Task.isCancelled { return }
                if covers[b.id] != nil { continue }
                var img: NSImage?
                if let url = b.folderArt {
                    img = await Task.detached(priority: .utility) { Imaging.downsample(url: url, maxPixel: 700) }.value
                }
                if img == nil, let first = b.tracks.first {
                    img = await TagReader.readArt(AVURLAsset(url: first.url))
                }
                if let img, covers[b.id] == nil { covers[b.id] = img }
            }
        }
    }

    // MARK: Opening & loading

    func open(_ b: Book) {
        guard b.id != book?.id else { return }
        persistPosition()
        outlineTask?.cancel()
        book = b
        store.state.lastBookID = b.id
        let st = store.state.books[b.id] ?? BookState()
        bookmarks = st.bookmarks

        trackInfo = [:]
        if let d = st.trackDurations, d.count == b.tracks.count {
            for (i, x) in d.enumerated() { trackInfo[i] = TrackInfo(duration: x, chapters: []) }
        }
        entryIndex = 0
        rebuildEntries()

        applySpeed(Prefs.perBookSpeed ? (st.speed ?? 1.0) : store.state.speed)
        pausedAt = .distantPast
        let idx = max(0, min(st.lastTrack, b.tracks.count - 1))
        loadTrack(idx, seek: st.positions[idx] ?? 0, play: false)
        loadOutline(for: b)
        store.save()
    }

    private func loadTrack(_ index: Int, seek: Double, play: Bool) {
        guard let b = book, b.tracks.indices.contains(index) else { return }
        loadGeneration += 1
        let gen = loadGeneration
        isLoading = true
        trackIndex = index
        currentTime = seek
        duration = 0
        trackTitle = b.tracks[index].title
        narrator = nil
        refreshEntryIndex()

        let asset = AVURLAsset(url: b.tracks[index].url)
        let item = AVPlayerItem(asset: asset)
        item.audioTimePitchAlgorithm = .spectral
        avPlayer.replaceCurrentItem(with: item)

        Task {
            let d = (try? await asset.load(.duration))?.seconds ?? 0
            guard gen == loadGeneration else { return }
            duration = d.isFinite ? d : 0
            if seek > 1 {
                let target = CMTime(seconds: min(seek, max(duration - 1, 0)), preferredTimescale: 600)
                _ = await avPlayer.seek(to: target, toleranceBefore: .zero, toleranceAfter: .zero)
            }
            guard gen == loadGeneration else { return }
            isLoading = false
            refreshEntryIndex()
            if play { self.play() }
            updateNowPlaying()

            let tags = await TagReader.read(asset)
            guard gen == loadGeneration else { return }
            if let t = tags.title, !t.isEmpty { trackTitle = t }
            narrator = tags.narrator
            if let art = tags.art, covers[b.id] == nil, store.state.books[b.id]?.coverFile == nil { covers[b.id] = art }
            updateNowPlaying()
        }
    }

    /// Reads durations + embedded chapter markers for every file in the book, in the background.
    private func loadOutline(for b: Book) {
        outlineTask = Task {
            var infos = trackInfo
            for (n, t) in b.tracks.enumerated() {
                if Task.isCancelled || book?.id != b.id { return }
                let info = await OutlineLoader.load(AVURLAsset(url: t.url))
                if Task.isCancelled || book?.id != b.id { return }
                infos[t.index] = info
                if n % 6 == 5 || n == b.tracks.count - 1 {
                    trackInfo = infos
                    rebuildEntries()
                }
            }
            let durations = b.tracks.map { infos[$0.index]?.duration ?? 0 }
            mutateBook(b.id) { $0.trackDurations = durations }
        }
    }

    private func rebuildEntries() {
        guard let b = book else { entries = []; return }
        var out: [ChapterEntry] = []
        for t in b.tracks {
            let info = trackInfo[t.index]
            let dur = info?.duration ?? 0
            if let marks = info?.chapters, marks.count > 1 {
                for (i, m) in marks.enumerated() {
                    let end = i + 1 < marks.count ? marks[i + 1].start : dur
                    out.append(ChapterEntry(id: out.count, trackIndex: t.index, start: m.start, end: end,
                                            title: m.title.isEmpty ? "Chapter \(i + 1)" : m.title, embedded: true))
                }
            } else {
                out.append(ChapterEntry(id: out.count, trackIndex: t.index, start: 0, end: dur, title: t.title, embedded: false))
            }
        }
        entries = out
        refreshEntryIndex()
    }

    private func refreshEntryIndex() {
        var idx = 0
        for e in entries where e.trackIndex == trackIndex && e.start <= currentTime + 0.05 { idx = e.id }
        if entries.indices.contains(idx), entries[idx].trackIndex != trackIndex,
           let first = entries.first(where: { $0.trackIndex == trackIndex }) { idx = first.id }
        if idx != entryIndex { entryIndex = idx }
    }

    // MARK: Transport

    func play() {
        guard book != nil else { return notify("Open a library first") }
        let rewind = Prefs.autoRewind
        if rewind > 0, !isPlaying, currentTime > rewind, Date().timeIntervalSince(pausedAt) > 10 {
            let t = max(currentTime - rewind, 0)
            currentTime = t
            avPlayer.seek(to: CMTime(seconds: t, preferredTimescale: 600))
        }
        avPlayer.play()
    }

    func pause() {
        avPlayer.pause()
        pausedAt = Date()
        persistPosition()
    }

    func togglePlay() { isPlaying ? pause() : play() }

    func seek(to t: Double) {
        guard book != nil else { return }
        let target = max(0, min(t, duration > 0 ? duration : t))
        currentTime = target
        avPlayer.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        refreshEntryIndex()
        persistPosition()
        updateNowPlaying()
    }

    func skip(_ seconds: Double) { seek(to: currentTime + seconds) }

    func seekInChapter(fraction f: Double) { seek(to: chapterStart + f * chapterLength) }

    func seekBook(fraction f: Double) {
        guard let b = book, bookDurationKnown else { return }
        var target = f * bookTotal
        for t in b.tracks {
            let d = trackInfo[t.index]?.duration ?? 0
            if target <= d || t.index == b.tracks.count - 1 {
                target = min(target, d)
                if t.index == trackIndex {
                    seek(to: target)
                } else {
                    persistPosition()
                    loadTrack(t.index, seek: target, play: isPlaying)
                }
                return
            }
            target -= d
        }
    }

    func next() {
        guard entries.indices.contains(entryIndex + 1) else { return }
        goTo(entries[entryIndex + 1])
    }

    func previous() {
        guard let e = currentEntry else { return }
        if currentTime - e.start > 5 || entryIndex == 0 {
            return seek(to: e.trackIndex == trackIndex ? e.start : 0)
        }
        goTo(entries[entryIndex - 1])
    }

    func selectChapter(_ id: Int) {
        guard entries.indices.contains(id) else { return }
        goTo(entries[id])
    }

    private func goTo(_ e: ChapterEntry) {
        if e.trackIndex == trackIndex {
            seek(to: e.start)
        } else {
            persistPosition()
            loadTrack(e.trackIndex, seek: e.start, play: isPlaying)
        }
    }

    // MARK: Speed & volume

    private func applySpeed(_ s: Double) {
        speed = s
        avPlayer.defaultRate = Float(s)
        if isPlaying { avPlayer.rate = Float(s) }
    }

    func setSpeed(_ s: Double) {
        applySpeed(s)
        if Prefs.perBookSpeed, let b = book {
            mutateBook(b.id) { $0.speed = s }
        } else {
            store.state.speed = s
            store.save()
        }
        updateNowPlaying()
    }

    func stepSpeed(_ direction: Int) {
        if direction > 0, let s = Player.speeds.first(where: { $0 > speed + 0.001 }) { setSpeed(s) }
        if direction < 0, let s = Player.speeds.last(where: { $0 < speed - 0.001 }) { setSpeed(s) }
    }

    func changeVolume(by delta: Double) { volume = min(1, max(0, volume + delta)) }

    // MARK: Playback events

    private func tick(_ time: CMTime) {
        guard !isLoading, time.isNumeric else { return }
        currentTime = time.seconds
        refreshEntryIndex()

        // "Stop at end of chapter" for chapters that sit inside a longer file
        if sleepAtChapterEnd, trackIndex == sleepArmedTrack, let e = entryIsCurrentTrack {
            if e.start > sleepArmedStart + 0.01 {
                // we already slipped into the next chapter: go back to its start and stop
                finishChapterSleep(at: e.start)
                return
            }
            if abs(e.start - sleepArmedStart) < 0.01, e.end > e.start,
               entries.indices.contains(entryIndex + 1), entries[entryIndex + 1].trackIndex == trackIndex,
               currentTime >= e.end - 0.6 {
                finishChapterSleep(at: entries[entryIndex + 1].start)
                return
            }
        }
        if isPlaying, Date().timeIntervalSince(lastSave) > 2 { persistPosition() }
    }

    private func itemEnded(_ item: AVPlayerItem?) {
        guard let b = book, b.tracks.indices.contains(trackIndex),
              let url = (item?.asset as? AVURLAsset)?.url, url == b.tracks[trackIndex].url else { return }
        let hasNext = trackIndex < b.tracks.count - 1
        if duration > 0 { currentTime = duration }
        persistPosition()
        if sleepAtChapterEnd {
            sleepAtChapterEnd = false
            notify("Goodnight 💤")
            if hasNext { loadTrack(trackIndex + 1, seek: 0, play: false) }
        } else if hasNext {
            loadTrack(trackIndex + 1, seek: 0, play: true)
        } else {
            notify("End of book. Well done 📖")
        }
    }

    // MARK: Persistence

    private func mutateBook(_ id: String, _ change: (inout BookState) -> Void) {
        var st = store.state.books[id] ?? BookState()
        change(&st)
        store.state.books[id] = st
        store.save()
    }

    private func persistPosition() {
        guard let b = book, !isLoading else { return }
        let idx = trackIndex, t = currentTime
        mutateBook(b.id) { $0.lastTrack = idx; $0.positions[idx] = t }
        lastSave = Date()
    }

    func resetProgress(_ b: Book) {
        mutateBook(b.id) { $0.positions = [:]; $0.lastTrack = 0; $0.lastPlayed = nil }
        if b.id == book?.id { loadTrack(0, seek: 0, play: false) }
        objectWillChange.send()
        notify("Progress reset")
    }

    // MARK: Sleep timer

    func startSleep(minutes: Int) {
        cancelSleep()
        sleepDeadline = Date().addingTimeInterval(Double(minutes) * 60)
        sleepRemaining = minutes * 60
        sleepTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tickSleep() }
        }
        notify("Sleep timer: \(minutes) min")
    }

    func sleepAtEndOfChapter() {
        cancelSleep()
        sleepAtChapterEnd = true
        sleepArmedTrack = trackIndex
        sleepArmedStart = chapterStart
        notify("Will stop at the end of this chapter")
    }

    private func finishChapterSleep(at start: Double) {
        sleepAtChapterEnd = false
        pause()
        seek(to: start)
        notify("Goodnight 💤")
    }

    func cancelSleep() {
        sleepTimer?.invalidate()
        sleepTimer = nil
        sleepDeadline = nil
        sleepRemaining = nil
        sleepAtChapterEnd = false
    }

    private func tickSleep() {
        guard let deadline = sleepDeadline else { return }
        let remaining = Int(deadline.timeIntervalSinceNow.rounded(.up))
        if remaining <= 0 {
            cancelSleep()
            fadeOutAndPause()
        } else {
            sleepRemaining = remaining
        }
    }

    private func fadeOutAndPause() {
        notify("Goodnight 💤")
        let original = avPlayer.volume
        Task {
            for step in stride(from: 19, through: 0, by: -1) {
                avPlayer.volume = original * Float(step) / 20
                try? await Task.sleep(nanoseconds: 200_000_000)
            }
            pause()
            avPlayer.volume = original
        }
    }

    // MARK: Bookmarks

    func addBookmark(title: String, note: String) {
        guard let b = book else { return }
        let bm = Bookmark(title: title.isEmpty ? formatTime(currentTime) : title, note: note,
                          time: currentTime, trackIndex: trackIndex)
        bookmarks.insert(bm, at: 0)
        let all = bookmarks
        mutateBook(b.id) { $0.bookmarks = all }
        notify("Bookmark saved")
    }

    func deleteBookmark(_ bm: Bookmark) {
        guard let b = book else { return }
        bookmarks.removeAll { $0.id == bm.id }
        let all = bookmarks
        mutateBook(b.id) { $0.bookmarks = all }
    }

    func jump(to bm: Bookmark) {
        if bm.trackIndex == trackIndex {
            seek(to: bm.time)
        } else {
            persistPosition()
            loadTrack(bm.trackIndex, seek: bm.time, play: isPlaying)
        }
    }

    // MARK: Cover art

    func setCover(from url: URL) {
        guard let b = book, let img = Imaging.downsample(url: url, maxPixel: 1200),
              let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else { return notify("Couldn't read that image") }
        if let old = store.state.books[b.id]?.coverFile {
            try? FileManager.default.removeItem(at: store.coversDir.appendingPathComponent(old))
        }
        let name = UUID().uuidString + ".png"
        try? png.write(to: store.coversDir.appendingPathComponent(name))
        mutateBook(b.id) { $0.coverFile = name }
        covers[b.id] = img
        updateNowPlaying()
        notify("Cover updated")
    }

    func removeCustomCover(_ b: Book) {
        if let old = store.state.books[b.id]?.coverFile {
            try? FileManager.default.removeItem(at: store.coversDir.appendingPathComponent(old))
        }
        mutateBook(b.id) { $0.coverFile = nil }
        covers[b.id] = nil
        loadArt(for: [b])
        notify("Custom cover removed")
    }

    // MARK: Toast

    func notify(_ message: String) {
        toast = message
        toastTask?.cancel()
        toastTask = Task {
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            if !Task.isCancelled { toast = nil }
        }
    }

    // MARK: Now Playing / media keys

    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        guard let b = book else { center.nowPlayingInfo = nil; return }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: chapterTitle,
            MPMediaItemPropertyArtist: b.author,
            MPMediaItemPropertyAlbumTitle: b.title,
            MPMediaItemPropertyPlaybackDuration: duration,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: currentTime,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? speed : 0.0,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: speed,
        ]
        if let img = covers[b.id] {
            info[MPMediaItemPropertyArtwork] = MPMediaItemArtwork(boundsSize: img.size) { _ in img }
        }
        center.nowPlayingInfo = info
        center.playbackState = isPlaying ? .playing : .paused
    }

    private func setupRemoteCommands() {
        let c = MPRemoteCommandCenter.shared()
        c.playCommand.addTarget { [weak self] _ in Task { @MainActor in self?.play() }; return .success }
        c.pauseCommand.addTarget { [weak self] _ in Task { @MainActor in self?.pause() }; return .success }
        c.togglePlayPauseCommand.addTarget { [weak self] _ in Task { @MainActor in self?.togglePlay() }; return .success }
        c.nextTrackCommand.addTarget { [weak self] _ in Task { @MainActor in self?.next() }; return .success }
        c.previousTrackCommand.addTarget { [weak self] _ in Task { @MainActor in self?.previous() }; return .success }
        c.skipForwardCommand.preferredIntervals = [NSNumber(value: Prefs.skipSeconds)]
        c.skipBackwardCommand.preferredIntervals = [NSNumber(value: Prefs.skipSeconds)]
        c.skipForwardCommand.addTarget { [weak self] _ in Task { @MainActor in self?.skip(Prefs.skipSeconds) }; return .success }
        c.skipBackwardCommand.addTarget { [weak self] _ in Task { @MainActor in self?.skip(-Prefs.skipSeconds) }; return .success }
        c.changePlaybackPositionCommand.addTarget { [weak self] event in
            if let e = event as? MPChangePlaybackPositionCommandEvent {
                let t = e.positionTime
                Task { @MainActor in self?.seek(to: t) }
            }
            return .success
        }
    }
}
