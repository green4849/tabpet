// Small characters that walk in the front Chrome window's tab strip (or along the
// bottom of the screen), dance while music plays and doze when you're away.
// Click one and it plays a sound; drag it and let go and it drops.
//
// Characters are sprite sheets: <name>-sheet.png + <name>-sheet.json, with rows idle,
// walk (facing right), sleep, dance, fall (other rows are ignored). They are looked for
// in ~/.tabpet/ (your own) and in the app's bundled characters/ folder (the example);
// sounds likewise in ~/.tabpet/sounds/ and the bundled characters/sounds/.
// Nothing here needs a permission prompt: sound is "is the output device in
// use", away is "seconds since the last input event".
import AppKit
import CoreAudio

let HOME = FileManager.default.homeDirectoryForCurrentUser.path
// Your own characters and sounds; the example that ships with the app is in its Resources.
let PET_DIR = ProcessInfo.processInfo.environment["TABPET_DIR"] ?? "\(HOME)/.tabpet"
let CHARACTER_DIRS = [PET_DIR] + (Bundle.main.resourcePath.map { ["\($0)/characters"] } ?? [])
// The character out on first launch; after that the menu's choice is remembered.
let PET_NAME = ProcessInfo.processInfo.environment["TABPET_CHARACTER"] ?? "sprout"

// How big it is on screen, as a fraction of the sheet's size (0.75: a 64×69 pt cell
// becomes 48×52 pt, the character itself about 31 pt tall, a bit less than Chrome's
// tab strip). `defaults write io.github.green4849.tabpet petScale -float 1`
let PET_SCALE: CGFloat = {
    let v = UserDefaults.standard.double(forKey: "petScale")
    return v > 0 ? CGFloat(min(2, max(0.3, v))) : 0.75
}()

// Chrome's tab strip, the strip above the address bar. Without reading Chrome's own
// layout (that would need the Accessibility permission) the free part is guessed:
// this much is left for the window buttons and the first tab or so, this much on the
// right for the buttons there. All three can be changed with `defaults write`.
func tabStripSetting(_ key: String, _ fallback: Double) -> CGFloat {
    let v = UserDefaults.standard.double(forKey: key)
    return CGFloat(v > 0 ? v : fallback)
}
let TAB_STRIP_HEIGHT = tabStripSetting("tabStripHeight", 40)
let TAB_STRIP_LEFT = tabStripSetting("tabStripLeft", 400)
let TAB_STRIP_RIGHT = tabStripSetting("tabStripRight", 180)

// Every character with a sheet, by file name (`sprout-sheet.json` → sprout); your own come first.
func petCharacters() -> [String] {
    var seen: [String] = []
    for dir in CHARACTER_DIRS {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
        for n in names.filter({ $0.hasSuffix("-sheet.json") }).sorted() {
            let c = String(n.dropLast("-sheet.json".count)); if !seen.contains(c) { seen.append(c) }
        }
    }
    return seen
}

// The folder that has this character's sheet (yours wins over the bundled one).
func characterDir(_ name: String) -> String? {
    CHARACTER_DIRS.first { FileManager.default.fileExists(atPath: "\($0)/\(name)-sheet.json") }
}

struct PetRow: Decodable { let name: String; let frames: Int; let fps: Double }
struct PetSheet: Decodable { let cell: [Int]; let displayPoints: [Double]; let rows: [PetRow]; let displayName: String?; let climb: String?; let color: String? }

// How a character gets up to a Chrome tab strip far above it (see Pet.startHop), from the
// sheet's "climb": jump (crouch and leap, using a "climb" row if there is one), balloon or
// fly (float or flap up slowly, "climb" row), steps (several little hops) or poof (shrink
// away and pop up there, the default).
let CLIMB_STYLES: Set<String> = ["jump", "balloon", "fly", "steps", "poof"]

// The name shown in the menu: the sheet's "displayName", else the file name.
func characterLabel(_ name: String) -> String {
    guard let dir = characterDir(name), let data = FileManager.default.contents(atPath: "\(dir)/\(name)-sheet.json"),
          let sheet = try? JSONDecoder().decode(PetSheet.self, from: data) else { return name }
    return sheet.displayName ?? name
}

