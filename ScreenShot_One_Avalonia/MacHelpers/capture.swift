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
//   capture-helper --record файл.mp4 (--display N | --rect X,Y,W,H)   запис відео (macOS 15+, SCRecordingOutput);
//                  зупинка — рядок «q» у stdin (або закритий stdin). Після старту пише в stdout «RECORDING».
//                  --exclude-pid PID       не показувати вікна цієї програми (панель запису, рамка зони)…
//                  --except-window ID      …крім цих вікон (показ натиснутих клавіш), можна кілька разів
//                  --clicks  --mic  --audio  --fps N
// Вихід:
//   --out <файл.png>   PNG у файл;  --out -   PNG у stdout (за замовчуванням)
//   --raw              сирі пікселі в stdout (так читає програма, без PNG «туди й назад»):
//                      "SOC1", ширина, висота, stride (UInt32 LE), масштаб (Float64 LE), далі BGRA premultiplied, sRGB
// Інше:
//   --scale S          масштаб результату (пікселі на точку); за замовчуванням — найбільший серед задіяних дисплеїв
//   --cursor           показувати курсор
//   --exclude-window ID  не показувати це вікно (мініатюра після знімка, панель запису); можна кілька разів
//   --timeout СЕК      аварійний тайм-аут (за замовчуванням 15)
// Коди виходу: 0 — успіх, 1 — неправильні аргументи, 2 — знімок не вдався, 3 — не вдалося записати результат,
//              4 — немає дозволу «Запис екрана», 5 — тайм-аут, 6 — запис не підтримується (macOS < 15).
import CoreGraphics
import CoreVideo
import Foundation
import AppKit
import AVFoundation
import CoreMedia
import ImageIO
import ScreenCaptureKit
import UniformTypeIdentifiers

enum ExitCode: Int32 {
    case ok = 0, badArgs = 1, captureFailed = 2, writeFailed = 3, noPermission = 4, timeout = 5, unsupported = 6
}

struct HelperError: Error {
    let message: String
    let code: ExitCode
}

func fail(_ message: String, _ code: ExitCode) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(code.rawValue)
}

let usage = "usage: capture-helper (--display N | --all | --rect X,Y,W,H | --list | --selftest) [--out file.png | --out - | --raw] [--scale S] [--cursor] [--exclude-window ID]... [--timeout SEC]\n       capture-helper --record file.mp4 (--display N | --rect X,Y,W,H) [--cursor] [--clicks] [--mic] [--audio] [--fps N] [--exclude-pid PID [--except-window ID]...]"

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
    var timeoutGiven = false
    // Запис
    var record: String?
    var excludePid: pid_t?
    var exceptWindows: [CGWindowID] = []
    var excludeWindows: [CGWindowID] = []
    var clicks = false
    var mic = false
    var audio = false
    var fps = 30
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
            o.timeoutGiven = true
        case "--record":
            o.record = value(arg)
        case "--exclude-pid":
            guard let p = Int32(value(arg)), p > 0 else { fail("--exclude-pid expects a process id", .badArgs) }
            o.excludePid = p
        case "--exclude-window":
            guard let w = UInt32(value(arg)) else { fail("--exclude-window expects a window id", .badArgs) }
            o.excludeWindows.append(w)
        case "--except-window":
            guard let w = UInt32(value(arg)) else { fail("--except-window expects a window id", .badArgs) }
            o.exceptWindows.append(w)
        case "--clicks":
            o.clicks = true
        case "--mic":
            o.mic = true
        case "--audio":
            o.audio = true
        case "--fps":
            guard let f = Int(value(arg)), f >= 1, f <= 120 else { fail("--fps expects 1…120", .badArgs) }
            o.fps = f
        default:
            fail("unknown argument: \(arg)\n\(usage)", .badArgs)
        }
        i += 1
    }
    if o.mode == nil { fail(usage, .badArgs) }
    if o.record != nil {
        switch o.mode! {
        case .display, .rect: break
        default: fail("--record needs --display N or --rect X,Y,W,H", .badArgs)
        }
    }
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

/// Вікна, яких не має бути на знімку (з --exclude-window), — заповнюється в loadDisplays.
var excludedWindows: [SCWindow] = []

func loadDisplays(excluding ids: [CGWindowID] = []) async throws -> [Display] {
    let content: SCShareableContent
    do {
        content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    } catch {
        throw mapCaptureError(error, "cannot list displays")
    }
    excludedWindows = ids.isEmpty ? [] : content.windows.filter { ids.contains($0.windowID) }
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
    let filter = SCContentFilter(display: d.sc, excludingWindows: excludedWindows)
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

// ---- Запис відео (macOS 15+) ----

@available(macOS 15.0, *)
final class RecordingController: NSObject, SCStreamDelegate, SCRecordingOutputDelegate {
    private let lock = NSLock()
    private var finish: CheckedContinuation<Void, Error>?
    private var finishedEarly: Result<Void, Error>?

    func waitForFinish() async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            lock.lock()
            if let r = finishedEarly { lock.unlock(); c.resume(with: r); return }
            finish = c
            lock.unlock()
        }
    }

    private func complete(_ r: Result<Void, Error>) {
        lock.lock()
        if let c = finish { finish = nil; lock.unlock(); c.resume(with: r); return }
        if finishedEarly == nil { finishedEarly = r }
        lock.unlock()
    }

    func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {}
    func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        complete(.failure(HelperError(message: "recording failed: \(error.localizedDescription)", code: .captureFailed)))
    }
    func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) { complete(.success(())) }
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        complete(.failure(mapCaptureError(error, "stream stopped")))
    }
}

