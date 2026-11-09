// Entry point: a menu bar icon whose menu turns the characters and their voice on or
// off. Each character is a Pet (Pet.swift).
import AppKit

final class App: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let menu = NSMenu()
    let voice = Voice()
    lazy var senses = Senses(voice: voice)
    var pets: [String: Pet] = [:]
    var timer: Timer?

    var shown: Bool {
        get { UserDefaults.standard.object(forKey: "petShown") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "petShown"); syncPets() }
    }
    // The characters out on screen, in the order they were picked.
    var chosen: [String] {
        get { UserDefaults.standard.stringArray(forKey: "petCharacters") ?? [PET_NAME] }
        set { UserDefaults.standard.set(newValue, forKey: "petCharacters"); syncPets() }
    }

    // Start the chosen ones that aren't out yet, put away the rest.
    func syncPets() {
        defer { updateIcon() }
        let want = shown ? chosen : []
        for (name, pet) in pets where !want.contains(name) { pet.stop(); pets[name] = nil }
        for (i, name) in want.enumerated() where pets[name] == nil {
            guard let pet = Pet(name, voice: voice, senses: senses, slot: i) else {
                NSLog("TabPet: could not read the sheet for %@", name); continue
            }
            pets[name] = pet; pet.start()
        }
    }

    func applicationDidFinishLaunching(_ n: Notification) {
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        item.button?.toolTip = "TabPet"
        syncPets()
        // What the computer is doing, and the sounds the pets want, for all of them at once.
        timer = repeatingTimer(0.1) { [weak self] in
            guard let self else { return }
            let now = CACurrentMediaTime()
            self.senses.update(now)
            // Hidden everywhere means a full-screen app is in front: stay quiet too.
            self.voice.flush(now: now, music: self.senses.music, away: self.senses.away,
                             visible: self.pets.values.contains { $0.isVisible })
        }
    }

    // The menu bar icon: the face of the first character picked, in colour, from its first
    // idle frame (the part of the picture from the top of its head down, as a square).
    // A paw print when no sheet can be read.
    func updateIcon() {
        if let name = chosen.first(where: { characterDir($0) != nil }),
           let frame = Sprite(name: name)?.frames["idle"]?.first, let face = faceCrop(frame) {
            let img = NSImage(cgImage: face, size: NSSize(width: 18, height: 18))
            img.isTemplate = false
            item.button?.image = img
        } else {
            let img = NSImage(systemSymbolName: "pawprint", accessibilityDescription: "TabPet")
            img?.isTemplate = true
            item.button?.image = img
        }
    }

    // The square from the top of the character down, as wide as it is (taller for long ears).
    func faceCrop(_ img: CGImage) -> CGImage? {
        let w = img.width, h = img.height
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let data = ctx.data else { return nil }
        ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
        let px = data.bindMemory(to: UInt8.self, capacity: w * h * 4)
        var minX = w, maxX = -1, minY = h, maxY = -1      // rows from the top
        for row in 0..<h {
            for x in 0..<w where px[(row * w + x) * 4 + 3] > 40 {
                minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, row); maxY = max(maxY, row)
            }
        }
        guard maxX >= minX, maxY >= minY else { return nil }
        let bw = Double(maxX - minX + 1), bh = Double(maxY - minY + 1)
        let side = max(bw, 0.68 * bh), cx = Double(minX + maxX + 1) / 2
        // the image's own rows also run from the top
        return img.cropping(to: CGRect(x: cx - side / 2, y: Double(minY), width: side, height: side).integral)
    }

    func menuNeedsUpdate(_ m: NSMenu) {
        m.removeAllItems()
        let list = petCharacters()
        let petItem = action(list.isEmpty ? "화면 캐릭터 (그림 없음: \(PET_DIR))" : "화면에 캐릭터 띄우기", #selector(togglePet))
        petItem.state = shown && !list.isEmpty ? .on : .off
        petItem.isEnabled = !list.isEmpty
        m.addItem(petItem)
        let voiceItem = action("캐릭터 소리", #selector(togglePetSound))
        voiceItem.state = voice.on ? .on : .off
        voiceItem.isEnabled = !list.isEmpty && shown
        m.addItem(voiceItem)
        if list.count > 1 {
            // Tick as many as you like; at least one stays.
            let sub = NSMenu()
            sub.autoenablesItems = false
            let picked = chosen
            for name in list {
                let i = action(characterLabel(name), #selector(choose(_:)))
                i.representedObject = name
                i.state = picked.contains(name) ? .on : .off
                i.isEnabled = !(picked == [name])
                sub.addItem(i)
            }
            let chars = NSMenuItem(title: "캐릭터 추가", action: nil, keyEquivalent: "")
            chars.submenu = sub
            m.addItem(chars)
        }
        m.addItem(.separator())
        m.addItem(action("종료", #selector(quit), key: "q"))
    }

    func action(_ s: String, _ sel: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: s, action: sel, keyEquivalent: key); i.target = self; return i
    }

    @objc func choose(_ sender: NSMenuItem) {
        guard let name = sender.representedObject as? String else { return }
        var c = chosen
        if let i = c.firstIndex(of: name) { if c.count > 1 { c.remove(at: i) } } else { c.append(name) }
        chosen = c
    }
    @objc func togglePet() { shown.toggle() }
    @objc func togglePetSound() { voice.on.toggle() }
    @objc func quit() { NSApp.terminate(nil) }
}

// One copy of the app at a time: a second copy would put the characters on screen twice.
if let id = Bundle.main.bundleIdentifier,
   NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0.processIdentifier != getpid() }) {
    exit(0)
}

let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