final class Sprite {
    let frames: [String: [CGImage]]
    let fps: [String: Double]
    let size: NSSize
    let climb: String
    let color: NSColor?      // the sheet's "color" ("#rrggbb"): what its checklist is drawn in

    init?(name: String) {
        guard let dir = characterDir(name) else { return nil }
        let png = URL(fileURLWithPath: "\(dir)/\(name)-sheet.png")
        guard let data = FileManager.default.contents(atPath: "\(dir)/\(name)-sheet.json"),
              let sheet = try? JSONDecoder().decode(PetSheet.self, from: data),
              sheet.cell.count == 2, sheet.displayPoints.count == 2,
              let src = CGImageSourceCreateWithURL(png as CFURL, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let w = sheet.cell[0], h = sheet.cell[1]
        var f: [String: [CGImage]] = [:], r: [String: Double] = [:]
        for (i, row) in sheet.rows.enumerated() {
            f[row.name] = (0..<row.frames).compactMap { img.cropping(to: CGRect(x: $0 * w, y: i * h, width: w, height: h)) }
            r[row.name] = row.fps
        }
        guard f["idle"]?.isEmpty == false, f["walk"]?.isEmpty == false else { return nil }
        frames = f; fps = r
        climb = CLIMB_STYLES.contains(sheet.climb ?? "") ? sheet.climb! : "poof"
        if let hex = sheet.color?.trimmingCharacters(in: CharacterSet(charactersIn: "#")), hex.count == 6, let v = UInt32(hex, radix: 16) {
            color = NSColor(srgbRed: CGFloat(v >> 16 & 0xff) / 255, green: CGFloat(v >> 8 & 0xff) / 255, blue: CGFloat(v & 0xff) / 255, alpha: 1)
        } else { color = nil }
        size = NSSize(width: sheet.displayPoints[0] * PET_SCALE, height: sheet.displayPoints[1] * PET_SCALE)
    }
}

// Is anything playing through the default output device? No permission needed.
func soundPlaying() -> Bool {
    var dev = AudioObjectID(0)
    var size = UInt32(MemoryLayout<AudioObjectID>.size)
    var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                          mScope: kAudioObjectPropertyScopeGlobal,
                                          mElement: kAudioObjectPropertyElementMain)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &dev) == noErr else { return false }
    var running = UInt32(0)
    size = UInt32(MemoryLayout<UInt32>.size)
    addr.mSelector = kAudioDevicePropertyDeviceIsRunningSomewhere
    guard AudioObjectGetPropertyData(dev, &addr, 0, nil, &size, &running) == noErr else { return false }
    return running != 0
}

// One voice for all the pets: short clips in ~/.tabpet/sounds/ (or the bundled ones),
// `poke.m4a` or variants `poke-1.m4a`, `poke-2.m4a` … picked at random; a missing clip is
// just skipped. Kept rare: 8 s between any two sounds, 2 min between the same one, one at
// a time (the order below is the priority), nothing while every pet is hidden (a
// full-screen app is in front) or you're away (except dozing off, as it happens), only
// dance while music plays. Follows the Mac's volume and mute.
final class Voice {
    // One clip per action, in priority order.
    enum Kind: String, CaseIterable {
        case welcome   // you're back after a while
        case poke      // clicked
        case dance     // music started
        case hop       // jumps up onto a window
        case sleep     // dozes off (the only one said while you're away, as it happens)
        case drop      // let go after a drag: said with sayNow, every time, right away
    }

    var on: Bool = UserDefaults.standard.object(forKey: "petSound") as? Bool ?? true {
        didSet { UserDefaults.standard.set(on, forKey: "petSound") }
    }
    // Until when the output device being busy is (probably) our own clip: the device
    // stays "running" for about 2.4 s after a short sound ends.
    private(set) var selfUntil = 0.0
    private var pending: Set<Kind> = []
    private var lastAny = -1e9
    private var lastKind: [Kind: Double] = [:]
    private var player: NSSound?

    func want(_ k: Kind) { pending.insert(k) }

    // Said now, whatever the spacing rules say; it still counts as the last sound.
    func sayNow(_ k: Kind, now: Double) {
        guard on, let url = clip(k), let s = NSSound(contentsOf: url, byReference: true) else {
            NSLog("TabPet pet voice: %@ skipped (off or no clip)", k.rawValue); return
        }
        play(k, s, url, now: now)
    }

