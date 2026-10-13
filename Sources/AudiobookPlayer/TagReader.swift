import AVFoundation
import AppKit

struct Tags {
    var title: String?
    var narrator: String?
    var art: NSImage?
}

/// Reads ID3 / iTunes (MP4) metadata through AVFoundation.
enum TagReader {
    static func read(_ asset: AVAsset) async -> Tags {
        var tags = Tags()
        let common = (try? await asset.load(.commonMetadata)) ?? []

        if let item = AVMetadataItem.metadataItems(from: common, filteredByIdentifier: .commonIdentifierTitle).first,
           let s = try? await item.load(.stringValue) {
            tags.title = s.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        tags.art = await art(in: common)

        // Narrator: ID3 TPE3 ("conductor"), or an iTunes comment that starts with "Narrat…"
        let formats = (try? await asset.load(.availableMetadataFormats)) ?? []
        for format in formats {
            guard let items = try? await asset.loadMetadata(for: format) else { continue }
            if let item = AVMetadataItem.metadataItems(from: items, filteredByIdentifier: .id3MetadataConductor).first,
               let s = try? await item.load(.stringValue), !s.isEmpty {
                tags.narrator = s
            } else if tags.narrator == nil,
                      let item = AVMetadataItem.metadataItems(from: items, filteredByIdentifier: .iTunesMetadataUserComment).first,
                      let s = try? await item.load(.stringValue), s.lowercased().hasPrefix("narrat") {
                tags.narrator = s.replacingOccurrences(of: #"(?i)^narrated by[:\s]*"#, with: "", options: .regularExpression)
            }
        }
        return tags
    }

    /// Cheap artwork-only read, used when filling in sidebar thumbnails.
    static func readArt(_ asset: AVAsset) async -> NSImage? {
        let common = (try? await asset.load(.commonMetadata)) ?? []
        return await art(in: common)
    }

    private static func art(in common: [AVMetadataItem]) async -> NSImage? {
        guard let item = AVMetadataItem.metadataItems(from: common, filteredByIdentifier: .commonIdentifierArtwork).first,
              let data = try? await item.load(.dataValue) else { return nil }
        return Imaging.downsample(data: data, maxPixel: 900)
    }
}

/// Loads a file's duration and any embedded chapter markers (m4b / m4a chapter tracks).
enum OutlineLoader {
    static func load(_ asset: AVURLAsset) async -> TrackInfo {
        let d = (try? await asset.load(.duration))?.seconds ?? 0
        var marks: [ChapterMark] = []

        let groups = (try? await asset.loadChapterMetadataGroups(bestMatchingPreferredLanguages: Locale.preferredLanguages)) ?? []
        for g in groups {
            var title = ""
            if let item = AVMetadataItem.metadataItems(from: g.items, filteredByIdentifier: .commonIdentifierTitle).first,
               let s = try? await item.load(.stringValue) {
                title = s.trimmingCharacters(in: .whitespacesAndNewlines)
            }
            marks.append(ChapterMark(start: g.timeRange.start.seconds, title: title))
        }
        marks.sort { $0.start < $1.start }
        return TrackInfo(duration: d.isFinite ? d : 0, chapters: marks.count > 1 ? marks : [])
    }
}
