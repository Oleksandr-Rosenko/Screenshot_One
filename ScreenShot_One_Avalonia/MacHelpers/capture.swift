// Допоміжна утиліта знімка екрана для macOS 14+ на ScreenCaptureKit (SCShareableContent + SCScreenshotManager).
// Замінює застарілий CGDisplayCreateImage. Збирається в Build-MacApp.ps1 і кладеться в
// Screenshot_One.app/Contents/MacOS/capture-helper. Програма запускає її як процес (MacScreenCaptureKit.cs);
// якщо утиліти немає або вона не спрацювала — CGDisplayCreateImage, а далі /usr/sbin/screencapture.
//
// Використання:
//   capture-helper --display N [вихід]      дисплей за індексом: 0 — головний, решта зліва направо, згори вниз
//   capture-helper --all [вихід]            усі дисплеї в одній картинці
//   capture-helper --rect X,Y,W,H [вихід]   прямокутник у ТОЧКАХ глобальних координат
//                                           (початок — лівий верхній кут головного екрана, як у CGDisplayBounds)
//   capture-helper --list                   дисплеї: індекс, id, x, y, ширина, висота (точки), масштаб
//   capture-helper --selftest [--out файл]  знімок головного дисплея й перевірка, що картинка не порожня
// Вихід:
//   --out <файл.png>   PNG у файл;  --out -   PNG у stdout (за замовчуванням)
//   --raw              сирі пікселі в stdout (так читає програма, без PNG «туди й назад»):
//                      "SOC1", ширина, висота, stride (UInt32 LE), масштаб (Float64 LE), далі BGRA premultiplied, sRGB
// Інше:
//   --scale S          масштаб результату (пікселі на точку); за замовчуванням — найбільший серед задіяних дисплеїв
//   --cursor           показувати курсор
//   --timeout СЕК      аварійний тайм-аут (за замовчуванням 15)
// Коди виходу: 0 — успіх, 1 — неправильні аргументи, 2 — знімок не вдався, 3 — не вдалося записати результат,
//              4 — немає дозволу «Запис екрана», 5 — тайм-аут.
import CoreGraphics
import CoreVideo
import Foundation
import ImageIO
import ScreenCaptureKit
import UniformTypeIdentifiers

enum ExitCode: Int32 {
    case ok = 0, badArgs = 1, captureFailed = 2, writeFailed = 3, noPermission = 4, timeout = 5
}

struct HelperError: Error {
    let message: String
    let code: ExitCode
}

func fail(_ message: String, _ code: ExitCode) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(code.rawValue)
}

let usage = "usage: capture-helper (--display N | --all | --rect X,Y,W,H | --list | --selftest) [--out file.png | --out - | --raw] [--scale S] [--cursor] [--timeout SEC]"

// ---- Аргументи ----

enum Mode {
    case display(Int), all, rect(CGRect), list, selftest
}

struct Options {
    var mode: Mode?
    var out = "-"
    var outGiven = false
    var raw = false
    var scale: CGFloat?
    var cursor = false
    var timeout: Double = 15
}

func parseArgs(_ args: [String]) -> Options {
    var o = Options()
    var i = 0
    func value(_ name: String) -> String {
        i += 1
        guard i < args.count else { fail("missing value for \(name)\n\(usage)", .badArgs) }
        return args[i]
    }
    while i < args.count {
        let arg = args[i]
        switch arg {
        case "--display":
            guard let n = Int(value(arg)), n >= 0 else { fail("--display expects a non-negative index", .badArgs) }
            o.mode = .display(n)
        case "--all":
            o.mode = .all
        case "--rect":
            let parts = value(arg).split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
            guard parts.count == 4, parts[2] > 0, parts[3] > 0 else { fail("--rect expects X,Y,W,H (points, W and H > 0)", .badArgs) }
            o.mode = .rect(CGRect(x: parts[0], y: parts[1], width: parts[2], height: parts[3]))
        case "--list":
            o.mode = .list
        case "--selftest":
            o.mode = .selftest
        case "--out":
            o.out = value(arg)
            o.outGiven = true
        case "--raw":
            o.raw = true
        case "--scale":
            guard let s = Double(value(arg)), s > 0, s <= 8 else { fail("--scale expects a number in (0, 8]", .badArgs) }
            o.scale = CGFloat(s)
        case "--cursor":
            o.cursor = true
        case "--timeout":
            guard let t = Double(value(arg)), t > 0 else { fail("--timeout expects seconds > 0", .badArgs) }
            o.timeout = t
        default:
            fail("unknown argument: \(arg)\n\(usage)", .badArgs)
        }
        i += 1
    }
    if o.mode == nil { fail(usage, .badArgs) }
    if o.raw && o.outGiven { fail("--raw and --out cannot be used together", .badArgs) }
    return o
}

