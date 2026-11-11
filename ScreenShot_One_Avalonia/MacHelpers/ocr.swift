// Допоміжна утиліта OCR для macOS (Vision). Збирається в Build-MacApp.ps1 і кладеться в Screenshot_One.app/Contents/MacOS.
// Використання:
//   ocr-helper <шлях до PNG>   → розпізнаний текст у stdout (рядки зверху вниз)
//   ocr-helper --selftest      → малює відомий текст і розпізнає його (перевірка на CI)
// Коди виходу: 0 — успіх (текст може бути порожнім), 1 — неправильні аргументи,
//              2 — не вдалося прочитати зображення, 3 — помилка Vision.
import AppKit
import Foundation
import Vision

func fail(_ message: String, _ code: Int32) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(code)
}

func recognize(_ cgImage: CGImage) throws -> [String] {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = true

    // Українська, англійська, російська — лише ті, що підтримує ця версія macOS
    let preferred = ["uk-UA", "en-US", "ru-RU"]
    if #available(macOS 12.0, *) {
        if let supported = try? request.supportedRecognitionLanguages() {
            let langs = preferred.filter { supported.contains($0) }
            if !langs.isEmpty { request.recognitionLanguages = langs }
        }
    } else {
        request.recognitionLanguages = ["en-US"]
    }
    if #available(macOS 13.0, *) {
        request.automaticallyDetectsLanguage = true
    }

    try VNImageRequestHandler(cgImage: cgImage, options: [:]).perform([request])

    // Vision повертає координати з початком у лівому НИЖНЬОМУ куті — сортуємо зверху вниз, потім зліва направо
    let observations = (request.results ?? []).sorted { a, b in
        let ay = a.boundingBox.midY, by = b.boundingBox.midY
        if abs(ay - by) > 0.01 { return ay > by }
        return a.boundingBox.minX < b.boundingBox.minX
    }
    return observations.compactMap { $0.topCandidates(1).first?.string }
}

func selfTestImage() -> CGImage? {
    let size = NSSize(width: 900, height: 200)
    let image = NSImage(size: size)
    image.lockFocus()
    NSColor.white.setFill()
    NSRect(origin: .zero, size: size).fill()
    let attrs: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 56, weight: .bold),
        .foregroundColor: NSColor.black
    ]
    ("Screenshot One 2026" as NSString).draw(at: NSPoint(x: 40, y: 60), withAttributes: attrs)
    image.unlockFocus()
    return image.cgImage(forProposedRect: nil, context: nil, hints: nil)
}

guard CommandLine.arguments.count >= 2 else { fail("usage: ocr-helper <image> | --selftest", 1) }

let cgImage: CGImage
if CommandLine.arguments[1] == "--selftest" {
    guard let img = selfTestImage() else { fail("cannot render self-test image", 2) }
    cgImage = img
} else {
    guard let image = NSImage(contentsOfFile: CommandLine.arguments[1]),
          let img = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
        fail("cannot read image", 2)
    }
    cgImage = img
}

do {
    print(try recognize(cgImage).joined(separator: "\n"))
} catch {
    fail("vision error: \(error)", 3)
}
