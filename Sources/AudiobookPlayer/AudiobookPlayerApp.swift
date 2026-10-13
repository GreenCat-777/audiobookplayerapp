import SwiftUI
import AppKit

@main
struct AudiobookPlayerApp: App {
    @StateObject private var player = Player()
    @AppStorage("showMenuBar") private var showMenuBar = true

    var body: some Scene {
        Window("Audiobook Player", id: "main") {
            ContentView()
                .environmentObject(player)
                .onAppear { KeyMonitor.install(player) }
        }
        .defaultSize(width: 1000, height: 780)
        .commands { AppCommands(player: player) }

        MenuBarExtra("Audiobook Player",
                     systemImage: player.isPlaying ? "headphones.circle.fill" : "headphones",
                     isInserted: $showMenuBar) {
            MenuBarPlayer().environmentObject(player)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView().environmentObject(player)
        }
    }
}

struct AppCommands: Commands {
    @ObservedObject var player: Player

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("Open Library Folder…") { player.chooseLibrary() }.keyboardShortcut("o")
            Button("Rescan Library") { player.rescan() }.keyboardShortcut("r")
        }
        CommandMenu("Playback") {
            Button(player.isPlaying ? "Pause" : "Play") { player.togglePlay() }
            Divider()
            Button("Next Chapter") { player.next() }.keyboardShortcut(.rightArrow, modifiers: .command)
            Button("Previous Chapter") { player.previous() }.keyboardShortcut(.leftArrow, modifiers: .command)
            Button("Skip Forward") { player.skip(Prefs.skipSeconds) }
            Button("Skip Back") { player.skip(-Prefs.skipSeconds) }
            Button("Forward 1 Minute") { player.skip(60) }.keyboardShortcut(.rightArrow, modifiers: [.command, .option])
            Button("Back 1 Minute") { player.skip(-60) }.keyboardShortcut(.leftArrow, modifiers: [.command, .option])
            Divider()
            Button("Faster") { player.stepSpeed(1) }.keyboardShortcut("=", modifiers: .command)
            Button("Slower") { player.stepSpeed(-1) }.keyboardShortcut("-", modifiers: .command)
            Button("Volume Up") { player.changeVolume(by: 0.1) }.keyboardShortcut(.upArrow, modifiers: .command)
            Button("Volume Down") { player.changeVolume(by: -0.1) }.keyboardShortcut(.downArrow, modifiers: .command)
            Divider()
            Menu("Sleep Timer") {
                Button("Off") { player.cancelSleep() }
                ForEach([5, 10, 15, 30, 45, 60], id: \.self) { m in
                    Button("\(m) minutes") { player.startSleep(minutes: m) }
                }
                Button("End of Chapter") { player.sleepAtEndOfChapter() }
            }
            Divider()
            Button("Add Bookmark…") { player.showingBookmarkSheet = true }.keyboardShortcut("d")
        }
    }
}

/// Plain-key shortcuts (Space, ←/→, [ ], B) that never fight with text fields or sheets.
@MainActor
enum KeyMonitor {
    private static var monitor: Any?

    static func install(_ p: Player) {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            guard let w = NSApp.keyWindow, w.sheetParent == nil, !(w.firstResponder is NSText), p.book != nil,
                  e.modifierFlags.intersection([.command, .control, .option]).isEmpty else { return e }
            let shift = e.modifierFlags.contains(.shift)
            switch e.keyCode {
            case 49:  p.togglePlay()                                          // space
            case 123: p.skip(shift ? -60 : -Prefs.skipSeconds)                // ←
            case 124: p.skip(shift ? 60 : Prefs.skipSeconds)                  // →
            case 33:  p.previous()                                            // [
            case 30:  p.next()                                                // ]
            case 11:  p.showingBookmarkSheet = true                           // B
            default:  return e
            }
            return nil
        }
    }
}
