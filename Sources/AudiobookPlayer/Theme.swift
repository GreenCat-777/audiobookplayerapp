import SwiftUI

extension Color {
    static let gold = Color(red: 0.784, green: 0.663, blue: 0.431)
    static let goldLight = Color(red: 0.91, green: 0.79, blue: 0.557)
    static let ink = Color(red: 0.051, green: 0.059, blue: 0.078)
    static let paper = Color(red: 0.94, green: 0.925, blue: 0.878)
}

struct PressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.92 : 1)
            .animation(.spring(duration: 0.2), value: configuration.isPressed)
    }
}

struct IconButton: View {
    let symbol: String
    let help: String
    var size: CGFloat = 20
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size))
                .foregroundStyle(Color.paper.opacity(hover ? 1 : 0.65))
                .frame(width: 42, height: 42)
                .background(Circle().fill(Color.white.opacity(hover ? 0.1 : 0)))
        }
        .buttonStyle(PressStyle())
        .help(help)
        .onHover { hover = $0 }
        .animation(.easeOut(duration: 0.12), value: hover)
    }
}

struct PlayButton: View {
    @EnvironmentObject var p: Player
    var size: CGFloat = 64

    var body: some View {
        Button { p.togglePlay() } label: {
            Image(systemName: p.isPlaying ? "pause.fill" : "play.fill")
                .font(.system(size: size * 0.375))
                .foregroundStyle(Color.ink)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: size, height: size)
                .background(Circle().fill(LinearGradient(colors: [.goldLight, .gold], startPoint: .top, endPoint: .bottom)))
                .shadow(color: .gold.opacity(0.4), radius: size / 4, y: size / 10)
        }
        .buttonStyle(PressStyle())
        .help("Play / Pause  Space")
    }
}

struct SectionLabel: View {
    let text: String
    init(_ t: String) { text = t }
    var body: some View {
        Text(text.uppercased())
            .font(.system(size: 10, weight: .medium)).tracking(1.4).foregroundStyle(.secondary)
    }
}

extension View {
    func pill(active: Bool = false) -> some View {
        self.font(.system(size: 11, design: .monospaced))
            .padding(.horizontal, 12).padding(.vertical, 6)
            .foregroundStyle(active ? Color.goldLight : Color.paper.opacity(0.75))
            .background(Capsule().fill(active ? Color.gold.opacity(0.14) : Color.white.opacity(0.06)))
            .overlay(Capsule().stroke(active ? Color.gold.opacity(0.4) : Color.white.opacity(0.1)))
    }
}

/// Draggable progress bar. Reports a 0…1 fraction.
struct Scrubber: View {
    let fraction: Double
    var thickness: CGFloat = 5
    var thumb: CGFloat = 14
    let onDrag: (Double) -> Void
    let onEnd: (Double) -> Void
    @State private var hover = false

    var body: some View {
        GeometryReader { g in
            let w = max(g.size.width, 1)
            let x = min(max(fraction, 0), 1) * w
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.12)).frame(height: thickness)
                Capsule()
                    .fill(LinearGradient(colors: [Color.gold.opacity(0.55), .gold], startPoint: .leading, endPoint: .trailing))
                    .frame(width: max(x, thickness), height: thickness)
                Circle().fill(Color.gold)
                    .frame(width: thumb, height: thumb)
                    .overlay(Circle().stroke(Color.ink, lineWidth: 2))
                    .shadow(color: .gold.opacity(0.5), radius: 6)
                    .scaleEffect(hover ? 1.25 : 1)
                    .offset(x: x - thumb / 2)
            }
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { onDrag(min(max($0.location.x / w, 0), 1)) }
                .onEnded { onEnd(min(max($0.location.x / w, 0), 1)) })
            .onHover { hover = $0 }
            .animation(.easeOut(duration: 0.12), value: hover)
        }
        .frame(height: 24)
    }
}