    // Called a few times a second for all the pets together; plays at most one wanted clip.
    func flush(now: Double, music: Bool, away: Bool, visible: Bool) {
        defer { pending.removeAll() }
        guard on, visible, now - lastAny >= 8 else { return }
        for k in Kind.allCases where pending.contains(k) {
            if away && k != .sleep { continue }
            if music && k != .dance { continue }
            if now - (lastKind[k] ?? -1e9) < 120 { continue }
            guard let url = clip(k), let s = NSSound(contentsOf: url, byReference: true) else { continue }
            play(k, s, url, now: now)
            return
        }
    }

    private func play(_ k: Kind, _ s: NSSound, _ url: URL, now: Double) {
        player?.stop(); player = s
        NSLog("TabPet pet voice: %@ %@ started=%d", k.rawValue, url.lastPathComponent, s.play() ? 1 : 0)
        lastAny = now; lastKind[k] = now
        selfUntil = now + s.duration + 3
    }

    private func clip(_ k: Kind) -> URL? {
        // Your sounds folder if it has any clip for this, else the bundled one.
        for dir in CHARACTER_DIRS.map({ "\($0)/sounds" }) {
            if let url = clip(k, in: dir) { return url }
        }
        return nil
    }

    private func clip(_ k: Kind, in dir: String) -> URL? {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return nil }
        let exts: Set<String> = ["m4a", "wav", "mp3", "aiff", "caf"]
        let mine = names.filter { n in
            let u = URL(fileURLWithPath: n)
            guard exts.contains(u.pathExtension.lowercased()) else { return false }
            let base = u.deletingPathExtension().lastPathComponent
            if base == k.rawValue { return true }
            guard base.hasPrefix(k.rawValue + "-") else { return false }
            return Int(base.dropFirst(k.rawValue.count + 1)) != nil
        }
        return mine.randomElement().map { URL(fileURLWithPath: "\(dir)/\($0)") }
    }
}

// Seconds since the last keyboard or mouse input.
func secondsIdle() -> Double {
    let kinds: [CGEventType] = [.mouseMoved, .keyDown, .flagsChanged, .leftMouseDown, .rightMouseDown, .otherMouseDown,
                                .leftMouseDragged, .rightMouseDragged, .scrollWheel]
    return kinds.map { CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: $0) }.min() ?? 0
}

// Something to stand on: the bottom of a screen (above the Dock on the main one),
// or the free, visible part of a Chrome window's tab strip. Cocoa coordinates.
struct Floor {
    let window: Int?          // CGWindowNumber of the Chrome window, nil for the screen bottom
    let y: CGFloat
    let minX: CGFloat, maxX: CGFloat
    let originX: CGFloat      // the window's left edge, to carry the pet along when it moves
}

func screenFloor(_ s: NSScreen) -> Floor {
    let v = s.visibleFrame
    return Floor(window: nil, y: v.minY, minX: v.minX, maxX: v.maxX, originX: v.minX)
}

// What the window list says about Chrome: the tab strips the pet can walk in, and
// the frontmost Chrome window (whose screen the pet lives on, even when that window
// has no free part of its strip).
struct ChromeScan {
    var floors: [Floor] = []
    var front: Int? = nil
    var frontScreen: NSScreen? = nil
}

