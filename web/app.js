(() => {
  "use strict";

  const { t } = window.I18n;

  const byId = (id) => document.getElementById(id);
  const ui = {
    connectionBadge: byId("connectionBadge"),
    connectionText: byId("connectionText"),
    sidebarToggleButton: byId("sidebarToggleButton"),
    pageTitle: byId("pageTitle"),
    loadingView: byId("loadingView"),
    waitingView: byId("waitingView"),
    waitingEyebrow: byId("waitingEyebrow"),
    waitingTitle: byId("waitingTitle"),
    waitingDescription: byId("waitingDescription"),
    pairCode: byId("pairCode"),
    retryButton: byId("retryButton"),
    workspace: byId("workspace"),
    editor: byId("editor"),
    editorHeading: byId("editorHeading"),
    editorKicker: byId("editorKicker"),
    saveStatus: byId("saveStatus"),
    characterCount: byId("characterCount"),
    conflictNotice: byId("conflictNotice"),
    useLatestButton: byId("useLatestButton"),
    keepMineButton: byId("keepMineButton"),
    undoButton: byId("undoButton"),
    redoButton: byId("redoButton"),
    selectAllButton: byId("selectAllButton"),
    copyButton: byId("copyButton"),
    clearButton: byId("clearButton"),
    desktopPanel: byId("desktopPanel"),
    qrImage: byId("qrImage"),
    qrUnavailable: byId("qrUnavailable"),
    pairingUrl: byId("pairingUrl"),
    pairExpiry: byId("pairExpiry"),
    rotateButton: byId("rotateButton"),
    shutdownButton: byId("shutdownButton"),
    desktopPin: byId("desktopPin"),
    copyPinButton: byId("copyPinButton"),
    pinSection: byId("pinSection"),
    pinInput: byId("pinInput"),
    pinSubmitButton: byId("pinSubmitButton"),
    pinError: byId("pinError"),
    pendingList: byId("pendingList"),
    pendingEmpty: byId("pendingEmpty"),
    pendingCount: byId("pendingCount"),
    deviceList: byId("deviceList"),
    devicesEmpty: byId("devicesEmpty"),
    toast: byId("toast"),
    micButton: byId("micButton"),
    micButtonText: byId("micButtonText"),
    voiceHint: byId("voiceHint"),
    voiceHintText: byId("voiceHintText"),
    voiceHintClose: byId("voiceHintClose"),
    tabText: byId("tabText"),
    tabFiles: byId("tabFiles"),
    textPanel: byId("textPanel"),
    filesCard: byId("filesCard"),
    tabRealtime: byId("tabRealtime"),
    realtimeCard: byId("realtimeCard"),
    filesDot: byId("filesDot"),
    pairDetailsWrap: byId("pairDetailsWrap"),
    pairCollapsedBox: byId("pairCollapsedBox"),
    togglePairingButton: byId("togglePairingButton"),
    hidePairingButton: byId("hidePairingButton"),
  };

  const app = {
    hydrated: false,
    status: "loading",
    role: null,
    isListening: false,
    recognition: null,
    speechBaseText: "",
    pairingCollapsed: false,
    pairingCollapseTimer: null,
    sidebarCollapsed: false,
    pairCode: null,
    remote: null,
    base: null,
    dirty: false,
    conflict: false,
    saving: false,
    sending: null,
    editEpoch: 0,
    apiAvailable: true,
    streamConnected: false,
    stream: null,
    saveTimer: null,
    toastTimer: null,
    refreshPromise: null,
    pending: [],
    devices: [],
    pairing: null,
    closedByUser: false,
    saveProblem: null,
    undo: [],
    redo: [],
    beforeEdit: null,
    lastEditorSnapshot: null,
    lastHistoryType: null,
    lastHistoryAt: 0,
    filesRevision: null,
    mainTab: "text",
  };

  const numberFormat = { format: value => window.I18n.number(value) };
  const segmenter = typeof Intl.Segmenter === "function"
    ? new Intl.Segmenter("zh-TW", { granularity: "grapheme" })
    : null;

  function countCharacters(value) {
    return segmenter ? [...segmenter.segment(value)].length : [...value].length;
  }

  function snapshotEditor() {
    return {
      text: ui.editor.value,
      start: ui.editor.selectionStart,
      end: ui.editor.selectionEnd,
    };
  }

  function normalizeSnapshot(value) {
    if (!value || typeof value.text !== "string" || value.revision === undefined || value.generation === undefined) {
      return null;
    }
    return { text: value.text, revision: value.revision, generation: value.generation };
  }

  function sameGeneration(a, b) {
    return a && b && String(a.generation) === String(b.generation);
  }

  function sameVersion(a, b) {
    return sameGeneration(a, b) && String(a.revision) === String(b.revision);
  }

  function isOlder(next, current) {
    if (!current || !sameGeneration(next, current)) return false;
    const nextRevision = Number(next.revision);
    const currentRevision = Number(current.revision);
    return Number.isFinite(nextRevision) && Number.isFinite(currentRevision) && nextRevision < currentRevision;
  }

  let isComposing = false;

  function setEditorFromServer(text) {
    if (isComposing) return;
    const start = Math.min(ui.editor.selectionStart, text.length);
    const end = Math.min(ui.editor.selectionEnd, text.length);
    ui.editor.value = text;
    ui.editor.setSelectionRange(start, end);
    app.undo = [];
    app.redo = [];
    app.beforeEdit = null;
    app.lastEditorSnapshot = snapshotEditor();
    app.lastHistoryType = null;
    renderEditorStatus();
  }

  function receiveRemote(next, source = "event") {
    if (!next || isOlder(next, app.remote) || sameVersion(next, app.remote)) return;
    const previous = app.remote;
    app.remote = next;

    if (!previous) {
      app.base = null;
      app.dirty = false;
      app.conflict = false;
      setEditorFromServer(next.text);
      return;
    }

    if (!app.dirty) {
      if (ui.editor.value !== next.text) setEditorFromServer(next.text);
      renderEditorStatus();
      return;
    }

    if (ui.editor.value === next.text) {
      app.base = null;
      app.dirty = false;
      app.conflict = false;
      renderEditorStatus();
      return;
    }

    // Acknowledging a previous send is safe even when the user kept typing.
    if (app.sending && next.text === app.sending.text && sameGeneration(next, app.sending.base)) {
      app.base = next;
    } else if (app.base && next.text === app.base.text && sameGeneration(next, app.base)) {
      // A newer revision with identical content leaves the local draft based on the same text.
      app.base = next;
    } else if (!sameVersion(next, previous) || next.text !== previous.text || source === "conflict") {
      app.conflict = true;
      clearTimeout(app.saveTimer);
    }

    renderEditorStatus();
    if (app.dirty && !app.conflict && !app.saving) scheduleSave();
  }

  function markLocalEdit() {
    app.editEpoch += 1;
    app.saveProblem = null;
    if (!app.dirty) app.base = app.remote;
    app.dirty = !!app.remote && ui.editor.value !== app.remote.text;
    if (!app.dirty) {
      app.base = null;
      app.conflict = false;
      clearTimeout(app.saveTimer);
    } else if (!app.conflict) {
      scheduleSave();
    }
    renderEditorStatus();
  }

  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;

  // 手機用 http://區網IP 連線時不是「安全環境」，瀏覽器不會開放網頁麥克風與網頁語音辨識
  // （不是使用者設定的問題）。此時改引導使用手機鍵盤內建的麥克風鍵（系統聽寫引擎）。
  const speechUsable = window.isSecureContext === true && typeof SpeechRecognition === "function";

  function isAppleTouch() {
    return /iPad|iPhone|iPod/.test(navigator.userAgent)
      || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  }

  function keyboardMicTip() {
    if (isAppleTouch()) {
      return t("app.on.your.iphone.keyboard.tap.the.microphone.to.dictate");
    }
    if (/Android/i.test(navigator.userAgent)) {
      return t("app.tap.the.microphone.on.your.keyboard.samsung.keyboard.or");
    }
    return t("app.use.system.dictation.press.win.h.on.windows.or");
  }

  function showVoiceHint(reason) {
    const lead = {
      insecure: t("app.this.page.connects.to.your.computer.over.http.so"),
      unsupported: t("app.this.browser.does.not.support.web.speech.recognition"),
      denied: t("app.microphone.access.was.denied.to.use.web.speech.recognition"),
      network: t("app.web.speech.recognition.needs.an.internet.connection.chrome.sends"),
      "audio-capture": t("app.no.microphone.is.available"),
      generic: t("app.web.speech.recognition.is.temporarily.unavailable"),
    }[reason] || t("app.web.speech.recognition.is.temporarily.unavailable");
    if (ui.voiceHintText) window.I18n.bind(ui.voiceHintText, "textContent", () => t("app.for.reliable.dictation.if.the.microphone.key.is.missing", { value0: window.I18n.localize(lead), value1: keyboardMicTip() }));
    if (ui.voiceHint) ui.voiceHint.hidden = false;
    ui.editor.focus();
  }

  function hideVoiceHint() {
    if (ui.voiceHint) ui.voiceHint.hidden = true;
  }

  async function toggleSpeech() {
    if (app.isListening) {
      stopSpeech();
      return;
    }
    if (!speechUsable) {
      showVoiceHint(window.isSecureContext === true ? "unsupported" : "insecure");
      return;
    }
    hideVoiceHint();
    startSpeech();
  }

  function startSpeech() {
    if (app.isListening) return;
    try {
      const recognition = new SpeechRecognition();
      recognition.continuous = true;
      recognition.interimResults = true;
      recognition.lang = navigator.language || "zh-TW";

      app.speechBaseText = ui.editor.value;

      recognition.onstart = () => {
        app.isListening = true;
        renderSpeechUI();
        showToast(t("app.listening.start.speaking"));
      };

      recognition.onresult = (event) => {
        if (app.recognition !== recognition) return;
        let finalTranscript = "";
        let interimTranscript = "";
        for (let i = 0; i < event.results.length; ++i) {
          const item = event.results[i];
          if (item && item[0]) {
            if (item.isFinal) {
              finalTranscript += item[0].transcript;
            } else {
              interimTranscript += item[0].transcript;
            }
          }
        }
        const spoken = finalTranscript || interimTranscript;
        if (spoken) {
          const base = app.speechBaseText;
          const transcript = finalTranscript + interimTranscript;
          // 只在「英數字接英數字」時補空白；中文之間不加。
          const separator = /[A-Za-z0-9]$/.test(base) && /^[A-Za-z0-9]/.test(transcript) ? " " : "";
          ui.editor.value = base + separator + transcript;
          window.I18n.bind(ui.characterCount, "textContent", () => t("common.character", { count: countCharacters(ui.editor.value) }));
          ui.copyButton.disabled = ui.editor.value.length === 0;
          ui.clearButton.disabled = ui.editor.value.length === 0;
          markLocalEdit();
        }
      };

      recognition.onerror = (event) => {
        console.warn("Speech recognition error:", event.error);
        if (app.recognition !== recognition) return;
        if (event.error === "not-allowed" || event.error === "service-not-allowed") {
          showVoiceHint("denied");
        } else if (event.error === "network") {
          showVoiceHint("network");
        } else if (event.error === "audio-capture") {
          showVoiceHint("audio-capture");
        } else if (event.error !== "no-speech" && event.error !== "aborted") {
          showVoiceHint("generic");
        }
        stopSpeech();
      };

      recognition.onend = () => {
        // 舊的辨識實例結束時，不可蓋掉已經重新開始的新實例。
        if (app.recognition !== recognition) return;
        app.isListening = false;
        app.recognition = null;
        renderSpeechUI();
      };

      app.recognition = recognition;
      // 先標記為聆聽中，避免在 onstart 觸發前重複點擊而建立第二個實例。
      app.isListening = true;
      renderSpeechUI();
      recognition.start();
    } catch (e) {
      console.warn("startSpeech exception:", e);
      showVoiceHint("generic");
      stopSpeech();
    }
  }

  function stopSpeech() {
    if (app.recognition) {
      try { app.recognition.stop(); } catch (_) {}
      app.recognition = null;
    }
    app.isListening = false;
    renderSpeechUI();
  }

  function renderSpeechUI() {
    const listening = app.isListening;
    if (ui.micButton) {
      ui.micButton.classList.toggle("is-listening", listening);
      if (ui.micButtonText) {
        window.I18n.bind(ui.micButtonText, "textContent", () => listening ? t("app.listening.tap.to.stop") : t("app.voice.input"));
      }
    }
  }

  function schedulePairingCollapse(seconds = 15) {
    clearTimeout(app.pairingCollapseTimer);
    app.pairingCollapseTimer = setTimeout(() => {
      setPairingCollapsed(true);
    }, seconds * 1000);
  }

  function setPairingCollapsed(collapsed) {
    app.pairingCollapsed = collapsed;
    if (ui.pairDetailsWrap) ui.pairDetailsWrap.hidden = collapsed;
    if (ui.pairCollapsedBox) ui.pairCollapsedBox.hidden = !collapsed;
    if (!collapsed) {
      schedulePairingCollapse(15);
    }
  }

  function setSidebarCollapsed(collapsed, persist = true) {
    app.sidebarCollapsed = collapsed;
    ui.desktopPanel.hidden = app.role !== "desktop" || collapsed;
    ui.workspace.classList.toggle("sidebar-collapsed", collapsed);
    window.I18n.bind(ui.sidebarToggleButton, "textContent", () => collapsed ? t("app.show.pairing.and.devices") : t("app.hide.pairing.and.devices"));
    ui.sidebarToggleButton.setAttribute("aria-expanded", String(!collapsed));
    if (persist) {
      try { localStorage.setItem("fi_sidebar_collapsed", collapsed ? "1" : "0"); }
      catch (_) { /* Browser storage may be disabled. */ }
    }
  }

  function scheduleSave(delay = 200) {
    clearTimeout(app.saveTimer);
    if (!app.dirty || app.conflict || app.saveProblem || app.status !== "ready") return;
    app.saveTimer = setTimeout(sendText, delay);
  }

  async function request(path, options = {}, timeoutMs = 6000) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await fetch(path, {
        cache: "no-store",
        credentials: "same-origin",
        signal: options.signal || controller.signal,
        headers: { Accept: "application/json", ...(options.body ? { "Content-Type": "application/json" } : {}) },
        ...options,
      });
      let data = null;
      try { data = await response.json(); } catch (_) { /* Handled below. */ }
      return { response, data };
    } finally {
      clearTimeout(timeout);
    }
  }

  async function sendText() {
    if (!app.dirty || app.conflict || app.saveProblem || app.saving || !app.remote || app.status !== "ready") return;

    if (app.base && !sameVersion(app.base, app.remote)) {
      if (app.base.text !== app.remote.text || !sameGeneration(app.base, app.remote)) {
        app.conflict = true;
        renderEditorStatus();
        return;
      }
      app.base = app.remote;
    }

    const submitted = {
      text: ui.editor.value,
      base: app.base || app.remote,
      epoch: app.editEpoch,
    };
    app.sending = submitted;
    app.saving = true;
    renderEditorStatus();

    try {
      const { response, data } = await request("/api/text", {
        method: "POST",
        body: JSON.stringify({
          text: submitted.text,
          revision: submitted.base.revision,
          generation: submitted.base.generation,
        }),
      }, 5000);
      const next = normalizeSnapshot(data);
      if (response.status === 409) {
        if (next) {
          receiveRemote(next, "conflict");
          if (ui.editor.value !== next.text) app.conflict = true;
        } else {
          app.conflict = true;
        }
      } else if (response.status === 413) {
        app.saveProblem = window.I18n.errorMessage(data, t("app.text.is.too.long"));
        showToast(window.I18n.errorMessage(data, t("app.text.is.too.long.shorten.it.and.try.again")));
      } else if (response.status === 401) {
        showToast(window.I18n.errorMessage(data, t("app.pairing.has.expired.pair.again")));
        await refreshState(true);
      } else if (!response.ok || !next) {
        throw new Error(t("app.unable.to.save.text"));
      } else {
        receiveRemote(next, "save");
        if (ui.editor.value === app.remote.text) {
          app.dirty = false;
          app.base = null;
          app.conflict = false;
        } else if (!app.conflict && app.remote.text === submitted.text) {
          app.base = app.remote;
          app.dirty = true;
        }
      }
      app.apiAvailable = true;
    } catch (_) {
      app.apiAvailable = false;
      showToast(t("app.connection.interrupted.your.text.is.still.on.this.page"));
    } finally {
      app.saving = false;
      app.sending = null;
      renderConnection();
      renderEditorStatus();
      if (app.dirty && !app.conflict && !app.saveProblem) {
        scheduleSave(app.apiAvailable ? 80 : 1500);
      }
    }
  }

  function closeStream() {
    if (app.stream) {
      try { app.stream.close(); } catch (_) {}
    }
    app.stream = null;
    app.streamConnected = false;
    renderConnection();
  }

  function restartStream() {
    closeStream();
    if (app.status === "ready") {
      ensureStream();
    }
  }

  function ensureStream() {
    if (app.status !== "ready") return;
    if (app.stream) {
      if (app.stream.readyState === EventSource.CLOSED) {
        closeStream();
      } else {
        return;
      }
    }
    try {
      const stream = new EventSource("/api/events");
      app.stream = stream;
      stream.onopen = () => {
        app.streamConnected = true;
        app.apiAvailable = true;
        renderConnection();
        if (app.dirty && !app.conflict) scheduleSave(50);
      };
      stream.onmessage = (event) => {
        try {
          const payload = JSON.parse(event.data);
          noteFilesRevision(payload.files_revision);
          const next = normalizeSnapshot(payload);
          if (next) {
            receiveRemote(next);
          }
        } catch (_) { /* Ignore an event that is not a text snapshot. */ }
      };
      stream.onerror = () => {
        app.streamConnected = false;
        renderConnection();
      };
    } catch (_) {}
  }

  function applyState(data) {
    if (!data || typeof data !== "object") throw new Error(t("app.invalid.server.response"));
    const previousStatus = app.status;
    app.status = data.status || "unpaired";
    app.role = data.role || app.role || "phone";
    app.pairCode = data.code == null ? null : String(data.code);
    app.pending = Array.isArray(data.pending) ? data.pending : [];
    app.devices = Array.isArray(data.devices) ? data.devices : [];
    app.pairing = data.pairing || null;
    noteFilesRevision(data.files_revision);

    if (app.status === "ready") {
      const next = normalizeSnapshot(data);
      if (!next) throw new Error(t("app.text.version.is.missing"));
      if (!app.hydrated) {
        app.saveProblem = null;
        app.remote = null;
        app.base = null;
        app.dirty = false;
        app.conflict = false;
        receiveRemote(next, "initial");
      } else if (app.dirty) {
        // User has local edits typed while disconnected or backgrounded.
        // Keep local draft intact!
        if (app.remote && !sameVersion(next, app.remote)) {
          receiveRemote(next, "poll");
        } else {
          app.remote = next;
          if (!app.base) app.base = next;
        }
      } else {
        receiveRemote(next, "poll");
      }
      ensureStream();
    } else {
      closeStream();
      if (previousStatus === "ready") {
        app.saveProblem = null;
        app.remote = null;
        app.base = null;
        app.dirty = false;
        app.conflict = false;
        setEditorFromServer("");
      }
    }
    app.hydrated = true;
    renderAll();
    if (app.dirty && !app.conflict && !app.saving) scheduleSave(80);
  }

  let activeRefreshController = null;

  async function refreshState(force = false) {
    if (app.closedByUser) return;
    if (force && activeRefreshController) {
      try { activeRefreshController.abort(); } catch (_) {}
      activeRefreshController = null;
      app.refreshPromise = null;
    }
    if (app.refreshPromise) return app.refreshPromise;

    const controller = new AbortController();
    activeRefreshController = controller;

    app.refreshPromise = (async () => {
      try {
        const { response, data } = await request("/api/state", { signal: controller.signal }, 5000);
        if (!response.ok) throw new Error(t("app.unable.to.connect"));
        app.apiAvailable = true;
        app.closedByUser = false;
        applyState(data);
      } catch (err) {
        if (err && err.name !== "AbortError") {
          app.apiAvailable = false;
          if (!app.hydrated) {
            app.hydrated = true;
            app.status = "offline";
          }
          renderAll();
        }
      } finally {
        if (activeRefreshController === controller) {
          activeRefreshController = null;
        }
        app.refreshPromise = null;
      }
    })();
    return app.refreshPromise;
  }

  function renderConnection() {
    ui.connectionBadge.classList.remove("is-online", "is-offline");
    let text = t("app.connecting");
    if (app.closedByUser) text = t("app.ended");
    else if (app.status === "pending") text = t("app.waiting.for.approval.on.your.computer");
    else if (app.status === "unpaired") text = t("app.not.paired");
    else if (!app.apiAvailable || app.status === "offline") {
      text = t("app.disconnected");
      ui.connectionBadge.classList.add("is-offline");
    } else if (app.status === "ready" && app.streamConnected) {
      text = t("app.syncing.live");
      ui.connectionBadge.classList.add("is-online");
    } else if (app.status === "ready") {
      text = t("app.reconnecting");
    }
    ui.connectionText.textContent = text;
  }

  function renderEditorStatus() {
    const status = app.conflict ? t("app.choose.a.version")
      : app.saveProblem ? app.saveProblem
      : app.saving ? t("app.syncing")
      : app.dirty && !app.apiAvailable ? t("app.waiting.for.connection")
      : app.dirty ? t("app.pending.sync")
      : t("app.synced");
    window.I18n.bind(ui.saveStatus, "textContent", () => status);
    ui.saveStatus.classList.toggle("is-saving", app.saving);
    ui.saveStatus.classList.toggle("is-warning", app.conflict || !!app.saveProblem || (app.dirty && !app.apiAvailable));
    ui.conflictNotice.hidden = !app.conflict;
    window.I18n.bind(ui.characterCount, "textContent", () => t("common.character", { count: countCharacters(ui.editor.value) }));
    ui.undoButton.disabled = app.undo.length === 0;
    ui.redoButton.disabled = app.redo.length === 0;
    ui.copyButton.disabled = ui.editor.value.length === 0;
    ui.clearButton.disabled = ui.editor.value.length === 0;
  }

  function renderWaiting() {
    ui.pairCode.hidden = true;
    ui.retryButton.hidden = app.closedByUser;
    if (ui.pinSection) ui.pinSection.hidden = true;
    if (ui.pinError) ui.pinError.hidden = true;

    if (app.status === "pending") {
      window.I18n.bind(ui.waitingEyebrow, "textContent", () => t("app.waiting.for.pairing"));
      window.I18n.bind(ui.waitingTitle, "textContent", () => t("app.approve.this.device.on.your.computer"));
      window.I18n.bind(ui.waitingDescription, "textContent", () => t("app.check.that.the.numbers.match.on.both.screens.then"));
      if (app.pairCode) {
        ui.pairCode.textContent = app.pairCode;
        ui.pairCode.hidden = false;
      }
    } else if (app.closedByUser) {
      window.I18n.bind(ui.waitingEyebrow, "textContent", () => t("app.ended"));
      window.I18n.bind(ui.waitingTitle, "textContent", () => t("app.fastinput.has.closed"));
      window.I18n.bind(ui.waitingDescription, "textContent", () => t("app.to.use.fastinput.again.reopen.the.app.on.your"));
    } else if (app.status === "offline") {
      window.I18n.bind(ui.waitingEyebrow, "textContent", () => t("app.disconnected"));
      window.I18n.bind(ui.waitingTitle, "textContent", () => t("app.cannot.reach.your.computer"));
      window.I18n.bind(ui.waitingDescription, "textContent", () => t("app.connect.both.devices.to.the.same.local.network.and"));
    } else {
      window.I18n.bind(ui.waitingEyebrow, "textContent", () => t("app.not.paired"));
      window.I18n.bind(ui.waitingTitle, "textContent", () => t("app.enter.a.pairing.code.or.scan.the.qr.code"));
      window.I18n.bind(ui.waitingDescription, "textContent", () => t("app.enter.the.6.digit.code.shown.in.fastinput.on"));
      if (ui.pinSection) ui.pinSection.hidden = false;
    }
  }

  function safePairingUrl(value) {
    if (typeof value !== "string") return null;
    try {
      const url = new URL(value, location.href);
      if (url.protocol === "http:" || url.protocol === "https:") return url.href;
    } catch (_) { /* Ignore invalid links. */ }
    return null;
  }

  function renderPairing() {
    const pairing = app.pairing || {};
    const svg = typeof pairing.svg === "string" && /^\s*(?:<\?xml[^>]*\?>\s*)?(?:<!DOCTYPE[^>]*>\s*)?<svg(?:\s|>)/i.test(pairing.svg)
      ? pairing.svg : null;
    if (svg) {
      const imageUrl = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
      if (ui.qrImage.src !== imageUrl) ui.qrImage.src = imageUrl;
      ui.qrImage.hidden = false;
      ui.qrUnavailable.hidden = true;
    } else {
      ui.qrImage.hidden = true;
      ui.qrImage.removeAttribute("src");
      ui.qrUnavailable.hidden = false;
    }

    if (ui.desktopPin) {
      const pin = pairing.pin ? String(pairing.pin) : "";
      ui.desktopPin.textContent = pin && pin.length === 6 ? `${pin.slice(0, 3)} ${pin.slice(3)}` : (pin || "------");
    }

    const url = safePairingUrl(pairing.url);
    window.I18n.bind(ui.pairingUrl, "textContent", () => url || t("app.waiting.for.pairing.link"));
    if (url) {
      ui.pairingUrl.href = url;
      ui.pairingUrl.removeAttribute("aria-disabled");
    } else {
      ui.pairingUrl.removeAttribute("href");
      ui.pairingUrl.setAttribute("aria-disabled", "true");
    }

    const remaining = Number(pairing.expires_seconds);
    ui.pairExpiry.textContent = Number.isFinite(remaining) && remaining > 0
      ? t("app.pairing.valid", { count: Math.ceil(remaining / 60) }) : "";
  }

  function createButton(label, className, handler) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    window.I18n.bind(button, "textContent", () => label);
    button.addEventListener("click", handler);
    return button;
  }

  function createItem(title, detail, code) {
    const card = document.createElement("div");
    card.className = "item-card";
    const info = document.createElement("div");
    info.className = "item-info";
    const titleElement = document.createElement("strong");
    titleElement.className = "item-title";
    titleElement.textContent = title;
    const detailElement = document.createElement("span");
    detailElement.className = "item-detail";
    window.I18n.bind(detailElement, "textContent", () => detail);
    info.append(titleElement, detailElement);
    if (code) {
      const codeElement = document.createElement("span");
      codeElement.className = "item-code";
      codeElement.textContent = code;
      info.append(codeElement);
    }
    const actions = document.createElement("div");
    actions.className = "item-actions";
    card.append(info, actions);
    return { card, actions };
  }

  function formatLastSeen(value) {
    if (value == null) return t("app.paired");
    const numeric = Number(value);
    const date = Number.isFinite(numeric)
      ? new Date(numeric < 1e12 ? numeric * 1000 : numeric)
      : new Date(value);
    return Number.isNaN(date.getTime()) ? t("app.paired")
      : t("app.paired.on", { value0: date.toLocaleString(window.I18n.getLanguage(), { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" }) });
  }

  function renderLists() {
    ui.pendingList.replaceChildren();
    ui.pendingCount.textContent = window.I18n.number(app.pending.length);
    ui.pendingEmpty.hidden = app.pending.length > 0;
    app.pending.forEach((pending, index) => {
      const ip = typeof pending.ip === "string" && pending.ip ? pending.ip : t("app.unknown.location");
      const code = pending.code == null ? "" : String(pending.code);
      const { card, actions } = createItem(t("app.phone", { value0: index + 1 }), t("app.from.check.that.both.codes.match", { value0: ip }), code);
      actions.append(
        createButton(t("app.approve"), "button button-primary", () => pairingAction("approve", pending.id)),
        createButton(t("app.reject"), "button button-secondary", () => pairingAction("reject", pending.id)),
      );
      ui.pendingList.append(card);
    });

    ui.deviceList.replaceChildren();
    ui.devicesEmpty.hidden = app.devices.length > 0;
    app.devices.forEach((device, index) => {
      const name = typeof device.label === "string" && device.label.trim()
        ? device.label : t("app.phone", { value0: index + 1 });
      const { card, actions } = createItem(name, formatLastSeen(device.last_seen));
      actions.append(createButton(t("app.remove"), "button button-danger", () => pairingAction("revoke", device.id)));
      ui.deviceList.append(card);
    });
  }

  function renderAll() {
    document.body.dataset.role = app.role || "unknown";
    ui.loadingView.hidden = app.hydrated;
    ui.waitingView.hidden = !app.hydrated || app.status === "ready";
    ui.workspace.hidden = app.status !== "ready";
    ui.desktopPanel.hidden = app.role !== "desktop" || app.sidebarCollapsed;
    if (ui.tabRealtime) {
      ui.tabRealtime.hidden = app.role === "desktop";
      if (app.role === "desktop" && app.mainTab === "realtime") setMainTab("text", false);
    }
    if (app.role === "phone") {
      window.I18n.bind(ui.editorKicker, "textContent", () => t("app.phone.notepad"));
      window.I18n.bind(ui.editorHeading, "textContent", () => t("app.type.here.see.it.on.your.computer"));
    } else {
      window.I18n.bind(ui.editorKicker, "textContent", () => t("app.shared.notepad"));
      window.I18n.bind(ui.editorHeading, "textContent", () => t("app.write.something"));
    }
    renderConnection();
    renderEditorStatus();
    if (app.status !== "ready") renderWaiting();
    if (app.role === "desktop") {
      renderPairing();
      renderLists();
      if (!app.pairingCollapseTimer && !app.pairingCollapsed) {
        schedulePairingCollapse(15);
      }
    }
  }

  function showToast(message, duration = 3500) {
    clearTimeout(app.toastTimer);
    window.I18n.bind(ui.toast, "textContent", () => message);
    ui.toast.hidden = false;
    app.toastTimer = setTimeout(() => { ui.toast.hidden = true; }, duration);
  }

  async function pairingAction(kind, id) {
    const path = kind === "revoke" ? "/api/device/revoke" : `/api/pairing/${kind}`;
    try {
      const { response, data } = await request(path, { method: "POST", body: JSON.stringify({ id }) });
      if (!response.ok) { showToast(window.I18n.errorMessage(data, t("app.action.failed"))); return; }
      showToast(kind === "approve" ? t("app.phone.paired") : kind === "reject" ? t("app.pairing.rejected") : t("app.device.removed"));
      await refreshState();
    } catch (_) {
      showToast(t("app.action.could.not.be.completed.try.again.later"));
    }
  }

  function pushHistory(previous, type) {
    const now = Date.now();
    const canGroup = /^(insertText|deleteContentBackward|deleteContentForward)$/.test(type)
      && type === app.lastHistoryType && now - app.lastHistoryAt < 550;
    if (!canGroup) {
      app.undo.push(previous);
      if (app.undo.length > 80) app.undo.shift();
    }
    app.redo = [];
    app.lastHistoryType = type;
    app.lastHistoryAt = now;
  }

  function restoreEditor(snapshot) {
    ui.editor.value = snapshot.text;
    ui.editor.focus();
    ui.editor.setSelectionRange(snapshot.start, snapshot.end);
    app.lastEditorSnapshot = snapshotEditor();
    app.beforeEdit = null;
    app.lastHistoryType = null;
    markLocalEdit();
  }

  function undo() {
    if (!app.undo.length) return;
    app.redo.push(snapshotEditor());
    restoreEditor(app.undo.pop());
  }

  function redo() {
    if (!app.redo.length) return;
    app.undo.push(snapshotEditor());
    restoreEditor(app.redo.pop());
  }

  async function copyAll() {
    const value = ui.editor.value;
    if (!value) return;
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(value);
      } else {
        const helper = document.createElement("textarea");
        helper.value = value;
        helper.setAttribute("readonly", "");
        helper.style.position = "fixed";
        helper.style.opacity = "0";
        document.body.append(helper);
        helper.select();
        const copied = document.execCommand("copy");
        helper.remove();
        if (!copied) throw new Error("copy failed");
      }
      showToast(t("app.all.text.copied"));
    } catch (_) {
      showToast(t("app.cannot.copy.automatically.select.the.text.and.copy.it"));
    }
  }

  function clearAll() {
    if (!ui.editor.value) return;
    pushHistory(snapshotEditor(), "clear");
    ui.editor.value = "";
    ui.editor.focus();
    ui.editor.setSelectionRange(0, 0);
    app.lastEditorSnapshot = snapshotEditor();
    markLocalEdit();
    showToast(t("app.text.cleared.you.can.undo.this.change"));
  }

  async function submitPin() {
    if (!ui.pinInput) return;
    const raw = ui.pinInput.value.trim();
    const cleanPin = raw.replace(/\D/g, "");
    if (ui.pinError) {
      ui.pinError.hidden = true;
      ui.pinError.textContent = "";
    }

    if (cleanPin.length !== 6) {
      if (ui.pinError) {
        window.I18n.bind(ui.pinError, "textContent", () => t("app.enter.a.6.digit.pairing.code"));
        ui.pinError.hidden = false;
      }
      ui.pinInput.focus();
      return;
    }

    if (ui.pinSubmitButton) ui.pinSubmitButton.disabled = true;
    try {
      const { response, data } = await request("/api/pairing/pin", {
        method: "POST",
        body: JSON.stringify({ pin: cleanPin }),
      });
      if (response.ok) {
        showToast(t("app.paired.opening.your.notepad"));
        ui.pinInput.value = "";
        await refreshState();
      } else {
        const errorMsg = window.I18n.errorMessage(data) || t("app.pairing.code.is.incorrect.or.expired");
        if (ui.pinError) {
          window.I18n.bind(ui.pinError, "textContent", () => errorMsg);
          ui.pinError.hidden = false;
        }
      }
    } catch (_) {
      if (ui.pinError) {
        window.I18n.bind(ui.pinError, "textContent", () => t("app.connection.failed.check.that.both.devices.are.on.the"));
        ui.pinError.hidden = false;
      }
    } finally {
      if (ui.pinSubmitButton) ui.pinSubmitButton.disabled = false;
    }
  }

  ui.editor.addEventListener("compositionstart", () => {
    isComposing = true;
  });

  ui.editor.addEventListener("compositionend", () => {
    isComposing = false;
    const previous = app.beforeEdit || app.lastEditorSnapshot;
    app.beforeEdit = null;
    if (previous && previous.text !== ui.editor.value) {
      pushHistory(previous, "composition");
      app.lastEditorSnapshot = snapshotEditor();
      markLocalEdit();
    }
  });

  ui.editor.addEventListener("beforeinput", (event) => {
    if (isComposing) return;
    if (event.inputType === "historyUndo" || event.inputType === "historyRedo") {
      event.preventDefault();
      if (event.inputType === "historyUndo") undo(); else redo();
      return;
    }
    app.beforeEdit = snapshotEditor();
  });

  ui.editor.addEventListener("input", (event) => {
    if (isComposing) return;
    const previous = app.beforeEdit || app.lastEditorSnapshot;
    app.beforeEdit = null;
    if (previous && previous.text !== ui.editor.value) {
      pushHistory(previous, event.inputType || "edit");
      app.lastEditorSnapshot = snapshotEditor();
      markLocalEdit();
    }
  });

  ui.editor.addEventListener("keydown", (event) => {
    if (!(event.metaKey || event.ctrlKey) || event.altKey) return;
    const key = event.key.toLowerCase();
    if (key === "z") {
      event.preventDefault();
      if (event.shiftKey) redo(); else undo();
    } else if (key === "y") {
      event.preventDefault();
      redo();
    }
  });

  ui.undoButton.addEventListener("click", undo);
  ui.redoButton.addEventListener("click", redo);
  if (ui.selectAllButton) {
    ui.selectAllButton.addEventListener("click", () => {
      ui.editor.focus();
      ui.editor.select();
    });
  }
  ui.copyButton.addEventListener("click", copyAll);
  ui.clearButton.addEventListener("click", clearAll);
  if (ui.pinSubmitButton) ui.pinSubmitButton.addEventListener("click", submitPin);
  if (ui.pinInput) {
    ui.pinInput.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        submitPin();
      }
    });
  }
  if (ui.copyPinButton) {
    ui.copyPinButton.addEventListener("click", async () => {
      const pin = (app.pairing && app.pairing.pin) || "";
      if (!pin) return;
      try {
        if (navigator.clipboard && window.isSecureContext) {
          await navigator.clipboard.writeText(pin);
        } else {
          const helper = document.createElement("textarea");
          helper.value = pin;
          document.body.append(helper);
          helper.select();
          document.execCommand("copy");
          helper.remove();
        }
        showToast(t("app.pairing.code.copied", { value0: pin }));
      } catch (_) {
        showToast(t("app.cannot.copy.the.pairing.code.automatically"));
      }
    });
  }
  ui.useLatestButton.addEventListener("click", () => {
    if (!app.remote) return;
    app.base = null;
    app.dirty = false;
    app.conflict = false;
    setEditorFromServer(app.remote.text);
    renderEditorStatus();
    showToast(t("app.latest.version.applied"));
  });
  ui.keepMineButton.addEventListener("click", () => {
    if (!app.remote) return;
    app.base = app.remote;
    app.dirty = ui.editor.value !== app.remote.text;
    app.conflict = false;
    renderEditorStatus();
    if (app.dirty) scheduleSave(0);
  });
  ui.retryButton.addEventListener("click", refreshState);
  ui.rotateButton.addEventListener("click", async () => {
    ui.rotateButton.disabled = true;
    try {
      const { response, data } = await request("/api/pairing/rotate", { method: "POST", body: "{}" });
      if (!response.ok) { showToast(window.I18n.errorMessage(data, t("app.action.failed"))); return; }
      await refreshState();
      showToast(t("app.pairing.code.refreshed"));
    } catch (_) {
      showToast(t("app.cannot.refresh.the.pairing.code.try.again.later"));
    } finally {
      ui.rotateButton.disabled = false;
    }
  });
  ui.shutdownButton.addEventListener("click", async () => {
    if (app.dirty || app.saving || app.conflict) {
      showToast(t("app.wait.for.syncing.to.finish.or.resolve.the.text"));
      return;
    }
    ui.shutdownButton.disabled = true;
    try {
      const { response, data } = await request("/api/shutdown", { method: "POST", body: "{}" });
      if (!response.ok) { showToast(window.I18n.errorMessage(data, t("app.action.failed"))); return; }
      app.closedByUser = true;
      app.status = "offline";
      app.apiAvailable = false;
      app.dirty = false;
      clearTimeout(app.saveTimer);
      closeStream();
      renderAll();
    } catch (_) {
      showToast(t("app.cannot.close.fastinput.try.again.later"));
    } finally {
      ui.shutdownButton.disabled = false;
    }
  });

  if (ui.micButton) ui.micButton.addEventListener("click", toggleSpeech);
  if (ui.voiceHintClose) ui.voiceHintClose.addEventListener("click", hideVoiceHint);
  ui.sidebarToggleButton.addEventListener("click", () => setSidebarCollapsed(!app.sidebarCollapsed));
  if (ui.togglePairingButton) ui.togglePairingButton.addEventListener("click", () => setPairingCollapsed(false));
  if (ui.hidePairingButton) ui.hidePairingButton.addEventListener("click", () => setPairingCollapsed(true));

  function handleResume() {
    if (document.hidden) return;
    app.apiAvailable = true;
    refreshState(true);
    restartStream();
    if (app.dirty && !app.conflict) {
      scheduleSave(60);
    }
  }

  window.addEventListener("pagehide", () => {
    clearTimeout(app.saveTimer);
  });
  window.addEventListener("pageshow", () => {
    handleResume();
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) {
      handleResume();
    }
  });
  window.addEventListener("focus", () => {
    handleResume();
  });
  window.addEventListener("online", () => {
    handleResume();
  });
  window.addEventListener("beforeunload", (event) => {
    if (!app.dirty) return;
    event.preventDefault();
    event.returnValue = "";
  });

  // 主欄分頁（文字／檔案）。檔案區的邏輯在 files.js，這裡只負責切換與「有新檔案」提示。
  function setMainTab(name, persist = true) {
    // 實時分頁只給手機（桌面 Cookie 不能使用實時 API），且需要原生外殼；不記住為預設分頁。
    const showRealtime = name === "realtime" && !!ui.realtimeCard && app.role !== "desktop";
    const showFiles = !showRealtime && name === "files" && !!ui.filesCard;
    const previous = app.mainTab;
    app.mainTab = showRealtime ? "realtime" : (showFiles ? "files" : "text");
    if (ui.textPanel) ui.textPanel.hidden = showFiles || showRealtime;
    if (ui.filesCard) ui.filesCard.hidden = !showFiles;
    if (ui.realtimeCard) ui.realtimeCard.hidden = !showRealtime;
    if (ui.tabText) ui.tabText.setAttribute("aria-selected", String(!showFiles && !showRealtime));
    if (ui.tabFiles) ui.tabFiles.setAttribute("aria-selected", String(showFiles));
    if (ui.tabRealtime) ui.tabRealtime.setAttribute("aria-selected", String(showRealtime));
    if (showFiles && ui.filesDot) ui.filesDot.hidden = true;
    const rt = window.FastInputRealtime;
    if (rt) {
      if (showRealtime && previous !== "realtime" && typeof rt.activate === "function") rt.activate();
      if (!showRealtime && previous === "realtime" && typeof rt.deactivate === "function") rt.deactivate();
    }
    if (persist && !showRealtime) {
      try { localStorage.setItem("fi_main_tab", app.mainTab); }
      catch (_) { /* Browser storage may be disabled. */ }
    }
  }

  function noteFilesRevision(value) {
    if (value === undefined || value === null) return;
    const revision = Number(value);
    if (!Number.isFinite(revision)) return;
    if (app.filesRevision !== null && revision !== app.filesRevision
        && app.mainTab !== "files" && ui.filesDot) {
      ui.filesDot.hidden = false;
    }
    app.filesRevision = revision;
    if (window.FastInputFiles && typeof window.FastInputFiles.notifyRevision === "function") {
      window.FastInputFiles.notifyRevision(revision);
    }
  }

  if (ui.tabText) ui.tabText.addEventListener("click", () => setMainTab("text"));
  if (ui.tabFiles) ui.tabFiles.addEventListener("click", () => setMainTab("files"));
  if (ui.tabRealtime) ui.tabRealtime.addEventListener("click", () => setMainTab("realtime"));
  try { setMainTab(localStorage.getItem("fi_main_tab") === "files" ? "files" : "text", false); }
  catch (_) { setMainTab("text", false); }

  try { setSidebarCollapsed(localStorage.getItem("fi_sidebar_collapsed") === "1", false); }
  catch (_) { setSidebarCollapsed(false, false); }
  app.lastEditorSnapshot = snapshotEditor();
  renderEditorStatus();
  refreshState();
  setInterval(() => {
    if (!document.hidden) {
      refreshState();
    }
  }, 4000);
  document.addEventListener("languagechange", () => {
    app.saveProblem = window.I18n.localize(app.saveProblem);
    const pinErrorHidden = ui.pinError?.hidden;
    renderAll();
    if (ui.pinError) ui.pinError.hidden = pinErrorHidden;
    renderSpeechUI();
    setSidebarCollapsed(app.sidebarCollapsed, false);
  });
})();