// ---- Дисплеї ----

struct Display {
    let sc: SCDisplay
    let frame: CGRect   // точки, глобальні координати з початком у лівому верхньому куті головного екрана
    let scale: CGFloat  // пікселі на точку (Retina = 2)
}

func nativeScale(of display: SCDisplay) -> CGFloat {
    let filter = SCContentFilter(display: display, excludingWindows: [])
    let s = CGFloat(filter.pointPixelScale)
    if s > 0 { return s }
    // Запасний шлях: фізична ширина режиму / ширина в точках
    if let mode = CGDisplayCopyDisplayMode(display.displayID), mode.width > 0 {
        return CGFloat(mode.pixelWidth) / CGFloat(mode.width)
    }
    return 1
}

func loadDisplays() async throws -> [Display] {
    let content: SCShareableContent
    do {
        content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    } catch {
        throw mapCaptureError(error, "cannot list displays")
    }
    let mainID = CGMainDisplayID()
    let displays = content.displays.map { Display(sc: $0, frame: $0.frame, scale: nativeScale(of: $0)) }
    // Стабільний порядок: головний дисплей першим, далі зліва направо, згори вниз
    return displays.sorted { a, b in
        let am = a.sc.displayID == mainID, bm = b.sc.displayID == mainID
        if am != bm { return am }
        if a.frame.minX != b.frame.minX { return a.frame.minX < b.frame.minX }
        return a.frame.minY < b.frame.minY
    }
}

// ---- Знімок ----

func mapCaptureError(_ error: Error, _ context: String) -> HelperError {
    if let e = error as? HelperError { return e }
    let ns = error as NSError
    // SCStreamError.userDeclined (-3801): користувач не дав дозволу «Запис екрана»
    if ns.domain == SCStreamErrorDomain && ns.code == SCStreamError.Code.userDeclined.rawValue {
        return HelperError(message: "no Screen Recording permission (\(context))", code: .noPermission)
    }
    return HelperError(message: "\(context): \(error.localizedDescription) [\(ns.domain) \(ns.code)]", code: .captureFailed)
}

/// Знімає частину одного дисплея. localRect — у точках відносно самого дисплея, pixelSize — розмір результату.
func captureRegion(_ d: Display, localRect: CGRect, pixelSize: CGSize, cursor: Bool) async throws -> CGImage {
    let filter = SCContentFilter(display: d.sc, excludingWindows: [])
    let config = SCStreamConfiguration()
    config.sourceRect = localRect
    config.width = max(1, Int(pixelSize.width.rounded()))
    config.height = max(1, Int(pixelSize.height.rounded()))
    config.showsCursor = cursor
    config.capturesAudio = false
    config.pixelFormat = kCVPixelFormatType_32BGRA
    config.colorSpaceName = CGColorSpace.sRGB
    do {
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    } catch {
        throw mapCaptureError(error, "capture of display \(d.sc.displayID) failed")
    }
}

func makeContext(_ width: Int, _ height: Int) -> CGContext? {
    guard let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
    // premultipliedFirst + byteOrder32Little → у пам'яті BGRA premultiplied (так само, як Bgra8888/Premul в Avalonia)
    return CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                     bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
}