// Only window positions and the app's name are read (no titles, no screen
// recording). The pets walk along the bottom of the tab strip, between the tabs on the
// left and the buttons on the right; parts covered by windows in front don't count.
func scanChrome() -> ChromeScan {
    var out = ChromeScan()
    guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
          let primaryH = NSScreen.screens.first?.frame.height else { return out }
    let me = Int(getpid())
    var front: [CGRect] = []
    for w in info {   // front to back
        guard (w[kCGWindowLayer as String] as? Int) == 0, (w[kCGWindowOwnerPID as String] as? Int) != me,
              let b = w[kCGWindowBounds as String] as? NSDictionary,
              let r = CGRect(dictionaryRepresentation: b) else { continue }
        let rect = CGRect(x: r.minX, y: primaryH - r.maxY, width: r.width, height: r.height)
        // Google Chrome, and its Beta / Dev / Canary builds
        if ((w[kCGWindowOwnerName as String] as? String) ?? "").hasPrefix("Google Chrome"),
           r.height > 150, r.width > 200,
           let num = w[kCGWindowNumber as String] as? Int,
           let scr = NSScreen.screens.first(where: { $0.frame.contains(CGPoint(x: rect.midX, y: rect.maxY - 1)) }) {
            if out.front == nil { out.front = num; out.frontScreen = scr }
            let floorY = rect.maxY - TAB_STRIP_HEIGHT
            let band = CGRect(x: rect.minX, y: floorY, width: rect.width, height: TAB_STRIP_HEIGHT)
            // Only the free part of the strip, and only what is on its screen.
            var segs = [(max(rect.minX + TAB_STRIP_LEFT, scr.frame.minX), min(rect.maxX - TAB_STRIP_RIGHT, scr.frame.maxX))]
                .filter { $0.1 > $0.0 }
            for o in front where o.intersects(band) {
                segs = segs.flatMap { s -> [(CGFloat, CGFloat)] in
                    if o.maxX <= s.0 || o.minX >= s.1 { return [s] }
                    return [(s.0, min(s.1, o.minX)), (max(s.0, o.maxX), s.1)].filter { $0.1 > $0.0 }
                }
            }
            for s in segs {
                out.floors.append(Floor(window: num, y: floorY, minX: s.0, maxX: s.1, originX: rect.minX))
            }
        }
        front.append(rect)
    }
    return out
}

// What the computer is doing, worked out once for all the pets: music, away, and where
// Chrome's tab strips are. The welcome, sleep and dance sounds are wanted here, once.
final class Senses {
    private let voice: Voice
    private(set) var music = false
    private(set) var away = false
    private var soundSince: Double?
    private var silentSince: Double?
    private var lastCheck = -1.0
    private var scan = ChromeScan()
    private var scannedAt = -1.0

    init(voice: Voice) { self.voice = voice }

    func update(_ now: Double) {
        guard now - lastCheck >= 1 else { return }
        lastCheck = now
        let wasAway = away
        away = secondsIdle() > 5 * 60
        if wasAway && !away { voice.want(.welcome) }
        if !wasAway && away { voice.want(.sleep) }
        // Its own clip (and the device winding down after it) isn't music.
        guard now >= voice.selfUntil else { soundSince = nil; return }
        let wasMusic = music
        if soundPlaying() {
            silentSince = nil
            let since = soundSince ?? now; soundSince = since
            if now - since >= 3 { music = true }     // not for a notification blip
        } else {
            soundSince = nil
            let since = silentSince ?? now; silentSince = since
            if now - since >= 2 { music = false }
        }
        if music && !wasMusic { voice.want(.dance) }
    }

    // The window list, read at most every 30 ms however many pets ask.
    func chrome(_ now: Double) -> ChromeScan {
        if now - scannedAt >= 0.03 { scan = scanChrome(); scannedAt = now }
        return scan
    }
}

// macOS keeps windows below the menu bar; in a tab strip right under it the top of the
// pet's (mostly transparent) window has to reach a little above, so don't push it down.
final class PetPanel: NSPanel {
    override func constrainFrameRect(_ r: NSRect, to screen: NSScreen?) -> NSRect { r }
}

final class PetView: NSView {
    let sprite = CALayer()
    var onClick: () -> Void = {}
    var onDrag: (CGFloat, CGFloat) -> Void = { _, _ in }
    var onDrop: () -> Void = {}
    private var last: NSPoint?
    private var moved = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        sprite.frame = bounds
        sprite.contentsGravity = .resizeAspect
        sprite.contentsScale = 2
        sprite.minificationFilter = .trilinear
        layer?.addSublayer(sprite)
    }
    required init?(coder: NSCoder) { nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with e: NSEvent) { last = NSEvent.mouseLocation; moved = false }
    override func mouseDragged(with e: NSEvent) {
        guard let l = last else { return }
        let p = NSEvent.mouseLocation
        if !moved && hypot(p.x - l.x, p.y - l.y) < 4 { return }
        moved = true
        onDrag(p.x - l.x, p.y - l.y)
        last = p
    }
    override func mouseUp(with e: NSEvent) {
        if moved { onDrop() } else { onClick() }
        last = nil
    }

    func show(_ img: CGImage, mirrored: Bool, scale: CGFloat = 1) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        sprite.contents = img
        // shrink toward the feet (the bottom middle), not the centre
        let drop = -(1 - scale) * bounds.height / 2
        sprite.setAffineTransform(CGAffineTransform(translationX: 0, y: drop).scaledBy(x: mirrored ? -scale : scale, y: scale))
        CATransaction.commit()
    }
}

