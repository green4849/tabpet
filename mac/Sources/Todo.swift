// A small checklist that opens over a character when you click it.
// No dates, no "save" step: every keystroke, tick and delete goes to
// ~/.tabpet/todo.json within a moment, and nothing is ever removed unless you
// remove it. If the file changes from outside (edited by hand, another tool), the open
// list follows it.
import AppKit

struct TodoItem: Codable, Equatable { var id: String; var text: String; var done: Bool }
struct TodoFile: Codable, Equatable { var items: [TodoItem] = []; var draft: String = "" }

let TODO_PATH = "\(PET_DIR)/todo.json"

// MARK: - The file

final class TodoStore {
    private(set) var data = TodoFile()
    var onExternalChange: () -> Void = {}
    var onSaveError: (Bool) -> Void = { _ in }         // true: the last write failed
    private let url = URL(fileURLWithPath: TODO_PATH)
    private var lastDisk: Data?                         // what we last read or wrote: our own writes aren't news
    private var saveWork: DispatchWorkItem?
    private var dirty = false                           // changed here and not on disk yet (stays set if a write fails)
    private var blocked = false                         // an unreadable file we couldn't copy aside: never write over it
    private var dirSource: DispatchSourceFileSystemObject?
    private var fileSource: DispatchSourceFileSystemObject?

    init() {
        if let d = try? Data(contentsOf: url) {
            if let f = try? JSONDecoder().decode(TodoFile.self, from: d) { data = f; lastDisk = d }
            else {
                // Can't read it: keep a copy before anything is written over it.
                let aside = url.deletingLastPathComponent().appendingPathComponent("todo.unreadable-\(Int(Date().timeIntervalSince1970)).json")
                do { try FileManager.default.copyItem(at: url, to: aside) }
                catch { blocked = true; NSLog("TabPet todo: \(url.path) is unreadable and couldn't be copied aside; not writing to it") }
            }
        }
        watch()
    }

    func change(_ f: (inout TodoFile) -> Void) {
        f(&data)
        dirty = true
        saveWork?.cancel()
        let w = DispatchWorkItem { [weak self] in self?.saveNow() }
        saveWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: w)
    }

    // Write anything still waiting (closing the list, quitting).
    func flush() { if dirty { saveNow() } }

    private func saveNow() {
        saveWork?.cancel(); saveWork = nil
        guard !blocked else { onSaveError(true); return }
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        guard let d = try? enc.encode(data) else { onSaveError(true); return }
        if d == lastDisk { dirty = false; return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        do { try d.write(to: url, options: .atomic); lastDisk = d; dirty = false; onSaveError(false) }
        catch { NSLog("TabPet todo: can't write \(url.path): \(error)"); onSaveError(true) }
    }

    // Someone else changed the file: take it, unless something changed here isn't written yet
    // (that one wins; it's what you just did). A half-written or broken file is skipped.
    private func reread() {
        guard !dirty, !blocked, let d = try? Data(contentsOf: url), d != lastDisk,
              let f = try? JSONDecoder().decode(TodoFile.self, from: d) else { return }
        lastDisk = d
        if f != data { data = f; onExternalChange() }
    }

    // The folder sees files swapped in (how most editors and our own writes save);
    // the file itself sees writes in place.
    private func watch() {
        let dir = url.deletingLastPathComponent().path
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let fd = open(dir, O_EVTONLY)
        guard fd >= 0 else { return }
        let s = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: .write, queue: .main)
        s.setEventHandler { [weak self] in self?.reread(); self?.watchFile() }
        s.setCancelHandler { close(fd) }
        s.resume()
        dirSource = s
        watchFile()
    }
    private func watchFile() {
        fileSource?.cancel(); fileSource = nil
        let fd = open(url.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let s = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write, .extend, .delete, .rename], queue: .main)
        s.setEventHandler { [weak self, weak s] in
            self?.reread()
            if let e = s?.data, !e.intersection([.delete, .rename]).isEmpty { self?.watchFile() }
        }
        s.setCancelHandler { close(fd) }
        s.resume()
        fileSource = s
    }
}

// MARK: - Views

final class FlippedView: NSView { override var isFlipped: Bool { true } }

final class TodoPanel: NSPanel {
    var onCancel: () -> Void = {}
    override var canBecomeKey: Bool { true }
    override func cancelOperation(_ sender: Any?) { onCancel() }
    override func constrainFrameRect(_ r: NSRect, to screen: NSScreen?) -> NSRect { r }
}