/// Знімок прямокутника в глобальних точках. Кожен дисплей знімається в рідній роздільності,
/// а склеюються вони в одну картинку з масштабом scale (проміжки між моніторами лишаються прозорими).
func captureRect(_ rect: CGRect, displays: [Display], scale: CGFloat, cursor: Bool) async throws -> CGImage {
    let parts = displays.filter { $0.frame.intersects(rect) }
    guard !parts.isEmpty else { throw HelperError(message: "rect \(rect) does not intersect any display", code: .captureFailed) }

    let outW = max(1, Int((rect.width * scale).rounded()))
    let outH = max(1, Int((rect.height * scale).rounded()))

    // Найчастіший випадок — прямокутник у межах одного дисплея: один знімок одразу потрібного розміру
    if parts.count == 1 {
        let d = parts[0]
        let inter = d.frame.intersection(rect)
        if inter.equalTo(rect) {
            let local = rect.offsetBy(dx: -d.frame.minX, dy: -d.frame.minY)
            return try await captureRegion(d, localRect: local, pixelSize: CGSize(width: outW, height: outH), cursor: cursor)
        }
    }

    guard let ctx = makeContext(outW, outH) else { throw HelperError(message: "cannot create bitmap context", code: .captureFailed) }
    ctx.interpolationQuality = .high
    for d in parts {
        let inter = d.frame.intersection(rect)
        let local = inter.offsetBy(dx: -d.frame.minX, dy: -d.frame.minY)
        let native = CGSize(width: inter.width * d.scale, height: inter.height * d.scale)
        let image = try await captureRegion(d, localRect: local, pixelSize: native, cursor: cursor)
        // CoreGraphics має початок координат у ЛІВОМУ НИЖНЬОМУ куті — перевертаємо Y
        let dx = (inter.minX - rect.minX) * scale
        let dyTop = (inter.minY - rect.minY) * scale
        let dw = inter.width * scale, dh = inter.height * scale
        ctx.draw(image, in: CGRect(x: dx, y: CGFloat(outH) - dyTop - dh, width: dw, height: dh))
    }
    guard let result = ctx.makeImage() else { throw HelperError(message: "cannot compose image", code: .captureFailed) }
    return result
}

// ---- Вивід ----

/// Пікселі BGRA premultiplied (sRGB) у новому контексті; повертає контекст, щоб не копіювати буфер ще раз.
func renderBgra(_ image: CGImage) throws -> CGContext {
    guard let ctx = makeContext(image.width, image.height) else {
        throw HelperError(message: "cannot create bitmap context", code: .writeFailed)
    }
    ctx.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    return ctx
}

func writeRaw(_ image: CGImage, scale: CGFloat) throws {
    let ctx = try renderBgra(image)
    guard let pixels = ctx.data else { throw HelperError(message: "empty bitmap context", code: .writeFailed) }
    var header = Data("SOC1".utf8)
    func appendU32(_ v: Int) {
        var le = UInt32(v).littleEndian
        withUnsafeBytes(of: &le) { header.append(contentsOf: $0) }
    }
    appendU32(ctx.width)
    appendU32(ctx.height)
    appendU32(ctx.bytesPerRow)
    var s = Double(scale).bitPattern.littleEndian
    withUnsafeBytes(of: &s) { header.append(contentsOf: $0) }
    do {
        try FileHandle.standardOutput.write(contentsOf: header)
        try FileHandle.standardOutput.write(contentsOf: Data(bytesNoCopy: pixels, count: ctx.bytesPerRow * ctx.height, deallocator: .none))
    } catch {
        throw HelperError(message: "cannot write to stdout: \(error)", code: .writeFailed)
    }
}

func writePNG(_ image: CGImage, to path: String) throws {
    let data = NSMutableData()
    guard let dest = CGImageDestinationCreateWithData(data as CFMutableData, UTType.png.identifier as CFString, 1, nil) else {
        throw HelperError(message: "cannot create PNG encoder", code: .writeFailed)
    }
    CGImageDestinationAddImage(dest, image, nil)
    guard CGImageDestinationFinalize(dest) else { throw HelperError(message: "PNG encoding failed", code: .writeFailed) }
    do {
        if path == "-" {
            try FileHandle.standardOutput.write(contentsOf: data as Data)
        } else {
            try (data as Data).write(to: URL(fileURLWithPath: path), options: .atomic)
        }
    } catch {
        throw HelperError(message: "cannot write \(path): \(error)", code: .writeFailed)
    }
}