// A timer that keeps firing while a menu is open (the default run loop mode pauses then).
func repeatingTimer(_ interval: Double, _ body: @escaping () -> Void) -> Timer {
    let t = Timer(timeInterval: interval, repeats: true) { _ in body() }
    RunLoop.main.add(t, forMode: .common)
    return t
}

// One character on screen. Several can be out at once; they share one Voice and one Senses.
final class Pet {
    private let sprite: Sprite
    private let voice: Voice
    private let senses: Senses
    private let slot: Int                               // where along the bottom it first appears
    private var window: NSPanel?
    private var view: PetView?
    private var timer: Timer?

    // A plain click (the checklist opens over it), and where it is while that's open.
    var onTap: ((Pet) -> Void)?
    var onMove: ((Pet, NSRect) -> Void)?
    var screenRect: NSRect? { window?.frame }
    var accent: NSColor? { sprite.color }
    // Held while its checklist is open: it stands still and doesn't go off to a Chrome window.
    var held = false

    private var x: CGFloat = 200, y: CGFloat = 0, vy: CGFloat = 0
    private var dir: CGFloat = 1
    private var dragging = false, falling = false
    private var landing = 0.0
    private var home = NSScreen.screens.first          // whose bottom it falls to
    private var floor: Floor? = nil                     // what it stands on now
    private var floors: [Floor] = []                    // tab strips wide enough for it
    private var lastScan = 0.0
    private var target: Int? = nil, targetSince = 0.0  // the frontmost Chrome window, and since when
    private var targetScreen: NSScreen? = nil
    private var declined: Int? = nil                    // dragged off this window: leave it alone
    // A jump onto a floor. Up to a strip far above, it goes the character's own way (style).
    private var hop: (from: NSPoint, to: NSPoint, t: Double, dur: Double, floor: Floor, style: String, steps: Int)? = nil
    private var restUntil = 0.0, nextRest = CACurrentMediaTime() + 12
    private var row = "idle", frame = 0, phase = 0.0
    private var last = CACurrentMediaTime()
    private let feet: CGFloat = 5 * PET_SCALE           // the sheet's floor line is 5 pt above the cell's bottom

    init?(_ name: String, voice: Voice, senses: Senses, slot: Int) {
        guard let s = Sprite(name: name) else { return nil }
        sprite = s; self.voice = voice; self.senses = senses; self.slot = slot
    }

    // Shown somewhere (not hidden behind a full-screen app).
    var isVisible: Bool { window?.occlusionState.contains(.visible) ?? false }