func even(_ v: CGFloat) -> Int { max(2, Int(v.rounded()) & ~1) }

// Запис стартує на головному потоці: ScreenCaptureKit під час запису створює службове NSWindow,
// а AppKit дозволяє це лише з головного потоку (інакше NSInternalInconsistencyException).
@available(macOS 15.0, *)
@MainActor
func record(_ o: Options, _ displays: [Display], to path: String) async throws {
    // Область: дисплей цілком або прямокутник (точки, глобальні координати) у межах дисплея, де його центр
    let target: Display
    var local: CGRect
    switch o.mode! {
    case .display(let n):
        guard n < displays.count else { throw HelperError(message: "no display \(n) (have \(displays.count))", code: .captureFailed) }
        target = displays[n]
        local = CGRect(origin: .zero, size: target.frame.size)
    case .rect(let r):
        target = displays.first { $0.frame.contains(CGPoint(x: r.midX, y: r.midY)) } ?? displays[0]
        local = r.intersection(target.frame).offsetBy(dx: -target.frame.minX, dy: -target.frame.minY)
        guard local.width >= 2, local.height >= 2 else { throw HelperError(message: "rect \(r) is outside displays", code: .captureFailed) }
    default:
        throw HelperError(message: "bad record mode", code: .badArgs)
    }

    // Власні вікна програми (панель запису, рамка зони, підказки) не потрапляють у відео — крім показу клавіш
    let content: SCShareableContent
    do { content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false) }
    catch { throw mapCaptureError(error, "cannot list windows") }
    let filter: SCContentFilter
    if let pid = o.excludePid, let app = content.applications.first(where: { $0.processID == pid }) {
        let except = content.windows.filter { o.exceptWindows.contains($0.windowID) }
        filter = SCContentFilter(display: target.sc, excludingApplications: [app], exceptingWindows: except)
    } else {
        filter = SCContentFilter(display: target.sc, excludingWindows: [])
    }

    let config = SCStreamConfiguration()
    config.sourceRect = local
    config.width = even(local.width * target.scale)
    config.height = even(local.height * target.scale)
    config.showsCursor = o.cursor
    config.showMouseClicks = o.clicks
    config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(o.fps))
    config.queueDepth = 6
    config.capturesAudio = o.audio
    config.captureMicrophone = o.mic

    let controller = RecordingController()
    let stream = SCStream(filter: filter, configuration: config, delegate: controller)
    let rc = SCRecordingOutputConfiguration()
    rc.outputURL = URL(fileURLWithPath: path)
    rc.outputFileType = .mp4
    rc.videoCodecType = .h264
    let output = SCRecordingOutput(configuration: rc, delegate: controller)
    do {
        try stream.addRecordingOutput(output)
        try await stream.startCapture()
    } catch {
        throw mapCaptureError(error, "cannot start recording")
    }
    print("RECORDING \(config.width)x\(config.height)")
    fflush(stdout)

    // Чекаємо «q» або закритий stdin
    await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
        DispatchQueue.global().async {
            while let line = readLine() {
                if line.trimmingCharacters(in: .whitespaces) == "q" { break }
            }
            c.resume()
        }
    }
    do { try await stream.stopCapture() } catch { FileHandle.standardError.write("stopCapture: \(error)\n".data(using: .utf8)!) }
    // Дописування файла: чекаємо подію завершення, але не вічно
    DispatchQueue.global().asyncAfter(deadline: .now() + 20) { fail("timeout while finishing the recording", .timeout) }
    try await controller.waitForFinish()
    print("DONE")
    fflush(stdout)
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
    let displays = try await loadDisplays(excluding: o.excludeWindows)
    guard !displays.isEmpty else { throw HelperError(message: "no displays", code: .captureFailed) }

    if let path = o.record {
        guard #available(macOS 15.0, *) else { throw HelperError(message: "recording needs macOS 15", code: .unsupported) }
        try await record(o, displays, to: path)
        return
    }

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

// Запис триває скільки треба — тайм-аут лише якщо його задано явно
if options.record == nil || options.timeoutGiven {
    DispatchQueue.global().asyncAfter(deadline: .now() + options.timeout) {
        fail("timeout after \(options.timeout) s", .timeout)
    }
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
if options.record != nil {
    // Запис: повноцінний цикл AppKit (без значка в Dock), щоб службові вікна ScreenCaptureKit мали головний потік
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    app.run()
} else {
    dispatchMain()
}
