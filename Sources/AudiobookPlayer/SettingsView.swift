import SwiftUI
import AppKit

struct SettingsView: View {
    @EnvironmentObject var p: Player
    @AppStorage("skipSeconds") private var skip = 10
    @AppStorage("autoRewind") private var rewind = 5
    @AppStorage("perBookSpeed") private var perBook = true
    @AppStorage("showMenuBar") private var menuBar = true

    var body: some View {
        Form {
            Section("Playback") {
                Picker("Skip buttons", selection: $skip) {
                    ForEach([10, 15, 30], id: \.self) { Text("\($0) seconds").tag($0) }
                }
                Picker("Rewind when resuming", selection: $rewind) {
                    Text("Off").tag(0)
                    ForEach([3, 5, 10, 15, 30], id: \.self) { Text("\($0) seconds").tag($0) }
                }
                Toggle("Remember playback speed per book", isOn: $perBook)
            }
            Section("Interface") {
                Toggle("Show player in the menu bar", isOn: $menuBar)
            }
            Section("Library") {
                LabeledContent("Folder") {
                    Text(p.libraryPath ?? "None chosen")
                        .font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                        .lineLimit(1).truncationMode(.middle)
                }
                HStack {
                    Button("Change…") { p.chooseLibrary() }
                    Button("Rescan") { p.rescan() }
                    Button("Show Data Folder") { NSWorkspace.shared.open(p.dataFolder) }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
    }
}
