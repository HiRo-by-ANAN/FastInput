import AppKit
import ApplicationServices
import CoreImage
import Foundation

// The computer window is AppKit. Only the phone uses the browser pages served by
// fastinput-server. The desktop credential never appears in a browser history.

private struct Snapshot: Decodable, Equatable {
    let text: String
    let revision: UInt64
    let generation: String
}

private struct Pairing: Decodable {
    let url: String
    let pin: String?
    let expires_seconds: Int
    let lan_reachable: Bool
}

private struct PendingDevice: Decodable {
    let id: String
    let code: String
    let ip: String
}

private struct PairedDevice: Decodable {
    let id: String
    let label: String
}

private struct StatePayload: Decodable {
    let status: String
    let role: String?
    let text: String?
    let revision: UInt64?
    let generation: String?
    let pending: [PendingDevice]?
    let devices: [PairedDevice]?
    let pairing: Pairing?

    var snapshot: Snapshot? {
        guard let text = text, let revision = revision, let generation = generation else { return nil }
        return Snapshot(text: text, revision: revision, generation: generation)
    }
}

private final class ActionButton: NSButton {
    var onPress: (() -> Void)?

    init(_ title: String, onPress: @escaping () -> Void) {
        super.init(frame: .zero)
        self.title = title
        self.onPress = onPress
        bezelStyle = .rounded
        target = self
        action = #selector(pressed)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    @objc private func pressed() { onPress?() }
}

private func label(_ text: String, size: CGFloat = 13, weight: NSFont.Weight = .regular,
                   color: NSColor = .labelColor) -> NSTextField {
    let field = NSTextField(labelWithString: text)
    field.font = .systemFont(ofSize: size, weight: weight)
    field.textColor = color
    field.lineBreakMode = .byWordWrapping
    field.maximumNumberOfLines = 0
    return field
}

private func stack(_ views: [NSView], orientation: NSUserInterfaceLayoutOrientation,
                   spacing: CGFloat = 8) -> NSStackView {
    let result = NSStackView(views: views)
    result.orientation = orientation
    result.spacing = spacing
    result.alignment = orientation == .vertical ? .leading : .centerY
    result.distribution = .fill
    result.detachesHiddenViews = true
    return result
}

private final class MainWindowController: NSObject, NSTextViewDelegate, NSWindowDelegate {
    private(set) var window: NSWindow!
    private var server: Process?
    private var serverOutput = Data()
    private var serverError = Data()
    private var baseURL: URL?
    private var sessionToken: String?
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.timeoutIntervalForRequest = 6
        config.urlCache = nil
        return URLSession(configuration: config)
    }()
    private var pollTimer: Timer?
    private var saveTimer: Timer?
    private var polling = false
    private var remote: Snapshot?
    private var editBase: Snapshot?
    private var dirty = false
    private var conflict = false
    private var saving = false
    private var sendingText: String?
    private var applyingRemote = false
    private var currentPairingURL: String?
    private var pendingSignature = ""
    private var devicesSignature = ""
    private var online = false


    private let connectionLabel = label("正在啟動…", size: 12, weight: .medium, color: .secondaryLabelColor)
    private let editorTitle = label("同步文字", size: 20, weight: .semibold)
    private let countLabel = label("0 字", size: 12, color: .secondaryLabelColor)
    private let statusLabel = label("啟動後會先讀取電腦保存的內容", size: 12, color: .secondaryLabelColor)
    private let conflictMessage = label("手機已更新內容。請選擇要採用哪一份文字。", size: 12)
    private var conflictBar: NSStackView!
    private let pairingStatus = label("正在取得配對資訊…", size: 12, color: .secondaryLabelColor)
    private let pairingURL = label("", size: 11, color: .secondaryLabelColor)
    private let pinInstructionLabel = label("其他電腦連線配對碼", size: 11, weight: .medium, color: .secondaryLabelColor)
    private let pairingPinLabel = label("", size: 24, weight: .bold, color: .controlAccentColor)
    private let qrImage = NSImageView()
    private let pendingRows = stack([], orientation: .vertical, spacing: 8)
    private let deviceRows = stack([], orientation: .vertical, spacing: 8)
    private let editor = NSTextView(frame: .zero)
    private var undoButton: ActionButton!
    private var redoButton: ActionButton!
    private var pairingDetailBox: NSStackView!
    private var pairingCollapsedBox: NSStackView!
    private var pairingAutoCollapseTimer: Timer?
    private var isPairingCollapsed: Bool = false
    private var sidebarPane: NSView!
    private var sidebarToggle: ActionButton!
    private var sidebarOpenConstraints: [NSLayoutConstraint] = []
    private var sidebarClosedConstraints: [NSLayoutConstraint] = []
    private var isSidebarCollapsed = false