// The round tick box. Fills with the character's colour when done.
final class CheckView: NSView {
    var onToggle: () -> Void = {}
    var accent: NSColor = .controlAccentColor { didSet { paint(animated: false) } }
    private(set) var done = false
    private let ring = CAShapeLayer(), mark = CAShapeLayer()

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        let d: CGFloat = 14, o = CGPoint(x: (frame.width - d) / 2, y: (frame.height - d) / 2)
        ring.path = CGPath(ellipseIn: CGRect(x: o.x, y: o.y, width: d, height: d), transform: nil)
        ring.lineWidth = 1.3
        let m = CGMutablePath()
        m.move(to: CGPoint(x: o.x + 3.8, y: o.y + 7.2))
        m.addLine(to: CGPoint(x: o.x + 6.1, y: o.y + 4.8))
        m.addLine(to: CGPoint(x: o.x + 10.4, y: o.y + 9.4))
        mark.path = m
        mark.fillColor = nil
        mark.lineWidth = 1.6
        mark.lineCap = .round; mark.lineJoin = .round
        mark.strokeColor = NSColor.white.cgColor
        layer?.addSublayer(ring); layer?.addSublayer(mark)
        paint(animated: false)
    }
    required init?(coder: NSCoder) { nil }

    func set(_ on: Bool, animated: Bool) { guard on != done || !animated else { return }; done = on; paint(animated: animated) }

    private func paint(animated: Bool) {
        CATransaction.begin()
        if animated { CATransaction.setAnimationDuration(0.15) } else { CATransaction.setDisableActions(true) }
        effectiveAppearance.performAsCurrentDrawingAppearance {
            ring.strokeColor = (done ? accent : NSColor.tertiaryLabelColor).cgColor
            ring.fillColor = (done ? accent : NSColor.clear).cgColor
        }
        mark.opacity = done ? 1 : 0
        // a dark tick on light colours (a pale yellow), white on the rest
        let c = accent.usingColorSpace(.sRGB) ?? accent
        let light = 0.299 * c.redComponent + 0.587 * c.greenComponent + 0.114 * c.blueComponent > 0.7
        mark.strokeColor = (light ? NSColor(white: 0.2, alpha: 1) : NSColor.white).cgColor
        CATransaction.commit()
    }
    override func viewDidChangeEffectiveAppearance() { paint(animated: false) }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with e: NSEvent) { onToggle() }
}

func plainField(_ f: NSTextField) {
    f.isBordered = false; f.isBezeled = false; f.drawsBackground = false
    f.focusRingType = .none
    f.font = .systemFont(ofSize: 13)
    f.textColor = .labelColor
    f.isEditable = true; f.isSelectable = true
    f.lineBreakMode = .byTruncatingTail
    f.cell?.usesSingleLineMode = true; f.cell?.wraps = false; f.cell?.isScrollable = true
}

// One line: tick box, the words (click to change them), and × while the mouse is over it.
final class TodoRow: NSView {
    let id: String
    let check: CheckView
    let field = NSTextField()
    let del = NSButton()
    var onDelete: () -> Void = {}
    private var hovering = false
    var editing = false { didSet { del.isHidden = !(hovering || editing) } }

    init(id: String, width: CGFloat, height: CGFloat) {
        self.id = id
        check = CheckView(frame: NSRect(x: 4, y: 0, width: 26, height: height))
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: height))
        plainField(field)
        field.frame = NSRect(x: 32, y: (height - 18) / 2, width: width - 32 - 30, height: 18)
        let cfg = NSImage.SymbolConfiguration(pointSize: 9, weight: .semibold)
        del.image = NSImage(systemSymbolName: "xmark", accessibilityDescription: "지우기")?.withSymbolConfiguration(cfg)
        del.isBordered = false
        del.contentTintColor = .tertiaryLabelColor
        del.frame = NSRect(x: width - 28, y: (height - 20) / 2, width: 20, height: 20)
        del.target = self; del.action = #selector(deleteTapped)
        del.isHidden = true
        del.toolTip = "지우기"
        addSubview(check); addSubview(field); addSubview(del)
    }
    required init?(coder: NSCoder) { nil }

    // Show an item. Leaves the words alone while you're typing in them.
    func show(_ item: TodoItem, accent: NSColor, editing: Bool, animated: Bool) {
        check.accent = accent
        check.set(item.done, animated: animated)
        guard !editing else { return }
        var a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 13),
                                                 .foregroundColor: item.done ? NSColor.tertiaryLabelColor : NSColor.labelColor]
        if item.done { a[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
        field.attributedStringValue = NSAttributedString(string: item.text, attributes: a)
    }

    @objc private func deleteTapped() { onDelete() }
    override func updateTrackingAreas() {
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
        super.updateTrackingAreas()
    }
    override func mouseEntered(with e: NSEvent) { hovering = true; del.isHidden = false }
    override func mouseExited(with e: NSEvent) { hovering = false; del.isHidden = !editing }
}