    func start() {
        guard window == nil else { return }
        let size = sprite.size
        let p = PetPanel(contentRect: NSRect(origin: .zero, size: size),
                         styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.level = .floating
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        // All desktops, but not over full-screen apps (videos, presentations): it just isn't shown there.
        p.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        let v = PetView(frame: NSRect(origin: .zero, size: size))
        v.onClick = { [weak self] in
            guard let self else { return }
            self.voice.want(.poke); self.onTap?(self)
        }
        v.onDrag = { [weak self] dx, dy in
            guard let self else { return }
            self.dragging = true; self.falling = false; self.hop = nil
            self.x += dx; self.y += dy
        }
        v.onDrop = { [weak self] in
            guard let self else { return }
            let c = CGPoint(x: self.x + size.width / 2, y: self.y + size.height / 2)
            self.home = NSScreen.screens.first(where: { $0.frame.contains(c) }) ?? NSScreen.screens.first
            self.dragging = false; self.falling = true; self.vy = 0; self.floor = nil
            self.voice.sayNow(.drop, now: CACurrentMediaTime())
            // Put somewhere by hand: don't hop back onto the front Chrome window until another one comes to the front.
            self.declined = self.target
        }
        p.contentView = v
        if let vf = NSScreen.screens.first?.visibleFrame {
            x = vf.minX + vf.width * (0.3 + 0.13 * CGFloat(slot % 5)); y = vf.minY - feet
            dir = slot % 2 == 0 ? 1 : -1
        }
        window = p; view = v
        p.orderFrontRegardless()
        last = CACurrentMediaTime()
        timer = repeatingTimer(1.0 / 30) { [weak self] in self?.tick() }
        let ws = NSWorkspace.shared.notificationCenter
        ws.addObserver(self, selector: #selector(pause), name: NSWorkspace.screensDidSleepNotification, object: nil)
        ws.addObserver(self, selector: #selector(resume), name: NSWorkspace.screensDidWakeNotification, object: nil)
    }

    func stop() {
        timer?.invalidate(); timer = nil
        window?.orderOut(nil); window = nil; view = nil
        NSWorkspace.shared.notificationCenter.removeObserver(self)
    }

    @objc private func pause() { timer?.invalidate(); timer = nil }
    @objc private func resume() {
        guard window != nil, timer == nil else { return }
        last = CACurrentMediaTime()
        timer = repeatingTimer(1.0 / 30) { [weak self] in self?.tick() }
    }

    private func tick() {
        guard let w = window, let v = view else { return }
        let size = sprite.size
        let now = CACurrentMediaTime()
        let dt = min(0.1, now - last); last = now

        if home == nil || !NSScreen.screens.contains(where: { $0 == home }) { home = NSScreen.screens.first }
        guard let screen = home else { return }
        let mid = { self.x + size.width / 2 }

        // Look every frame while standing on a window, so it keeps up when the window is dragged.
        if now - lastScan >= (floor?.window != nil ? 0 : 0.3) {
            lastScan = now
            let scan = senses.chrome(now)
            floors = scan.floors.filter { $0.maxX - $0.minX >= size.width }
            if scan.front != target { target = scan.front; targetSince = now }
            targetScreen = scan.frontScreen
            if target != declined && declined != nil && target != nil { declined = nil }
            // Standing on a Chrome window: follow it as it moves or changes size, step over to
            // another visible part of it when a window covers this one, fall when it's gone.
            if let f = floor, let id = f.window, !dragging, !falling, hop == nil {
                let mine = floors.filter { $0.window == id }
                if let any = mine.first {
                    x += any.originX - f.originX
                    if let g = mine.first(where: { mid() >= $0.minX && mid() <= $0.maxX }) { floor = g }
                    else if let g = mine.min(by: { dist($0, mid()) < dist($1, mid()) }) {
                        if dist(g, mid()) <= size.width { floor = g; x = clampX(x, on: g, size.width) }
                        else { startHop(to: g, size: size) }
                    }
                } else { floor = nil; falling = true; vy = 0 }
            }
            // Carried to another screen with its window: that screen's bottom is now where it falls to.
            if let f = floor, f.window != nil,
               let s = NSScreen.screens.first(where: { $0.frame.contains(CGPoint(x: mid(), y: f.y + 1)) }) { home = s }
        }
        // Go to the frontmost Chrome window once it has been in front for a moment.
        if !dragging, !falling, hop == nil, landing <= 0, !held, let id = target, id != declined,
           floor?.window != id, now - targetSince >= 1.0 {
            let mine = floors.filter { $0.window == id }
            if let g = mine.min(by: { dist($0, mid()) < dist($1, mid()) }), let scr = targetScreen {
                if scr == screen { startHop(to: g, size: size) }
                else { dropIn(on: g, from: screen, to: scr, size: size) }
            } else if let scr = targetScreen, scr != screen {
                // No free part of its tab strip: wait at the bottom of that screen instead.
                dropIn(on: screenFloor(scr), from: screen, to: scr, size: size)
            }
        }
        let ground = screenFloor(home ?? screen)
        let here = floor ?? hop?.floor ?? ground
        let minX = here.minX, maxX = here.maxX - size.width
        var want = "idle", speed: CGFloat = 0
        var fixed: Int? = nil
        var scale: CGFloat = 1, alpha: CGFloat = 1

        if dragging {
            want = "fall"; fixed = 0
        } else if var h = hop {
            h.t += dt; hop = h
            let p = CGFloat(min(1, h.t / h.dur))
            let S = PET_SCALE, lerpX = h.from.x + (h.to.x - h.from.x) * p
            let hasClimb = sprite.frames["climb"]?.isEmpty == false
            // A parabola from a (q=0) through rise (q=0.5) to c (q=1).
            func arc(_ a: CGFloat, _ c: CGFloat, _ rise: CGFloat, _ q: CGFloat) -> CGFloat {
                let b = 2 * rise - (a + c) / 2
                return a * (1 - q) * (1 - q) + 2 * b * q * (1 - q) + c * q * q
            }
            dir = h.to.x >= h.from.x ? 1 : -1
            switch h.style {
            case "jump":
                // crouch, then one big leap (with a spin when the sheet has a "climb" row)
                let crouch = CGFloat(0.3 / h.dur)
                if p < crouch {
                    x = h.from.x; y = h.from.y
                    want = hasClimb ? "climb" : "fall"; fixed = hasClimb ? 0 : 2
                } else {
                    let q = (p - crouch) / (1 - crouch)
                    x = h.from.x + (h.to.x - h.from.x) * q
                    // rises fast and slows near the strip, with a little extra lift that
                    // never takes it off the top of the screen
                    let top = (home?.frame.maxY ?? .greatestFiniteMagnitude) - size.height
                    let lift = max(0, min(35 * S, top - h.to.y))
                    y = h.from.y + (h.to.y - h.from.y) * (1 - (1 - q) * (1 - q)) + lift * sin(.pi * q)
                    want = hasClimb ? "climb" : "fall"; fixed = hasClimb ? 1 + min(4, Int(q * 5)) : (q < 0.5 ? 0 : 1)
                }
            case "balloon":
                // floats up slowly, swaying
                let e = p * p * (3 - 2 * p)
                x = lerpX + sin(p * .pi * 3) * 8 * S
                y = h.from.y + (h.to.y - h.from.y) * e
                want = hasClimb ? "climb" : "idle"
            case "fly":
                // flaps up quickly at first, then eases in, bobbing
                let e = 1 - (1 - p) * (1 - p)
                x = lerpX + sin(p * .pi * 3) * 12 * S
                y = h.from.y + (h.to.y - h.from.y) * e + sin(p * .pi * 6) * 5 * S * (1 - p)
                want = hasClimb ? "climb" : "dance"
            case "steps":
                // several little hops, one after another
                let n = CGFloat(h.steps), k = min(n - 1, (p * n).rounded(.down)), q = p * n - k
                let ay = h.from.y + (h.to.y - h.from.y) * k / n, cy = h.from.y + (h.to.y - h.from.y) * (k + 1) / n
                x = lerpX
                y = arc(ay, cy, max(ay, cy) + 22 * S, q)
                want = "fall"; fixed = q < 0.5 ? 0 : 1
            case "poof":
                // shrinks away where it is, and pops up again on the strip
                if p < 0.35 { x = h.from.x; y = h.from.y; scale = 1 - p / 0.35 * 0.85; alpha = 1 - p / 0.35 }
                else if p < 0.55 { x = h.to.x; y = h.to.y; scale = 0.15; alpha = 0 }
                else { let q = (p - 0.55) / 0.45; x = h.to.x; y = h.to.y; scale = 0.15 + 0.85 * q; alpha = q }
                want = "idle"; fixed = 0
            default:
                // a small jump in an arc up (or down) to a strip close by
                x = lerpX
                y = arc(h.from.y, h.to.y, max(h.to.y, h.from.y) + 40 * S, p)
                want = "fall"; fixed = p < 0.5 ? 0 : 1
            }
            if p >= 1 { floor = h.floor; x = h.to.x; y = h.to.y; hop = nil; landing = h.style == "poof" ? 0 : 0.3; scale = 1; alpha = 1 }
        } else if falling {
            let before = y
            vy -= 2200 * dt; y += vy * dt
            want = "fall"; fixed = vy > -200 ? 0 : 1
            // Land on the first tab strip it passes through, else on the screen bottom.
            let hit = floors.filter { before >= $0.y - feet - 1 && y <= $0.y - feet && mid() >= $0.minX && mid() <= $0.maxX }
                .max(by: { $0.y < $1.y })
            if let f = hit {
                floor = f; y = f.y - feet; falling = false; landing = 0.3
                if f.window == declined { declined = nil }   // put back on it by hand: fine to stay
            }
            else if y <= ground.y - feet { floor = nil; y = ground.y - feet; falling = false; landing = 0.3 }
        } else if landing > 0 {
            landing -= dt
            want = "fall"; fixed = landing > 0.15 ? 2 : 3
        } else if held {
            want = "idle"
        } else if senses.music {
            want = "dance"
        } else if senses.away {
            want = "sleep"
        } else if now < restUntil {
            want = "idle"
        } else {
            want = "walk"; speed = 18 * max(0.7, PET_SCALE)
            if now >= nextRest {
                restUntil = now + Double.random(in: 3...8)
                nextRest = restUntil + Double.random(in: 10...25)
            }
            if Double.random(in: 0..<1) < dt * 0.05 { dir *= -1 }
        }

        x += dir * speed * CGFloat(dt)
        if !dragging && hop == nil {
            if x <= minX { x = minX; if speed > 0 { dir = 1 } }
            if x >= maxX { x = maxX; if speed > 0 { dir = -1 } }
        }
        if !dragging && !falling && hop == nil { y = (floor ?? ground).y - feet }

        guard let frames = sprite.frames[want] ?? sprite.frames["idle"], !frames.isEmpty else { return }
        if want != row { row = want; frame = 0; phase = 0 }
        if let f = fixed {
            frame = min(f, frames.count - 1)
        } else {
            phase += dt * (sprite.fps[want] ?? 8)
            while phase >= 1 {
                phase -= 1; frame += 1
                if frame >= frames.count { frame = 0 }
            }
        }
        frame = min(frame, frames.count - 1)
        v.show(frames[frame], mirrored: want == "walk" && dir < 0, scale: scale)
        if w.alphaValue != alpha { w.alphaValue = alpha }
        if w.frame.origin != NSPoint(x: x, y: y) {
            w.setFrameOrigin(NSPoint(x: x, y: y))
            if held { onMove?(self, w.frame) }
        }
    }

    // How far a pet centred at `cx` is from standing on `f` (0 when it's over it).
    private func dist(_ f: Floor, _ cx: CGFloat) -> CGFloat {
        cx < f.minX ? f.minX - cx : (cx > f.maxX ? cx - f.maxX : 0)
    }

    private func clampX(_ x: CGFloat, on f: Floor, _ w: CGFloat) -> CGFloat {
        min(max(x, f.minX), max(f.minX, f.maxX - w))
    }

    // Jump to a floor on the same screen: land as close to where it is as the floor allows.
    // A short way, or down, is a small hop. Far up (from the Dock to a tab strip) each
    // character goes its own way, slower, so it doesn't just shoot up the screen.
    private func startHop(to f: Floor, size: NSSize) {
        voice.want(.hop)
        let to = NSPoint(x: clampX(x, on: f, size.width), y: f.y - feet)
        let d = hypot(to.x - x, to.y - y), rise = Double((to.y - y) / PET_SCALE)
        let style = rise > 120 ? sprite.climb : "hop"
        let steps = max(2, Int((rise / 70).rounded(.up)))
        let dur: Double
        switch style {
        case "jump": dur = 1.5
        case "balloon": dur = min(3.5, max(1.8, rise / 280))
        case "fly": dur = min(2.6, max(1.3, rise / 380))
        case "steps": dur = Double(steps) * 0.34
        case "poof": dur = 1.0
        default: dur = min(0.9, 0.45 + Double(d) / 2000)
        }
        hop = (from: NSPoint(x: x, y: y), to: to, t: 0, dur: dur, floor: f, style: style, steps: steps)
        floor = nil
    }

    // On another screen: appear just above the floor there and drop onto it, at the same
    // place across the width (as a fraction of the screen). The panel may sit partly above
    // the menu bar (PetPanel doesn't constrain it), so a strip right under it still works.
    private func dropIn(on f: Floor, from: NSScreen, to screen: NSScreen, size: NSSize) {
        let frac = (x - from.frame.minX) / max(1, from.frame.width)
        home = screen
        x = clampX(screen.frame.minX + frac * screen.frame.width, on: f, size.width)
        y = f.y + size.height * 0.8
        floor = nil; hop = nil; falling = true; vy = 0
    }
}