// ---- Режими ----

func union(of displays: [Display]) -> CGRect {
    displays.dropFirst().reduce(displays[0].frame) { $0.union($1.frame) }
}

func selfTest(_ o: Options, _ displays: [Display]) async throws {
    let main = displays[0]
    let image = try await captureRect(main.frame, displays: displays, scale: main.scale, cursor: false)
    guard image.width > 0, image.height > 0 else { throw HelperError(message: "self-test: empty image", code: .captureFailed) }
    // «Не порожній» = є хоч один непрозорий піксель (без дозволу/при збої буфер лишається нулями)
    let ctx = try renderBgra(image)
    guard let pixels = ctx.data?.assumingMemoryBound(to: UInt8.self) else {
        throw HelperError(message: "self-test: no pixel data", code: .captureFailed)
    }
    var opaque = 0
    let step = max(1, image.height / 64)
    for y in stride(from: 0, to: image.height, by: step) {
        let row = pixels + y * ctx.bytesPerRow
        for x in stride(from: 0, to: image.width, by: max(1, image.width / 64)) where row[x * 4 + 3] != 0 {
            opaque += 1
        }
    }
    guard opaque > 0 else { throw HelperError(message: "self-test: image is fully transparent", code: .captureFailed) }
    if o.outGiven && o.out != "-" { try writePNG(image, to: o.out) }
    print("OK: main display \(image.width)x\(image.height) @\(main.scale)x, displays: \(displays.count), opaque samples: \(opaque)")
}

func run(_ o: Options) async throws {
    // Без дозволу ScreenCaptureKit показав би системний запит і чекав; дозвіл перевіряє програма (MacPermissions)
    guard CGPreflightScreenCaptureAccess() else {
        throw HelperError(message: "no Screen Recording permission", code: .noPermission)
    }
    let displays = try await loadDisplays()
    guard !displays.isEmpty else { throw HelperError(message: "no displays", code: .captureFailed) }

    let image: CGImage
    let scale: CGFloat
    switch o.mode! {
    case .list:
        for (i, d) in displays.enumerated() {
            print("\(i) id=\(d.sc.displayID) x=\(d.frame.minX) y=\(d.frame.minY) w=\(d.frame.width) h=\(d.frame.height) scale=\(d.scale)")
        }
        return
    case .selftest:
        try await selfTest(o, displays)
        return
    case .display(let n):
        guard n < displays.count else { throw HelperError(message: "no display \(n) (have \(displays.count))", code: .captureFailed) }
        scale = o.scale ?? displays[n].scale
        image = try await captureRect(displays[n].frame, displays: displays, scale: scale, cursor: o.cursor)
    case .all:
        scale = o.scale ?? displays.map(\.scale).max()!
        image = try await captureRect(union(of: displays), displays: displays, scale: scale, cursor: o.cursor)
    case .rect(let r):
        let involved = displays.filter { $0.frame.intersects(r) }
        scale = o.scale ?? (involved.map(\.scale).max() ?? 1)
        image = try await captureRect(r, displays: displays, scale: scale, cursor: o.cursor)
    }

    if o.raw {
        try writeRaw(image, scale: scale)
    } else {
        try writePNG(image, to: o.out)
    }
}

// ---- Точка входу ----

let options = parseArgs(Array(CommandLine.arguments.dropFirst()))
// Підстраховка: відкриваємо з'єднання з WindowServer до першого виклику ScreenCaptureKit
_ = CGMainDisplayID()

DispatchQueue.global().asyncAfter(deadline: .now() + options.timeout) {
    fail("timeout after \(options.timeout) s", .timeout)
}

Task {
    do {
        try await run(options)
        exit(ExitCode.ok.rawValue)
    } catch let e as HelperError {
        fail(e.message, e.code)
    } catch {
        fail("unexpected error: \(error)", .captureFailed)
    }
}
dispatchMain()