// MARK: - The list

final class Checklist: NSObject, NSTextFieldDelegate {
    let store = TodoStore()
    private let W: CGFloat = 240, ROW: CGFloat = 26, PAD: CGFloat = 6, TAIL: CGFloat = 8, MAX_ROWS = 10

    private var panel: TodoPanel?
    private let effect = NSVisualEffectView()
    private let scroll = NSScrollView()
    private let list = FlippedView()
    private let line = NSBox()
    private let plus = NSImageView()
    private let input = NSTextField()
    private var rows: [TodoRow] = []

    private weak var pet: Pet?                          // the character it points at (nil: the menu bar icon)
    private var target = NSRect.zero                    // what it points at, on screen
    private var color: NSColor = .controlAccentColor
    private var monitors: [Any] = []
    private var reloadAfterEdit = false

    var isOpen: Bool { panel?.isVisible == true }
    var staysOpenOnOutsideClick = false                 // for trying it out from a test program

    override init() {
        super.init()
        store.onExternalChange = { [weak self] in self?.fileChanged() }
        store.onSaveError = { [weak self] failed in
            self?.plus.contentTintColor = failed ? .systemRed : .tertiaryLabelColor
            self?.plus.toolTip = failed ? "저장하지 못했어요: \(TODO_PATH)" : nil
        }
    }

    // Click on a character: open over it, or close if it's already open there.
    func toggle(_ p: Pet) {
        if isOpen && pet === p { close(); return }
        guard let r = p.screenRect else { return }
        open(at: r, pet: p)
    }
    // From the menu bar menu: over the first character if one is out, else under the icon.
    func open(at r: NSRect, pet p: Pet?) {
        if pet !== p { pet?.held = false }
        pet = p; target = r
        color = p?.accent ?? .controlAccentColor
        p?.held = true
        build()
        reloadRows()
        layout()
        guard let w = panel else { return }
        if !w.isVisible { w.orderFrontRegardless() }
        w.makeKey()
        w.makeFirstResponder(input)
        input.currentEditor()?.selectedRange = NSRange(location: (input.stringValue as NSString).length, length: 0)
        watchOutside()
    }

    func close() {
        guard let w = panel, w.isVisible else { return }
        w.makeFirstResponder(nil)                       // finish the line being typed in
        store.flush()
        w.orderOut(nil)
        monitors.forEach { NSEvent.removeMonitor($0) }; monitors = []
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        pet?.held = false; pet = nil
    }

    // The character it points at moved (its Chrome window was dragged) or was put away.
    func petMoved(_ p: Pet, to r: NSRect) { guard isOpen, pet === p else { return }; target = r; layout() }
    func petGone(_ p: Pet) { if pet === p { close() } }