    override init() {
        super.init()
        buildWindow()
    }

    private func buildWindow() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 740),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "FastInput"
        window.minSize = NSSize(width: 760, height: 570)
        window.center()
        window.delegate = self

        let root = NSView()
        root.translatesAutoresizingMaskIntoConstraints = false
        window.contentView = root

        let title = label("FastInput", size: 26, weight: .bold)
        let subtitle = label("用手機打字，在電腦接著編輯", size: 12, color: .secondaryLabelColor)
        let titleGroup = stack([title, subtitle], orientation: .vertical, spacing: 2)
        sidebarToggle = ActionButton("收起配對與裝置") { [weak self] in
            guard let self = self else { return }
            self.setSidebarCollapsed(!self.isSidebarCollapsed)
        }
        let header = stack([titleGroup, NSView(), sidebarToggle, connectionLabel], orientation: .horizontal, spacing: 12)
        header.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(header)

        let left = buildEditorPane()
        let right = buildPairingPane()
        sidebarPane = right
        left.translatesAutoresizingMaskIntoConstraints = false
        right.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(left)
        root.addSubview(right)
        let rightWidth = right.widthAnchor.constraint(equalToConstant: 304)
        rightWidth.priority = .defaultHigh
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 24),
            header.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24),
            header.topAnchor.constraint(equalTo: root.topAnchor, constant: 20),
            left.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 24),
            left.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 20),
            left.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -20),
            left.widthAnchor.constraint(greaterThanOrEqualToConstant: 360),
        ])
        sidebarOpenConstraints = [
            right.leadingAnchor.constraint(equalTo: left.trailingAnchor, constant: 22),
            right.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24),
            right.topAnchor.constraint(equalTo: left.topAnchor),
            right.bottomAnchor.constraint(equalTo: left.bottomAnchor),
            rightWidth,
        ]
        sidebarClosedConstraints = [
            left.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24),
        ]
        setSidebarCollapsed(UserDefaults.standard.bool(forKey: "FastInputSidebarCollapsed"))
    }

    private func setSidebarCollapsed(_ collapsed: Bool) {
        guard sidebarPane != nil else { return }
        NSLayoutConstraint.deactivate(collapsed ? sidebarOpenConstraints : sidebarClosedConstraints)
        if !collapsed {
            let contentSize = window.contentView?.bounds.size ?? NSSize(width: 760, height: 570)
            if contentSize.width < 760 || contentSize.height < 570 {
                window.setContentSize(NSSize(width: max(contentSize.width, 760),
                                             height: max(contentSize.height, 570)))
            }
        }
        sidebarPane.isHidden = collapsed
        NSLayoutConstraint.activate(collapsed ? sidebarClosedConstraints : sidebarOpenConstraints)
        isSidebarCollapsed = collapsed
        sidebarToggle.title = collapsed ? "顯示配對與裝置" : "收起配對與裝置"
        window.minSize = collapsed ? NSSize(width: 540, height: 500) : NSSize(width: 760, height: 570)
        UserDefaults.standard.set(collapsed, forKey: "FastInputSidebarCollapsed")
    }

    private func buildEditorPane() -> NSView {
        let pane = NSView()

        let heading = stack([editorTitle, NSView(), countLabel], orientation: .horizontal)
        let intro = label("這裡的文字會即時同步到已配對的手機。", size: 12, color: .secondaryLabelColor)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .bezelBorder
        scroll.drawsBackground = true
        scroll.backgroundColor = .textBackgroundColor
        editor.font = .systemFont(ofSize: 16)
        editor.textColor = .textColor
        editor.isRichText = false
        editor.isAutomaticQuoteSubstitutionEnabled = false
        editor.isAutomaticDashSubstitutionEnabled = false
        editor.isAutomaticTextReplacementEnabled = false
        editor.allowsUndo = true
        editor.isEditable = false
        editor.isVerticallyResizable = true
        editor.isHorizontallyResizable = false
        editor.autoresizingMask = [.width]
        editor.textContainerInset = NSSize(width: 16, height: 16)
        editor.textContainer?.widthTracksTextView = true
        editor.delegate = self
        scroll.documentView = editor

        let adoptButton = ActionButton("採用手機內容") { [weak self] in self?.adoptRemote() }
        let keepButton = ActionButton("保留我的文字") { [weak self] in self?.keepLocal() }
        let conflictActions = stack([adoptButton, keepButton], orientation: .horizontal, spacing: 6)
        conflictBar = stack([conflictMessage, conflictActions], orientation: .vertical, spacing: 5)
        conflictBar.isHidden = true

        undoButton = ActionButton("復原") { [weak self] in self?.editor.undoManager?.undo() }
        redoButton = ActionButton("重做") { [weak self] in self?.editor.undoManager?.redo() }
        let copyButton = ActionButton("複製全文") { [weak self] in self?.copyText() }
        let clearButton = ActionButton("清空") { [weak self] in self?.clearText() }
        let toolbar = stack([undoButton, redoButton, NSView(), copyButton, clearButton], orientation: .horizontal, spacing: 7)

        let content = stack([heading, intro, conflictBar, scroll, toolbar, statusLabel],
                            orientation: .vertical, spacing: 9)
        content.alignment = .width
        content.translatesAutoresizingMaskIntoConstraints = false
        pane.addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: pane.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: pane.trailingAnchor),
            content.topAnchor.constraint(equalTo: pane.topAnchor),
            content.bottomAnchor.constraint(equalTo: pane.bottomAnchor),
            scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 260),
        ])
        return pane
    }

    private func buildPairingPane() -> NSView {
        let outer = NSView()
        outer.wantsLayer = true
        outer.layer?.cornerRadius = 12
        outer.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        outer.addSubview(scroll)
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: outer.leadingAnchor, constant: 16),
            scroll.trailingAnchor.constraint(equalTo: outer.trailingAnchor, constant: -12),
            scroll.topAnchor.constraint(equalTo: outer.topAnchor, constant: 16),
            scroll.bottomAnchor.constraint(equalTo: outer.bottomAnchor, constant: -16),
        ])

        qrImage.imageScaling = .scaleProportionallyUpOrDown
        qrImage.wantsLayer = true
        qrImage.layer?.backgroundColor = NSColor.white.cgColor
        qrImage.layer?.cornerRadius = 8
        qrImage.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            qrImage.widthAnchor.constraint(equalToConstant: 232),
            qrImage.heightAnchor.constraint(equalToConstant: 232),
        ])
        pairingURL.lineBreakMode = .byTruncatingMiddle
        pairingURL.isSelectable = true
        let copyLink = ActionButton("複製連結") { [weak self] in self?.copyPairingLink() }
        let rotate = ActionButton("更新 QR") { [weak self] in self?.mutate("/api/pairing/rotate", id: nil) }
        let collapseBtn = ActionButton("收起配對碼") { [weak self] in self?.collapsePairing() }
        let pairActions = stack([copyLink, rotate, collapseBtn], orientation: .horizontal, spacing: 8)
        let pendingTitle = label("待確認手機", size: 15, weight: .semibold)
        let devicesTitle = label("已配對裝置", size: 15, weight: .semibold)

        let pinBox = stack([pinInstructionLabel, pairingPinLabel], orientation: .vertical, spacing: 2)
        pinBox.alignment = .centerX

        pairingDetailBox = stack([qrImage, pinBox, pairingStatus, pairingURL, pairActions], orientation: .vertical, spacing: 8)
        pairingDetailBox.alignment = .centerX

        let expandBtn = ActionButton("點擊展開配對碼與 QR") { [weak self] in self?.expandPairing() }
        let lockIcon = label("🔒", size: 28)
        let lockTitle = label("配對碼與 QR 已自動隱藏（防偷掃）", size: 13, weight: .bold)
        let lockSub = label("15 秒保護機制生效中，點擊展開", size: 11, color: .secondaryLabelColor)
        pairingCollapsedBox = stack([lockIcon, lockTitle, lockSub, expandBtn], orientation: .vertical, spacing: 6)
        pairingCollapsedBox.alignment = .centerX
        pairingCollapsedBox.isHidden = true

        let content = stack([
            label("裝置配對", size: 20, weight: .semibold),
            label("手機請掃描 QR，電腦請輸入下方 6 位配對碼：", size: 12, color: .secondaryLabelColor),
            pairingDetailBox, pairingCollapsedBox,
            pendingTitle, pendingRows, devicesTitle, deviceRows,
        ], orientation: .vertical, spacing: 10)
        content.translatesAutoresizingMaskIntoConstraints = false
        let document = NSView()
        document.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(content)
        scroll.documentView = document
        NSLayoutConstraint.activate([
            document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
            content.leadingAnchor.constraint(equalTo: document.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: document.trailingAnchor),
            content.topAnchor.constraint(equalTo: document.topAnchor),
            content.bottomAnchor.constraint(equalTo: document.bottomAnchor),
        ])
        renderPending([])
        renderDevices([])
        return outer
    }

    func show() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        schedulePairingCollapse(seconds: 15.0)
        startServer()
    }

    private func schedulePairingCollapse(seconds: TimeInterval = 15.0) {
        pairingAutoCollapseTimer?.invalidate()
        pairingAutoCollapseTimer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { [weak self] _ in
            self?.collapsePairing()
        }
    }

    private func collapsePairing() {
        pairingAutoCollapseTimer?.invalidate()
        pairingAutoCollapseTimer = nil
        isPairingCollapsed = true
        pairingDetailBox.isHidden = true
        pairingCollapsedBox.isHidden = false
    }

    private func expandPairing() {
        isPairingCollapsed = false
        pairingDetailBox.isHidden = false
        pairingCollapsedBox.isHidden = true
        schedulePairingCollapse(seconds: 15.0)
    }

    private func startServer() {
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/fastinput-server")
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            showStartupFailure("App 內找不到伺服器程式。請重新安裝 FastInput。")
            return
        }
        let process = Process()
        process.executableURL = executable
        process.arguments = ["--no-browser"] + Array(CommandLine.arguments.dropFirst())
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            DispatchQueue.main.async { self?.receiveServerOutput(chunk) }
        }
        stderr.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let chunk = handle.availableData
            DispatchQueue.main.async { self?.serverError.append(chunk) }
        }
        process.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                guard let self = self, self.sessionToken == nil else { return }
                let detail = String(data: self.serverError, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
                self.showStartupFailure(detail?.isEmpty == false ? detail! : "伺服器無法啟動。請確認連接埠 8765 沒有被其他程式使用。")
            }
        }
        do {
            try process.run()
            server = process
        } catch {
            showStartupFailure("伺服器無法啟動：\(error.localizedDescription)")
        }
    }

    private func receiveServerOutput(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        serverOutput.append(chunk)
        while let newline = serverOutput.firstIndex(of: 10) {
            let line = String(data: serverOutput[..<newline], encoding: .utf8) ?? ""
            serverOutput.removeSubrange(...newline)
            guard line.hasPrefix("Computer: "), let url = URL(string: String(line.dropFirst("Computer: ".count))),
                  let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
                  let token = components.queryItems?.first(where: { $0.name == "key" })?.value,
                  token.count == 64, token.allSatisfy({ $0.isHexDigit }),
                  url.host == "127.0.0.1", let port = url.port else { continue }
            baseURL = URL(string: "http://127.0.0.1:\(port)")
            sessionToken = token
            connectionLabel.stringValue = "正在連線…"
            pollState()
            pollTimer = Timer.scheduledTimer(withTimeInterval: 0.8, repeats: true) { [weak self] _ in
                self?.pollState()
            }
        }
    }

    private func showStartupFailure(_ message: String) {
        connectionLabel.stringValue = "無法啟動"
        statusLabel.stringValue = message
        pairingStatus.stringValue = "無法顯示 QR Code"
        editor.isEditable = false
    }

    private func makeRequest(_ path: String, method: String = "GET", body: [String: Any]? = nil,
                             completion: @escaping (Int, Data?, Error?) -> Void) {
        guard let baseURL = baseURL, let token = sessionToken,
              let url = URL(string: path, relativeTo: baseURL)?.absoluteURL else {
            completion(0, nil, nil)
            return
        }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 6)
        request.httpMethod = method
        request.setValue("fi_session=\(token)", forHTTPHeaderField: "Cookie")
        request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
        if let body = body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("http://127.0.0.1:\(baseURL.port ?? 8765)", forHTTPHeaderField: "Origin")
            request.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        session.dataTask(with: request) { data, response, error in
            DispatchQueue.main.async {
                completion((response as? HTTPURLResponse)?.statusCode ?? 0, data, error)
            }
        }.resume()
    }

    private func pollState() {
        guard !polling, sessionToken != nil else { return }
        polling = true
        makeRequest("/api/state") { [weak self] status, data, error in
            guard let self = self else { return }
            self.polling = false
            guard status == 200, error == nil, let data = data,
                  let state = try? JSONDecoder().decode(StatePayload.self, from: data),
                  state.status == "ready", state.role == "desktop" else {
                self.online = false
                self.connectionLabel.stringValue = "連線中斷，正在重試…"
                self.updateEditorStatus()
                return
            }
            self.online = true
            self.connectionLabel.stringValue = "已連線 · 僅限內網"

            if let snapshot = state.snapshot {
                self.receiveRemote(snapshot)
            }

            if let pairing = state.pairing { self.updatePairing(pairing) }
            self.renderPending(state.pending ?? [])
            self.renderDevices(state.devices ?? [])
            self.updateEditorStatus()
        }
    }

    private func receiveRemote(_ next: Snapshot) {
        if let prior = remote, prior.generation == next.generation, next.revision < prior.revision { return }
        // If the server state is IDENTICAL to our current remote, nothing changed on the server!
        // Do not touch anything (avoids erasing active typing or IME composition).
        if let prior = remote, prior == next { return }

        let previous = remote
        remote = next

        if previous == nil {
            setEditor(next.text)
            editor.isEditable = true
            dirty = false
            conflict = false
            editBase = nil
        } else if !dirty && !editor.hasMarkedText() {
            if editor.string != next.text { setEditor(next.text) }
            dirty = false
            conflict = false
            editBase = nil
        } else if editor.string == next.text {
            dirty = false
            conflict = false
            editBase = nil
        } else if sendingText == next.text, next.generation == editBase?.generation {
            // A save was acknowledged while the user kept typing.
            editBase = next
        } else if editBase?.text == next.text, editBase?.generation == next.generation {
            editBase = next
        } else if previous != next {
            conflict = true
            saveTimer?.invalidate()
            saveTimer = nil
        }
        updateEditorStatus()
        if dirty && !conflict && !saving { scheduleSave(delay: 0.35, resetIfRunning: false) }
    }

    private func setEditor(_ text: String) {
        applyingRemote = true
        let selectedRange = editor.selectedRange()
        editor.string = text
        let newLocation = min(selectedRange.location, (text as NSString).length)
        editor.setSelectedRange(NSRange(location: newLocation, length: 0))
        editor.undoManager?.removeAllActions()
        applyingRemote = false
        countLabel.stringValue = "\(text.count) 字"
    }

    func textDidChange(_ notification: Notification) {
        guard !applyingRemote, let remote = remote else { return }
        if !dirty { editBase = remote }
        dirty = editor.string != remote.text
        if !dirty {
            editBase = nil
            conflict = false
            saveTimer?.invalidate()
            saveTimer = nil
        } else if !conflict {
            scheduleSave(delay: 0.35, resetIfRunning: true)
        }
        updateEditorStatus()
    }

    private func scheduleSave(delay: TimeInterval = 0.35, resetIfRunning: Bool = true) {
        if !resetIfRunning && saveTimer != nil && saveTimer!.isValid {
            return
        }
        saveTimer?.invalidate()
        saveTimer = nil
        guard dirty, !conflict, !saving, online else { return }
        saveTimer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            self?.saveTimer = nil
            self?.sendText()
        }
    }

    private func sendText() {
        guard dirty, !conflict, !saving, online, let current = remote else { return }
        if editBase == nil { editBase = current }
        guard let base = editBase else { return }
        if base.generation != current.generation || base.text != current.text {
            conflict = true
            updateEditorStatus()
            return
        }
        let text = editor.string
        guard text.utf8.count <= 1_000_000 else {
            statusLabel.stringValue = "文字已超過 1 MB，請先刪減。"
            return
        }
        saving = true
        sendingText = text
        updateEditorStatus()
        makeRequest("/api/text", method: "POST", body: [
            "text": text, "revision": base.revision, "generation": base.generation,
        ]) { [weak self] status, data, error in
            guard let self = self else { return }
            self.saving = false
            let result = data.flatMap { try? JSONDecoder().decode(Snapshot.self, from: $0) }
            if status == 200, let result = result {
                self.receiveRemote(result)
            } else if status == 409, let result = result {
                self.receiveRemote(result)
                if self.editor.string != result.text { self.conflict = true }
            } else if status == 413 {
                self.statusLabel.stringValue = "文字已超過 1 MB，請先刪減。"
            } else if error != nil || status == 0 {
                self.online = false
                self.connectionLabel.stringValue = "連線中斷，正在重試…"
            } else {
                self.statusLabel.stringValue = "儲存失敗，將稍後重試。"
            }
            self.sendingText = nil
            self.updateEditorStatus()
            if self.dirty && !self.conflict && self.online { self.scheduleSave(delay: 0.2, resetIfRunning: false) }
        }
    }

    private func updateEditorStatus() {
        countLabel.stringValue = "\(editor.string.count) 字"
        conflictBar.isHidden = !conflict
        if conflict { statusLabel.stringValue = "有新的手機內容；請在上方選擇要保留哪份文字。" }
        else if saving { statusLabel.stringValue = "正在同步…" }
        else if dirty && online { statusLabel.stringValue = "有尚未同步的修改" }
        else if dirty { statusLabel.stringValue = "連線中斷，修改暫存在此視窗；恢復後會同步。" }
        else if online { statusLabel.stringValue = "已同步 · 文字保存在這台電腦" }
        undoButton?.isEnabled = editor.undoManager?.canUndo ?? false
        redoButton?.isEnabled = editor.undoManager?.canRedo ?? false
    }

    private func adoptRemote() {
        guard let remote = remote else { return }
        setEditor(remote.text)
        dirty = false
        conflict = false
        editBase = nil
        updateEditorStatus()
    }

    private func keepLocal() {
        guard let remote = remote else { return }
        editBase = remote
        conflict = false
        dirty = editor.string != remote.text
        updateEditorStatus()
        scheduleSave(delay: 0.05, resetIfRunning: true)
    }

    private func copyText() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(editor.string, forType: .string)
        statusLabel.stringValue = "已複製全文"
    }

    private func clearText() {
        guard editor.isEditable, !editor.string.isEmpty else { return }
        editor.selectAll(nil)
        editor.insertText("", replacementRange: editor.selectedRange())
        window.makeFirstResponder(editor)
    }

    private func copyPairingLink() {
        guard let link = currentPairingURL else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(link, forType: .string)
        pairingStatus.stringValue = "已複製配對連結"
    }

    private func updatePairing(_ pairing: Pairing) {
        guard pairing.lan_reachable, isPrivateLANPairingURL(pairing.url) else {
            currentPairingURL = nil
            qrImage.image = nil
            pairingPinLabel.stringValue = ""
            pinInstructionLabel.stringValue = ""
            pairingURL.stringValue = "目前沒有可供連線的配對連結"
            pairingStatus.stringValue = "未找到可供連線的內網 IP；請連上同一 Wi-Fi 或用 --lan-ip 指定"
            return
        }
        if let pin = pairing.pin, pin.count == 6 {
            let index = pin.index(pin.startIndex, offsetBy: 3)
            pairingPinLabel.stringValue = "\(pin[..<index]) \(pin[index...])"
            pinInstructionLabel.stringValue = "其他電腦連線配對碼"
        } else {
            pairingPinLabel.stringValue = ""
            pinInstructionLabel.stringValue = ""
        }
        if currentPairingURL != pairing.url {
            currentPairingURL = pairing.url
            pairingURL.stringValue = pairing.url
            qrImage.image = makeQRCode(pairing.url)
        }
        pairingStatus.stringValue = "配對碼約 \(pairing.expires_seconds) 秒後更新"
    }

    private func isPrivateLANPairingURL(_ value: String) -> Bool {
        guard let url = URLComponents(string: value), url.scheme == "http",
              url.path == "/pair", url.port != nil, let host = url.host else { return false }
        let octets = host.split(separator: ".").compactMap { UInt8($0) }
        guard octets.count == 4, host.split(separator: ".").count == 4 else { return false }
        return octets[0] == 10
            || (octets[0] == 172 && (16...31).contains(octets[1]))
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 127 && octets[1] == 0 && octets[2] == 0 && octets[3] == 1)
    }

    private func makeQRCode(_ value: String) -> NSImage? {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        filter.setValue(Data(value.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let image = filter.outputImage else { return nil }
        let scaled = image.transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        guard let cgImage = CIContext().createCGImage(scaled, from: scaled.extent) else { return nil }
        return NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
    }

    private func renderPending(_ pending: [PendingDevice]) {
        let signature = pending.map { "\($0.id):\($0.code):\($0.ip)" }.joined(separator: "|")
        guard signature != pendingSignature || pendingRows.arrangedSubviews.isEmpty else { return }
        pendingSignature = signature
        pendingRows.arrangedSubviews.forEach { pendingRows.removeArrangedSubview($0); $0.removeFromSuperview() }
        if pending.isEmpty {
            pendingRows.addArrangedSubview(label("目前沒有待確認的手機", size: 12, color: .secondaryLabelColor))
        }
        for device in pending {
            let info = label("確認碼 \(device.code) · \(device.ip)", size: 12, weight: .medium)
            let approve = ActionButton("允許") { [weak self] in self?.mutate("/api/pairing/approve", id: device.id) }
            let reject = ActionButton("拒絕") { [weak self] in self?.mutate("/api/pairing/reject", id: device.id) }
            let actions = stack([approve, reject], orientation: .horizontal, spacing: 5)
            pendingRows.addArrangedSubview(stack([info, actions], orientation: .vertical, spacing: 4))
        }
    }

    private func renderDevices(_ devices: [PairedDevice]) {
        let signature = devices.map { "\($0.id):\($0.label)" }.joined(separator: "|")
        guard signature != devicesSignature || deviceRows.arrangedSubviews.isEmpty else { return }
        devicesSignature = signature
        deviceRows.arrangedSubviews.forEach { deviceRows.removeArrangedSubview($0); $0.removeFromSuperview() }
        if devices.isEmpty {
            deviceRows.addArrangedSubview(label("尚未配對手機", size: 12, color: .secondaryLabelColor))
        }
        for device in devices {
            let name = label(device.label, size: 12)
            let revoke = ActionButton("移除") { [weak self] in self?.revoke(device) }
            deviceRows.addArrangedSubview(stack([name, NSView(), revoke], orientation: .horizontal, spacing: 5))
        }
    }

    private func revoke(_ device: PairedDevice) {
        let alert = NSAlert()
        alert.messageText = "移除 \(device.label)？"
        alert.informativeText = "這台手機之後需要重新掃描 QR Code 並確認配對。"
        alert.addButton(withTitle: "移除")
        alert.addButton(withTitle: "取消")
        if alert.runModal() == .alertFirstButtonReturn { mutate("/api/device/revoke", id: device.id) }
    }

    private func mutate(_ path: String, id: String?) {
        makeRequest(path, method: "POST", body: id.map { ["id": $0] } ?? [:]) { [weak self] status, _, _ in
            guard let self = self else { return }
            if status == 200 { self.pollState() }
            else { self.statusLabel.stringValue = "操作失敗，請稍後重試。" }
        }
    }

    func canClose() -> Bool {
        guard dirty || saving else { return true }
        let alert = NSAlert()
        alert.messageText = "還有尚未同步的文字"
        alert.informativeText = "現在關閉會失去這次未同步的修改。"
        alert.addButton(withTitle: "繼續編輯")
        alert.addButton(withTitle: "仍要關閉")
        return alert.runModal() == .alertSecondButtonReturn
    }

    func stopServer() {
        pollTimer?.invalidate()
        saveTimer?.invalidate()
        session.invalidateAndCancel()
        if let server = server, server.isRunning { server.terminate() }
    }
}

private final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: MainWindowController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        installMenu()
        controller = MainWindowController()
        controller?.show()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        controller?.canClose() == false ? .terminateCancel : .terminateNow
    }

    func applicationWillTerminate(_ notification: Notification) { controller?.stopServer() }

    private func installMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(NSMenuItem(title: "結束 FastInput", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "編輯")
        for (title, action, key) in [
            ("復原", "undo:", "z"), ("重做", "redo:", "Z"),
            ("剪下", "cut:", "x"), ("複製", "copy:", "c"),
            ("貼上", "paste:", "v"), ("全選", "selectAll:", "a"),
        ] {
            editMenu.addItem(NSMenuItem(title: title, action: Selector(action), keyEquivalent: key))
        }
        editItem.submenu = editMenu
        main.addItem(editItem)
        NSApp.mainMenu = main
    }
}

@main
private enum FastInputApp {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
