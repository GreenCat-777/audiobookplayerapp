import SwiftUI
import AppKit

/// The little player that drops down from the menu bar.
struct MenuBarPlayer: View {
    @EnvironmentObject var p: Player
    @Environment(\.openWindow) private var openWindow
    @AppStorage("skipSeconds") private var skip = 10

    var body: some View {
        VStack(spacing: 14) {
            if let b = p.book {
                HStack(spacing: 12) {
                    Group {
                        if let img = p.cover {
                            Image(nsImage: img).resizable().scaledToFill()
                        } else {
                            ZStack { Color.white.opacity(0.06); Image(systemName: "book.closed").foregroundStyle(.tertiary) }
                        }
                    }
                    .frame(width: 58, height: 58)
                    .clipShape(RoundedRectangle(cornerRadius: 8))

                    VStack(alignment: .leading, spacing: 2) {
                        Text(b.title).font(.system(size: 13, weight: .semibold, design: .serif)).lineLimit(2)
                        Text(b.author).font(.system(size: 11, design: .serif).italic()).foregroundStyle(.secondary).lineLimit(1)
                        Text(p.chapterTitle).font(.system(size: 11)).foregroundStyle(Color.gold).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }

                VStack(spacing: 3) {
                    ProgressView(value: p.overallFraction).tint(.gold)
                    HStack {
                        Text(formatTime(p.chapterElapsed))
                        Spacer()
                        if p.bookDurationKnown {
                            Text("\(formatDuration((p.bookTotal - p.bookElapsed) / max(p.speed, 0.1))) left")
                        } else {
                            Text(formatTime(p.chapterLength))
                        }
                    }
                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                }

                HStack(spacing: 10) {
                    IconButton(symbol: "backward.end.fill", help: "Previous chapter", size: 14) { p.previous() }
                    IconButton(symbol: "gobackward.\(skip)", help: "Back \(skip) seconds", size: 16) { p.skip(-Double(skip)) }
                    PlayButton(size: 48)
                    IconButton(symbol: "goforward.\(skip)", help: "Forward \(skip) seconds", size: 16) { p.skip(Double(skip)) }
                    IconButton(symbol: "forward.end.fill", help: "Next chapter", size: 14) { p.next() }
                }
            } else {
                VStack(spacing: 6) {
                    Image(systemName: "headphones").font(.system(size: 28, weight: .ultraLight)).foregroundStyle(Color.gold)
                    Text("Nothing loaded yet").font(.system(size: 13, design: .serif).italic())
                }
                .padding(.vertical, 8)
            }

            Divider()

            HStack {
                Button("Open Window") {
                    openWindow(id: "main")
                    NSApp.activate(ignoringOtherApps: true)
                }
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
            }
            .buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 310)
        .preferredColorScheme(.dark)
        .tint(.gold)
    }
}