    // Clicking anywhere else, or switching to another app, closes it.
    private func watchOutside() {
        guard monitors.isEmpty, !staysOpenOnOutsideClick else { return }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] _ in self?.close() }) {
            monitors.append(m)
        }
        // Inside this app too, except the list itself and a plain click on a character
        // (that one opens it there or closes it, via toggle).
        if let m = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] e in
            guard let self, let w = self.panel else { return e }
            let petClick = e.window is PetPanel && e.type == .leftMouseDown && !e.modifierFlags.contains(.control)
            if e.window !== w && !petClick { self.close() }
            return e
        }) { monitors.append(m) }
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(otherApp(_:)),
                                                          name: NSWorkspace.didActivateApplicationNotification, object: nil)
    }
    @objc private func otherApp(_ n: Notification) {
        let a = n.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
        if a?.processIdentifier != getpid() { close() }
    }

    private func build() {
        guard panel == nil else { return }
        let p = TodoPanel(contentRect: NSRect(x: 0, y: 0, width: W, height: 100),
                          styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 1)
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        p.becomesKeyOnlyIfNeeded = false
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        p.onCancel = { [weak self] in self?.close() }

        let root = NSView(frame: p.contentRect(forFrameRect: p.frame))
        effect.material = .popover
        effect.blendingMode = .behindWindow
        effect.state = .active
        root.addSubview(effect)

        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.scrollerStyle = .overlay
        scroll.borderType = .noBorder
        scroll.documentView = list
        root.addSubview(scroll)

        line.boxType = .separator
        root.addSubview(line)

        plus.image = NSImage(systemSymbolName: "plus", accessibilityDescription: nil)?
            .withSymbolConfiguration(.init(pointSize: 11, weight: .medium))
        plus.contentTintColor = .tertiaryLabelColor
        root.addSubview(plus)

        plainField(input)
        input.placeholderString = "적기…"
        input.delegate = self
        input.stringValue = store.data.draft
        root.addSubview(input)

        p.contentView = root
        panel = p
    }

    // MARK: rows

    private func reloadRows() {
        rows.forEach { $0.removeFromSuperview() }
        rows = store.data.items.map { item in
            let r = TodoRow(id: item.id, width: W, height: ROW)
            r.field.delegate = self
            r.check.onToggle = { [weak self, weak r] in if let r { self?.toggleDone(r.id) } }
            r.onDelete = { [weak self, weak r] in if let r { self?.remove(r.id) } }
            r.show(item, accent: color, editing: false, animated: false)
            list.addSubview(r)
            return r
        }
        if !isEditing(input) { input.stringValue = store.data.draft }
    }

    private func layout() {
        guard let p = panel else { return }
        let n = rows.count
        let listH = CGFloat(min(n, MAX_ROWS)) * ROW
        let gap: CGFloat = n > 0 ? 5 : 0
        let bubbleH = PAD + listH + gap + ROW + PAD
        let H = bubbleH + TAIL

        let screen = NSScreen.screens.first(where: { $0.frame.contains(NSPoint(x: target.midX, y: target.midY)) })
            ?? NSScreen.main ?? NSScreen.screens[0]
        let vf = screen.visibleFrame, sf = screen.frame
        // Above what it points at; below when there's no room (a character up in a tab strip, the menu bar icon).
        var below = target.maxY + H > sf.maxY - 4 || target.maxY > vf.maxY
        var y = below ? target.minY - H : target.maxY
        if !below && y + H > sf.maxY { below = true; y = target.minY - H }
        y = max(sf.minY, y)
        let x = min(max(target.midX - W / 2, vf.minX + 6), vf.maxX - W - 6)
        let tailX = min(max(target.midX - x, 18), W - 18)
        p.setFrame(NSRect(x: x, y: y, width: W, height: H), display: true)

        let root = p.contentView!
        root.frame = NSRect(x: 0, y: 0, width: W, height: H)
        effect.frame = root.bounds
        effect.maskImage = bubble(NSSize(width: W, height: H), tailX: tailX, down: !below)

        let base = below ? 0 : TAIL                      // bottom of the bubble
        let inputY = base + PAD
        plus.frame = NSRect(x: 4 + 6, y: inputY + (ROW - 14) / 2, width: 14, height: 14)
        input.frame = NSRect(x: 32, y: inputY + (ROW - 18) / 2, width: W - 32 - 12, height: 18)
        line.isHidden = n == 0
        line.frame = NSRect(x: 12, y: inputY + ROW + gap / 2, width: W - 24, height: 1)
        scroll.frame = NSRect(x: 0, y: inputY + ROW + gap, width: W, height: listH)
        list.frame = NSRect(x: 0, y: 0, width: W, height: CGFloat(n) * ROW)
        for (i, r) in rows.enumerated() { r.frame = NSRect(x: 0, y: CGFloat(i) * ROW, width: W, height: ROW) }
        p.invalidateShadow()
    }

    // The bubble: a rounded card with a small tail toward the character.
    private func bubble(_ s: NSSize, tailX: CGFloat, down: Bool) -> NSImage {
        let tail = TAIL
        let img = NSImage(size: s, flipped: false) { _ in
            let card = NSRect(x: 0, y: down ? tail : 0, width: s.width, height: s.height - tail)
            let path = NSBezierPath(roundedRect: card, xRadius: 14, yRadius: 14)
            let t = NSBezierPath()
            if down {
                t.move(to: NSPoint(x: tailX - 8, y: tail + 1))
                t.line(to: NSPoint(x: tailX, y: 0))
                t.line(to: NSPoint(x: tailX + 8, y: tail + 1))
            } else {
                t.move(to: NSPoint(x: tailX - 8, y: s.height - tail - 1))
                t.line(to: NSPoint(x: tailX, y: s.height))
                t.line(to: NSPoint(x: tailX + 8, y: s.height - tail - 1))
            }
            t.close()
            NSColor.black.setFill()
            path.fill(); t.fill()
            return true
        }
        return img
    }

    // MARK: changes

    private func index(_ id: String) -> Int? { store.data.items.firstIndex(where: { $0.id == id }) }
    private func row(_ id: String) -> TodoRow? { rows.first(where: { $0.id == id }) }

    private func toggleDone(_ id: String) {
        guard let i = index(id) else { return }
        store.change { $0.items[i].done.toggle() }
        if let r = row(id) { r.show(store.data.items[i], accent: color, editing: isEditing(r.field), animated: true) }
    }

    private func remove(_ id: String) {
        guard let i = index(id) else { return }
        store.change { $0.items.remove(at: i) }
        let keep = panel?.firstResponder
        reloadRows(); layout()
        if keep == nil || !(rows.contains { isEditing($0.field) }) { panel?.makeFirstResponder(input) }
    }

    private func add() {
        let text = input.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        store.change { $0.items.append(TodoItem(id: UUID().uuidString, text: text, done: false)); $0.draft = "" }
        input.stringValue = ""
        reloadRows(); layout()
        if let r = rows.last { list.scrollToVisible(r.frame) }
    }

    private func fileChanged() {
        guard isOpen else { return }
        // Typing in a line: wait until you leave it, so the words under the cursor don't jump.
        if rows.contains(where: { isEditing($0.field) }) { reloadAfterEdit = true; return }
        if isEditing(input) && input.stringValue != store.data.draft {
            // What's in the box right now is what you're typing: keep it.
            let typed = input.stringValue
            store.change { $0.draft = typed }
        }
        reloadRows(); layout()
    }

    private func isEditing(_ f: NSTextField) -> Bool {
        guard let ed = panel?.firstResponder as? NSTextView, ed.isFieldEditor else { return false }
        return ed.delegate as? NSTextField === f
    }
    private func rowOf(_ f: NSTextField) -> TodoRow? { rows.first(where: { $0.field === f }) }

    // MARK: NSTextFieldDelegate

    func controlTextDidBeginEditing(_ n: Notification) {
        (panel?.firstResponder as? NSTextView)?.insertionPointColor = color
        if let f = n.object as? NSTextField { rowOf(f)?.editing = true }
    }

    func controlTextDidChange(_ n: Notification) {
        guard let f = n.object as? NSTextField else { return }
        if f === input { let s = f.stringValue; store.change { $0.draft = s }; return }
        guard let r = rowOf(f), let i = index(r.id) else { return }
        let s = f.stringValue
        store.change { $0.items[i].text = s }
    }

    func controlTextDidEndEditing(_ n: Notification) {
        guard let f = n.object as? NSTextField, let r = rowOf(f) else { return }
        r.editing = false
        if let i = index(r.id) { r.show(store.data.items[i], accent: color, editing: false, animated: false) }
        if reloadAfterEdit {
            reloadAfterEdit = false
            DispatchQueue.main.async { [weak self] in self?.reloadRows(); self?.layout() }
        }
    }

    func control(_ c: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        // Enter while a Korean syllable is still being put together finishes the syllable only.
        if textView.hasMarkedText() { return false }
        switch sel {
        case #selector(NSResponder.insertNewline(_:)):
            if c === input { add(); return true }
            // Enter on a line you emptied: that line goes. Otherwise back to the new-line box.
            if let f = c as? NSTextField, let r = rowOf(f), f.stringValue.trimmingCharacters(in: .whitespaces).isEmpty {
                let id = r.id
                panel?.makeFirstResponder(input)
                remove(id)
            } else { panel?.makeFirstResponder(input) }
            return true
        case #selector(NSResponder.cancelOperation(_:)):
            close(); return true
        default:
            return false
        }
    }
}
