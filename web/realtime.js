(() => {
  "use strict";

  const { t } = window.I18n;

  const CLAIM_POLL_MS = 500;
  const CLAIM_TIMEOUT_MS = 30000;
  const STATUS_FAST_POLL_MS = 500;
  const STATUS_SLOW_POLL_MS = 2000;
  const HEARTBEAT_INTERVAL_MS = 1000;
  const LEASE_TICK_MS = 250;
  const MIN_SEND_INTERVAL_MS = 34; // <= 30 筆／秒
  const MAX_BATCH_SIZE = 8;
  const QUEUE_PAUSE_THRESHOLD = 24;
  const MAX_UTF16_UNITS = 4096;
  const MAX_UTF8_BYTES = 16384;
  const MAX_BODY_BYTES = 60000; // 安全低於 65,536 bytes 上限
  const MAX_EPOCH_SEQ = 10000;
  const ENTER_HOLD_MS = 600;

  const INPUT_KIND_WHITELIST = new Set([
    "insertText",
    "insertReplacementText",
    "insertCompositionText",
    "insertFromPaste",
    "insertFromDictation",
    "deleteContentBackward",
    "deleteContentForward",
    "deleteWordBackward",
    "deleteWordForward",
    "deleteByCut",
    "historyUndo",
    "historyRedo",
  ]);

  const REASON_MAP = {
    get focus_changed() { return t("realtime.focus.changed.on.your.computer"); },
    get content_diverged() { return t("realtime.content.changed.on.your.computer"); },
    get physical_input() { return t("realtime.keyboard.or.mouse.used.on.your.computer"); },
    get heartbeat_timeout() { return t("realtime.phone.disconnected.no.response.for.over.8.seconds"); },
    get idle_timeout() { return t("realtime.idle.for.too.long.no.input.for.over.60"); },
    get emergency_stop() { return t("realtime.emergency.stop.triggered.on.your.computer"); },
    get unsupported() { return t("realtime.this.app.is.not.supported"); },
    get expired() { return t("realtime.real.time.typing.permission.expired"); },
    get busy() { return t("realtime.another.real.time.typing.session.is.active"); },
    get released() { return t("realtime.real.time.typing.ended"); },
    get credential_revoked() { return t("realtime.phone.pairing.credentials.expired.or.were.removed"); },
    get native_disconnected() { return t("realtime.native.control.module.disconnected"); },
    get native_timeout() { return t("realtime.native.control.module.timed.out"); },
    get request_expired() { return t("realtime.computer.did.not.approve.within.30.seconds"); },
    get protocol_error() { return t("realtime.protocol.error"); },
    get sequence_conflict() { return t("realtime.text.sequence.conflict.sync.paused"); },
    get stale_state() { return t("realtime.pending.text.is.too.old.over.1.500.ms"); },
    get epoch_mismatch() { return t("realtime.target.field.changed.segment.mismatch"); },
    get queue_full() { return t("realtime.sync.queue.is.full.sync.paused"); },
    get rate_limited() { return t("realtime.sending.too.quickly.please.wait"); },
    get epoch_limit() { return t("realtime.this.field.has.reached.the.limit.of.10.000"); },
    get invalid_state() { return t("realtime.text.state.or.format.is.invalid"); },
    get target_changed() { return t("realtime.target.window.or.field.changed.on.your.computer"); },
    get locked() { return t("realtime.computer.screen.is.locked"); },
    get suspended() { return t("realtime.computer.is.asleep.or.suspended"); },
    get partial() { return t("realtime.some.text.may.not.have.been.applied"); },
    get unknown() { return t("realtime.cannot.confirm.whether.the.last.text.update.was.applied"); },
    get rejected() { return t("realtime.computer.rejected.this.input"); },
    get enter_sent() { return t("realtime.enter.sent.click.a.field.on.your.computer.to"); },
    get enter_disabled() { return t("realtime.virtual.enter.is.disabled.on.your.computer"); },
    get enter_blocked_target() { return t("realtime.virtual.enter.is.not.allowed.for.this.field"); },
    get text_pending() { return t("realtime.text.is.still.syncing.try.again.shortly"); },
    get enter_busy() { return t("realtime.virtual.enter.is.being.processed.please.wait"); },
  };

  const CODE_ERROR_MAP = {
    get busy() { return t("realtime.another.real.time.typing.session.is.active.2"); },
    get native_unavailable() { return t("realtime.native.real.time.typing.is.not.running.on.your"); },
    get not_paired() { return t("realtime.pair.your.phone.before.trying.again"); },
    get origin_rejected() { return t("realtime.origin.or.header.verification.failed"); },
    get invalid_capability() { return t("realtime.real.time.typing.credentials.expired.start.again"); },
    get invalid_claim() { return t("realtime.could.not.claim.permission.make.a.new.request"); },
    get expired() { return t("realtime.permission.timed.out.start.again"); },
    get queue_full() { return t("realtime.sync.queue.is.full.this.segment.is.paused"); },
    get sequence_conflict() { return t("realtime.sequence.conflict.this.segment.is.paused"); },
    get epoch_mismatch() { return t("realtime.target.field.changed.on.your.computer"); },
    get request_mismatch() { return t("realtime.permission.session.changed"); },
    get epoch_limit() { return t("realtime.the.limit.of.10.000.updates.has.been.reached"); },
    get forbidden_character() { return t("realtime.unsupported.control.character.such.as.a.line.break.or"); },
    get text_too_large() { return t("realtime.text.exceeds.the.real.time.typing.limit"); },
    get body_too_large() { return t("realtime.request.exceeds.the.transfer.size.limit"); },
    get enter_disabled() { return t("realtime.virtual.enter.is.disabled.on.your.computer"); },
    get text_pending() { return t("realtime.text.is.still.syncing.try.again.shortly"); },
    get invalid_hold() { return t("realtime.hold.for.at.least.0.6.seconds"); },
    get enter_busy() { return t("realtime.virtual.enter.is.being.processed.please.wait"); },
    get enter_blocked_target() { return t("realtime.virtual.enter.is.not.allowed.for.this.field"); },
  };

  const textEncoder = typeof TextEncoder === "function" ? new TextEncoder() : null;
  const pageStartPerf = typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();

  let capabilityToken = null; // 僅存於閉包記憶體，絕不寫入 storage / URL / console / DOM

  const state = {
    initialized: false,
    activeTab: false,
    serverBootId: null,
    requestId: null,
    cancelledRequestId: null,
    sessionId: null,
    clientNonce: null,
    checkCode: null,
    targetEpoch: null,
    confirmedEpoch: null,
    confirmingEpoch: null,
    activeSegmentEpoch: null,
    phase: "DISABLED",
    available: true,
    acceptedSeq: 0,
    appliedSeq: 0,
    expectedSeq: 1,
    seqStatus: null,
    proof: null,
    reason: null,
    localNotice: null,
    leaseRemainingMs: null,
    leaseSyncedAtPerf: 0,
    requestStartedAt: 0,

    // 虛擬 Enter 狀態與長按控制
    enterAvailable: false,
    targetApp: null,
    enterSnapshot: null,
    enterHolding: false,
    enterPointerId: null,
    enterHoldStartPerf: 0,
    enterHoldTimer: null,
    enterInFlight: false,
    enterLocked: false,
    enterRetryPayload: null,
    enterRetryTimer: null,
    enterRetryCount: 0,
    enterCooldownTimer: null,
    enterFeedbackMsg: null,
    enterFeedbackTimer: null,

    // 編輯器與序號佇列狀態
    nextSeq: 1,
    lastValidText: "",
    lastValidStart: 0,
    lastValidEnd: 0,
    lastValidDir: "none",
    lastQueuedText: "",
    lastInputType: null,
    isComposing: false,
    pendingCapture: false,
    rafId: null,
    rafTimer: null,
    lastSendPerf: 0,
    sendScheduleTimer: null,

    // 狀態傳送與重試佇列：seq -> stateItem
    stateHistory: new Map(),
    pendingQueue: [],
    inFlightBatch: null,
    retryBatch: null,
    retryTimer: null,
    retryCount: 0,

    // 已鎖定／灰化的歷史段落
    lockedSegments: [],

    // 計時器與連線
    claimTimer: null,
    statusTimer: null,
    statusFastMode: false,
    heartbeatTimer: null,
    leaseTimer: null,
    toastTimer: null,
    eventSource: null,
    statusInFlight: false,
    heartbeatInFlight: false,
  };

  let ui = null;

  function byId(id) {
    return document.getElementById(id);
  }

  function nowPerf() {
    return typeof performance !== "undefined" && typeof performance.now === "function"
      ? performance.now()
      : Date.now() - pageStartPerf;
  }

  function elapsedMs() {
    return Math.max(0, Math.floor(nowPerf()));
  }

  function utf8ByteLength(str) {
    if (textEncoder) {
      return textEncoder.encode(str).byteLength;
    }
    let bytes = 0;
    for (let i = 0; i < str.length; i++) {
      const code = str.charCodeAt(i);
      if (code <= 0x7f) bytes += 1;
      else if (code <= 0x7ff) bytes += 2;
      else if (code >= 0xd800 && code <= 0xdbff) {
        bytes += 4;
        i += 1;
      } else {
        bytes += 3;
      }
    }
    return bytes;
  }

  function generateClientNonce() {
    const bytes = new Uint8Array(16);
    if (typeof crypto !== "undefined" && typeof crypto.getRandomValues === "function") {
      crypto.getRandomValues(bytes);
    } else {
      for (let i = 0; i < 16; i++) {
        bytes[i] = Math.floor(Math.random() * 256);
      }
    }
    let hex = "";
    for (let i = 0; i < bytes.length; i++) {
      hex += bytes[i].toString(16).padStart(2, "0");
    }
    return hex;
  }

  function translateReason(reason) {
    if (!reason || typeof reason !== "string") return null;
    return REASON_MAP[reason] || t("realtime.sync.state.changed.follow.the.on.screen.instructions");
  }

  function translateCodeError(code, status, data) {
    const localized = window.I18n.errorMessage({ ...data, code });
    if (localized) return localized;
    if (code && CODE_ERROR_MAP[code]) {
      return CODE_ERROR_MAP[code];
    }
    if (status === 401) return CODE_ERROR_MAP.not_paired;
    if (status === 403) return CODE_ERROR_MAP.invalid_capability;
    if (status === 410) return CODE_ERROR_MAP.expired;
    if (status === 503) return CODE_ERROR_MAP.native_unavailable;
    return t("realtime.action.could.not.be.completed.check.your.network.and");
  }

  function getTailPreviewText(text, maxUnits = 20) {
    if (typeof text !== "string" || text.length === 0) return "";
    if (text.length <= maxUnits) return text;
    let start = text.length - maxUnits;
    const code = text.charCodeAt(start);
    if (code >= 0xdc00 && code <= 0xdfff) {
      start += 1;
    }
    return text.slice(start);
  }

  function validateRealtimeText(text) {
    if (typeof text !== "string") {
      return { valid: false, reason: t("realtime.invalid.text.format") };
    }
    if (text.length > MAX_UTF16_UNITS) {
      return { valid: false, reason: t("realtime.text.exceeds.the.limit.of.utf.16.units", { value0: MAX_UTF16_UNITS }) };
    }
    if (utf8ByteLength(text) > MAX_UTF8_BYTES) {
      return { valid: false, reason: t("realtime.text.exceeds.16.kib.cannot.continue.typing") };
    }
    for (let i = 0; i < text.length; i++) {
      const code = text.charCodeAt(i);
      if (code === 0x0a || code === 0x0d || code === 0x2028 || code === 0x2029) {
        return { valid: false, reason: t("realtime.line.breaks.are.not.supported.in.real.time.typing") };
      }
      if (code === 0x09) {
        return { valid: false, reason: t("realtime.tab.is.not.supported.in.real.time.typing") };
      }
      if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) {
        return { valid: false, reason: t("realtime.control.characters.are.not.supported.in.real.time.typing") };
      }
      if (
        code === 0x061c ||
        code === 0x200e ||
        code === 0x200f ||
        (code >= 0x202a && code <= 0x202e) ||
        (code >= 0x2066 && code <= 0x2069)
      ) {
        return { valid: false, reason: t("realtime.bidirectional.text.controls.are.not.supported.in.real.time") };
      }
      if (code >= 0xd800 && code <= 0xdbff) {
        if (i + 1 >= text.length) {
          return { valid: false, reason: t("realtime.incomplete.unicode.character.unpaired.surrogate") };
        }
        const next = text.charCodeAt(i + 1);
        if (next < 0xdc00 || next > 0xdfff) {
          return { valid: false, reason: t("realtime.incomplete.unicode.character.unpaired.surrogate") };
        }
        i += 1;
      } else if (code >= 0xdc00 && code <= 0xdfff) {
        return { valid: false, reason: t("realtime.incomplete.unicode.character.unpaired.surrogate") };
      }
    }
    return { valid: true, reason: null };
  }

  function clampSelectionToSurrogateBoundary(text, index) {
    const n = Number(index);
    if (!Number.isFinite(n) || n <= 0) return 0;
    if (n >= text.length) return text.length;
    const prev = text.charCodeAt(n - 1);
    const curr = text.charCodeAt(n);
    if (prev >= 0xd800 && prev <= 0xdbff && curr >= 0xdc00 && curr <= 0xdfff) {
      return n + 1;
    }
    return n;
  }

  function normalizeDirection(dir) {
    if (dir === "forward" || dir === "backward" || dir === "none") {
      return dir;
    }
    return "none";
  }

  function showToast(message, duration = 3200) {
    let toastEl = byId("toast");
    if (!toastEl) {
      toastEl = document.createElement("div");
      toastEl.id = "toast";
      toastEl.className = "toast";
      toastEl.setAttribute("role", "status");
      toastEl.setAttribute("aria-live", "polite");
      document.body.append(toastEl);
    }
    clearTimeout(state.toastTimer);
    window.I18n.bind(toastEl, "textContent", () => message);
    toastEl.hidden = false;
    state.toastTimer = setTimeout(() => {
      toastEl.hidden = true;
    }, duration);
  }

  function showAlert(message) {
    if (!ui || !ui.alert || !ui.alertText) return;
    window.I18n.bind(ui.alertText, "textContent", () => message);
    ui.alert.hidden = false;
    if (ui.liveStatus) {
      window.I18n.bind(ui.liveStatus, "textContent", () => message);
    }
  }

  function hideAlert() {
    if (!ui || !ui.alert || !ui.alertText) return;
    ui.alert.hidden = true;
    ui.alertText.textContent = "";
  }

  function buildEnterSectionDOM() {
    const enterSection = document.createElement("div");
    enterSection.id = "rtEnterSection";
    enterSection.className = "rt-enter-section";
    enterSection.hidden = true;

    const enterHeader = document.createElement("div");
    enterHeader.className = "rt-enter-header";

    const enterBadge = document.createElement("span");
    enterBadge.className = "rt-enter-badge";
    window.I18n.bind(enterBadge, "textContent", () => t("realtime.virtual.enter"));

    const enterDesc = document.createElement("span");
    enterDesc.id = "rtEnterDesc";
    enterDesc.className = "rt-enter-desc";
    window.I18n.bind(enterDesc, "textContent", () => t("realtime.hold.for.0.6.seconds.to.send.enter.release"));

    enterHeader.append(enterBadge, enterDesc);

    const enterButton = document.createElement("button");
    enterButton.id = "rtEnterButton";
    enterButton.type = "button";
    enterButton.tabIndex = -1;
    enterButton.setAttribute("tabindex", "-1");
    enterButton.className = "rt-enter-btn";
    window.I18n.bindAttribute(enterButton, "aria-label", () => t("realtime.hold.to.send.enter.at.least.0.6.seconds"));
    enterButton.setAttribute("aria-describedby", "rtEnterDesc rtEnterConfirm rtEnterFeedback");

    const ringWrap = document.createElement("span");
    ringWrap.className = "rt-enter-ring";
    ringWrap.setAttribute("aria-hidden", "true");

    const svgNS = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(svgNS, "svg");
    svg.setAttribute("class", "rt-enter-ring-svg");
    svg.setAttribute("viewBox", "0 0 36 36");
    svg.setAttribute("focusable", "false");
    svg.setAttribute("aria-hidden", "true");

    const trackCircle = document.createElementNS(svgNS, "circle");
    trackCircle.setAttribute("class", "rt-enter-ring-track");
    trackCircle.setAttribute("cx", "18");
    trackCircle.setAttribute("cy", "18");
    trackCircle.setAttribute("r", "15");
    trackCircle.setAttribute("pathLength", "100");

    const fillCircle = document.createElementNS(svgNS, "circle");
    fillCircle.setAttribute("class", "rt-enter-ring-fill");
    fillCircle.setAttribute("cx", "18");
    fillCircle.setAttribute("cy", "18");
    fillCircle.setAttribute("r", "15");
    fillCircle.setAttribute("pathLength", "100");

    svg.append(trackCircle, fillCircle);

    const ringIcon = document.createElement("span");
    ringIcon.className = "rt-enter-ring-icon";
    ringIcon.textContent = "↵";

    ringWrap.append(svg, ringIcon);

    const enterButtonLabel = document.createElement("span");
    enterButtonLabel.id = "rtEnterButtonLabel";
    enterButtonLabel.className = "rt-enter-btn-label";
    window.I18n.bind(enterButtonLabel, "textContent", () => t("realtime.hold.to.send.enter"));

    enterButton.append(ringWrap, enterButtonLabel);

    const enterConfirm = document.createElement("div");
    enterConfirm.id = "rtEnterConfirm";
    enterConfirm.className = "rt-enter-confirm";
    enterConfirm.setAttribute("role", "status");
    enterConfirm.setAttribute("aria-live", "polite");
    enterConfirm.hidden = true;

    const enterConfirmTarget = document.createElement("p");
    enterConfirmTarget.id = "rtEnterConfirmTarget";
    enterConfirmTarget.className = "rt-enter-confirm-target";
    window.I18n.bind(enterConfirmTarget, "textContent", () => t("realtime.enter.will.be.sent.to.the.current.field"));

    const enterConfirmTailRow = document.createElement("p");
    enterConfirmTailRow.id = "rtEnterConfirmTailRow";
    enterConfirmTailRow.className = "rt-enter-confirm-tail";

    const tailPrefix = document.createElement("span");
    window.I18n.bind(tailPrefix, "textContent", () => t("realtime.end.of.text"));

    const enterConfirmTail = document.createElement("strong");
    enterConfirmTail.id = "rtEnterConfirmTail";
    window.I18n.bind(enterConfirmTail, "textContent", () => t("realtime.no.text.yet"));

    enterConfirmTailRow.append(tailPrefix, enterConfirmTail);
    enterConfirm.append(enterConfirmTarget, enterConfirmTailRow);

    const enterFeedback = document.createElement("p");
    enterFeedback.id = "rtEnterFeedback";
    enterFeedback.className = "rt-enter-feedback";
    enterFeedback.setAttribute("role", "status");
    enterFeedback.setAttribute("aria-live", "polite");
    enterFeedback.hidden = true;

    enterSection.append(enterHeader, enterButton, enterConfirm, enterFeedback);
    return enterSection;
  }

  function ensureCardStructure(card) {
    if (
      byId("rtPhasePill") &&
      byId("rtStatusTitle") &&
      byId("rtStartButton") &&
      byId("rtInput")
    ) {
      if (!byId("rtEnterSection") || !byId("rtEnterButton")) {
        const enterDom = buildEnterSectionDOM();
        const inputWrapEl = byId("rtInputWrap");
        if (inputWrapEl && inputWrapEl.parentNode === card) {
          inputWrapEl.after(enterDom);
        } else {
          card.append(enterDom);
        }
      }
      return {
        card,
        phasePill: byId("rtPhasePill"),
        phaseDot: byId("rtPhaseDot"),
        phaseText: byId("rtPhaseText"),
        leasePill: byId("rtLeasePill"),
        statusBox: byId("rtStatusBox"),
        statusTitle: byId("rtStatusTitle"),
        statusDetail: byId("rtStatusDetail"),
        startButton: byId("rtStartButton"),
        releaseButton: byId("rtReleaseButton"),
        checkCodeWrap: byId("rtCheckCodeWrap"),
        checkCodeDigits: byId("rtCheckCodeDigits"),
        alert: byId("rtAlert"),
        alertText: byId("rtAlertText"),
        alertClose: byId("rtAlertClose"),
        lockedSection: byId("rtLockedSection"),
        lockedList: byId("rtLockedList"),
        lockedClearButton: byId("rtLockedClearButton"),
        inputWrap: byId("rtInputWrap"),
        textarea: byId("rtInput"),
        syncProgress: byId("rtSyncProgress"),
        charCounter: byId("rtCharCounter"),
        enterSection: byId("rtEnterSection"),
        enterDesc: byId("rtEnterDesc"),
        enterButton: byId("rtEnterButton"),
        enterButtonLabel: byId("rtEnterButtonLabel"),
        enterConfirm: byId("rtEnterConfirm"),
        enterConfirmTarget: byId("rtEnterConfirmTarget"),
        enterConfirmTail: byId("rtEnterConfirmTail"),
        enterFeedback: byId("rtEnterFeedback"),
        liveStatus: byId("rtLiveStatus"),
      };
    }

    card.replaceChildren();
    card.setAttribute("aria-labelledby", "rtHeading");

    const header = document.createElement("div");
    header.className = "rt-header";

    const headingGroup = document.createElement("div");
    headingGroup.className = "rt-heading-group";

    const iconBadge = document.createElement("span");
    iconBadge.className = "rt-icon-badge";
    iconBadge.setAttribute("aria-hidden", "true");
    iconBadge.textContent = "⚡";

    const titleWrap = document.createElement("div");
    const kicker = document.createElement("p");
    kicker.className = "section-kicker";
    window.I18n.bind(kicker, "textContent", () => t("realtime.live.typing.sync"));

    const h2 = document.createElement("h2");
    h2.id = "rtHeading";
    h2.className = "rt-title";
    window.I18n.bind(h2, "textContent", () => t("realtime.real.time.typing"));

    titleWrap.append(kicker, h2);
    headingGroup.append(iconBadge, titleWrap);

    const headerBadges = document.createElement("div");
    headerBadges.className = "rt-header-badges";

    const leasePill = document.createElement("span");
    leasePill.id = "rtLeasePill";
    leasePill.className = "rt-lease-pill";
    leasePill.hidden = true;
    window.I18n.bind(leasePill, "textContent", () => t("realtime.05.00.remaining"));

    const phasePill = document.createElement("span");
    phasePill.id = "rtPhasePill";
    phasePill.className = "rt-phase-pill";
    phasePill.dataset.phase = "DISABLED";
    phasePill.setAttribute("role", "status");
    phasePill.setAttribute("aria-live", "polite");

    const phaseDot = document.createElement("span");
    phaseDot.id = "rtPhaseDot";
    phaseDot.className = "rt-phase-dot";
    phaseDot.setAttribute("aria-hidden", "true");

    const phaseText = document.createElement("span");
    phaseText.id = "rtPhaseText";
    window.I18n.bind(phaseText, "textContent", () => t("realtime.standby"));

    phasePill.append(phaseDot, phaseText);
    headerBadges.append(leasePill, phasePill);
    header.append(headingGroup, headerBadges);

    const desc = document.createElement("p");
    desc.className = "rt-description";
    window.I18n.bind(desc, "textContent", () => t("realtime.type.on.your.phone.to.enter.text.directly.into"));

    const statusBox = document.createElement("div");
    statusBox.id = "rtStatusBox";
    statusBox.className = "rt-status-box";
    statusBox.setAttribute("role", "status");
    statusBox.setAttribute("aria-live", "polite");

    const statusTop = document.createElement("div");
    statusTop.className = "rt-status-top";

    const statusCopy = document.createElement("div");
    statusCopy.className = "rt-status-copy";

    const statusTitle = document.createElement("strong");
    statusTitle.id = "rtStatusTitle";
    statusTitle.className = "rt-status-title";
    window.I18n.bind(statusTitle, "textContent", () => t("realtime.ready.for.real.time.typing"));

    const statusDetail = document.createElement("p");
    statusDetail.id = "rtStatusDetail";
    statusDetail.className = "rt-status-detail";
    window.I18n.bind(statusDetail, "textContent", () => t("realtime.start.real.time.typing.then.check.the.code.and"));

    statusCopy.append(statusTitle, statusDetail);

    const actions = document.createElement("div");
    actions.className = "rt-actions";

    const startButton = document.createElement("button");
    startButton.id = "rtStartButton";
    startButton.type = "button";
    startButton.className = "button button-primary rt-btn";
    window.I18n.bind(startButton, "textContent", () => t("realtime.start.real.time.typing"));

    const releaseButton = document.createElement("button");
    releaseButton.id = "rtReleaseButton";
    releaseButton.type = "button";
    releaseButton.className = "button button-danger rt-btn";
    releaseButton.hidden = true;
    window.I18n.bind(releaseButton, "textContent", () => t("realtime.end.real.time.typing"));

    actions.append(startButton, releaseButton);
    statusTop.append(statusCopy, actions);

    const checkCodeWrap = document.createElement("div");
    checkCodeWrap.id = "rtCheckCodeWrap";
    checkCodeWrap.className = "rt-checkcode-wrap";
    checkCodeWrap.hidden = true;

    const checkCodeLabel = document.createElement("span");
    checkCodeLabel.className = "rt-checkcode-label";
    window.I18n.bind(checkCodeLabel, "textContent", () => t("realtime.check.the.2.digit.code.and.approve.on.your"));

    const checkCodeDigits = document.createElement("span");
    checkCodeDigits.id = "rtCheckCodeDigits";
    checkCodeDigits.className = "rt-checkcode-digits";
    window.I18n.bindAttribute(checkCodeDigits, "aria-label", () => t("realtime.2.digit.verification.code"));
    checkCodeDigits.textContent = "--";

    checkCodeWrap.append(checkCodeLabel, checkCodeDigits);
    statusBox.append(statusTop, checkCodeWrap);

    const alert = document.createElement("div");
    alert.id = "rtAlert";
    alert.className = "rt-alert";
    alert.setAttribute("role", "alert");
    alert.hidden = true;

    const alertText = document.createElement("p");
    alertText.id = "rtAlertText";
    alertText.className = "rt-alert-text";

    const alertClose = document.createElement("button");
    alertClose.id = "rtAlertClose";
    alertClose.type = "button";
    alertClose.className = "rt-alert-close";
    window.I18n.bindAttribute(alertClose, "aria-label", () => t("files.dismiss.message"));
    window.I18n.bind(alertClose, "textContent", () => t("files.close"));

    alert.append(alertText, alertClose);

    const lockedSection = document.createElement("div");
    lockedSection.id = "rtLockedSection";
    lockedSection.className = "rt-locked-section";
    lockedSection.hidden = true;

    const lockedHeader = document.createElement("div");
    lockedHeader.className = "rt-locked-header";

    const lockedTitle = document.createElement("span");
    window.I18n.bind(lockedTitle, "textContent", () => t("realtime.previous.segments.are.locked.read.only.and.never.resent"));

    const lockedClearButton = document.createElement("button");
    lockedClearButton.id = "rtLockedClearButton";
    lockedClearButton.type = "button";
    lockedClearButton.className = "rt-locked-clear";
    window.I18n.bind(lockedClearButton, "textContent", () => t("realtime.clear.history"));

    lockedHeader.append(lockedTitle, lockedClearButton);

    const lockedList = document.createElement("div");
    lockedList.id = "rtLockedList";
    lockedList.className = "rt-locked-list";

    lockedSection.append(lockedHeader, lockedList);

    const inputWrap = document.createElement("div");
    inputWrap.id = "rtInputWrap";
    inputWrap.className = "rt-input-wrap is-locked";

    const label = document.createElement("label");
    label.className = "rt-sr-only";
    label.setAttribute("for", "rtInput");
    window.I18n.bind(label, "textContent", () => t("realtime.real.time.typing.area"));

    const textarea = document.createElement("textarea");
    textarea.id = "rtInput";
    textarea.className = "rt-textarea";
    textarea.setAttribute("autocomplete", "off");
    textarea.setAttribute("autocorrect", "off");
    textarea.setAttribute("autocapitalize", "off");
    textarea.setAttribute("spellcheck", "false");
    textarea.setAttribute("rows", "4");
    window.I18n.bindAttribute(textarea, "placeholder", () => t("realtime.approve.and.select.a.field.on.your.computer.then"));
    textarea.readOnly = true;
    textarea.disabled = true;

    const inputFooter = document.createElement("div");
    inputFooter.className = "rt-input-footer";

    const syncProgress = document.createElement("span");
    syncProgress.id = "rtSyncProgress";
    syncProgress.className = "rt-sync-progress";
    syncProgress.setAttribute("aria-live", "polite");
    window.I18n.bind(syncProgress, "textContent", () => t("realtime.not.started"));

    const charCounter = document.createElement("span");
    charCounter.id = "rtCharCounter";
    charCounter.className = "rt-char-counter";
    window.I18n.bind(charCounter, "textContent", () => t("realtime.0.4.096.characters.0.16.384.b"));

    inputFooter.append(syncProgress, charCounter);
    inputWrap.append(label, textarea, inputFooter);

    const enterSection = buildEnterSectionDOM();

    const liveStatus = document.createElement("div");
    liveStatus.id = "rtLiveStatus";
    liveStatus.className = "rt-sr-only";
    liveStatus.setAttribute("role", "status");
    liveStatus.setAttribute("aria-live", "polite");

    card.append(header, desc, statusBox, alert, lockedSection, inputWrap, enterSection, liveStatus);

    return {
      card,
      phasePill,
      phaseDot,
      phaseText,
      leasePill,
      statusBox,
      statusTitle,
      statusDetail,
      startButton,
      releaseButton,
      checkCodeWrap,
      checkCodeDigits,
      alert,
      alertText,
      alertClose,
      lockedSection,
      lockedList,
      lockedClearButton,
      inputWrap,
      textarea,
      syncProgress,
      charCounter,
      enterSection,
      enterDesc: byId("rtEnterDesc"),
      enterButton: byId("rtEnterButton"),
      enterButtonLabel: byId("rtEnterButtonLabel"),
      enterConfirm: byId("rtEnterConfirm"),
      enterConfirmTarget: byId("rtEnterConfirmTarget"),
      enterConfirmTail: byId("rtEnterConfirmTail"),
      enterFeedback: byId("rtEnterFeedback"),
      liveStatus,
    };
  }

  function canShowEnterSection() {
    const localLastSeq = state.nextSeq - 1;
    const queuesEmpty =
      state.pendingQueue.length === 0 &&
      !state.inFlightBatch &&
      !state.retryBatch &&
      !state.pendingCapture &&
      !state.isComposing &&
      state.lastValidText === state.lastQueuedText;
    const seqsSynced =
      state.acceptedSeq === state.appliedSeq &&
      state.appliedSeq === localLastSeq;
    return (
      state.enterAvailable === true &&
      state.phase === "ACTIVE" &&
      queuesEmpty &&
      seqsSynced
    );
  }

  function setEnterFeedback(message, autoClearMs = 2400) {
    clearTimeout(state.enterFeedbackTimer);
    state.enterFeedbackTimer = null;
    state.enterFeedbackMsg = message || null;
    if (message && autoClearMs > 0) {
      state.enterFeedbackTimer = setTimeout(() => {
        state.enterFeedbackTimer = null;
        if (state.enterFeedbackMsg === message) {
          state.enterFeedbackMsg = null;
          renderEnterSection();
        }
      }, autoClearMs);
    }
  }

  function cancelEnterHold(showEarlyReleaseHint = false) {
    if (state.enterHoldTimer !== null) {
      clearTimeout(state.enterHoldTimer);
      state.enterHoldTimer = null;
    }
    const wasHolding = state.enterHolding;
    const ptrId = state.enterPointerId;
    state.enterHolding = false;
    state.enterPointerId = null;
    state.enterHoldStartPerf = 0;

    if (ui && ui.enterButton && ptrId !== null) {
      try {
        if (typeof ui.enterButton.releasePointerCapture === "function") {
          ui.enterButton.releasePointerCapture(ptrId);
        }
      } catch (_) {}
    }

    if (ui && ui.enterSection && ui.enterButton) {
      ui.enterSection.classList.remove("is-holding");
      ui.enterButton.classList.remove("is-holding");
    }

    if (wasHolding && showEarlyReleaseHint) {
      setEnterFeedback(t("realtime.hold.to.send"), 2200);
      showToast(t("realtime.hold.to.send"), 2200);
      if (ui && ui.liveStatus) {
        window.I18n.bind(ui.liveStatus, "textContent", () => t("realtime.hold.to.send"));
      }
    }

    renderEnterSection();
  }

  function clearEnterState() {
    cancelEnterHold(false);
    clearTimeout(state.enterRetryTimer);
    state.enterRetryTimer = null;
    clearTimeout(state.enterCooldownTimer);
    state.enterCooldownTimer = null;
    clearTimeout(state.enterFeedbackTimer);
    state.enterFeedbackTimer = null;
    state.enterInFlight = false;
    state.enterLocked = false;
    state.enterRetryPayload = null;
    state.enterRetryCount = 0;
    state.enterFeedbackMsg = null;
  }

  function renderEnterSection() {
    if (!ui || !ui.enterSection || !ui.enterButton) return;

    const visible = canShowEnterSection();
    if (!visible) {
      if (state.enterHolding) {
        cancelEnterHold(false);
        return;
      }
      ui.enterSection.hidden = true;
      if (ui.enterConfirm) ui.enterConfirm.hidden = true;
      return;
    }

    ui.enterSection.hidden = false;

    const targetLabel = state.targetApp ? state.targetApp : t("realtime.current.field");
    if (ui.enterConfirmTarget) {
      window.I18n.bind(ui.enterConfirmTarget, "textContent", () => t("realtime.enter.will.be.sent.to", { value0: targetLabel }));
    }

    const tail = getTailPreviewText(state.lastValidText, 20);
    if (ui.enterConfirmTail) {
      window.I18n.bind(ui.enterConfirmTail, "textContent", () => tail.length > 0 ? tail : t("realtime.no.text.yet"));
    }

    const isRequestedInSnap = Boolean(
      state.enterSnapshot && state.enterSnapshot.status === "requested"
    );
    const isBusyOrLocked = Boolean(
      state.enterLocked ||
      state.enterInFlight ||
      state.enterRetryPayload ||
      isRequestedInSnap
    );

    ui.enterButton.disabled = isBusyOrLocked;
    ui.enterButton.classList.toggle("is-locked", isBusyOrLocked);
    ui.enterSection.classList.toggle("is-holding", state.enterHolding);
    ui.enterButton.classList.toggle("is-holding", state.enterHolding);

    if (ui.enterConfirm) {
      ui.enterConfirm.hidden = !state.enterHolding;
    }

    if (ui.enterButtonLabel) {
      if (state.enterInFlight || state.enterRetryPayload || isRequestedInSnap) {
        window.I18n.bind(ui.enterButtonLabel, "textContent", () => t("realtime.sending.enter"));
      } else if (state.enterLocked) {
        window.I18n.bind(ui.enterButtonLabel, "textContent", () => t("realtime.try.again.shortly"));
      } else if (state.enterHolding) {
        window.I18n.bind(ui.enterButtonLabel, "textContent", () => t("realtime.keep.holding.to.send.enter"));
      } else {
        window.I18n.bind(ui.enterButtonLabel, "textContent", () => t("realtime.hold.to.send.enter"));
      }
    }

    if (ui.enterFeedback) {
      if (state.enterFeedbackMsg) {
        ui.enterFeedback.textContent = state.enterFeedbackMsg;
        ui.enterFeedback.hidden = false;
      } else {
        ui.enterFeedback.textContent = "";
        ui.enterFeedback.hidden = true;
      }
    }
  }

  function handleEnterPointerDown(event) {
    if (!ui || !ui.enterButton) return;
    if (event.isPrimary === false) return;
    if (typeof event.button === "number" && event.button !== 0) return;
    if (!canShowEnterSection()) return;
    if (
      state.enterHolding ||
      state.enterLocked ||
      state.enterInFlight ||
      state.enterRetryPayload
    ) {
      return;
    }

    event.preventDefault();

    clearTimeout(state.enterFeedbackTimer);
    state.enterFeedbackTimer = null;
    state.enterFeedbackMsg = null;

    state.enterHolding = true;
    state.enterPointerId = event.pointerId !== undefined ? event.pointerId : null;
    state.enterHoldStartPerf = nowPerf();

    if (state.enterPointerId !== null) {
      try {
        if (typeof ui.enterButton.setPointerCapture === "function") {
          ui.enterButton.setPointerCapture(state.enterPointerId);
        }
      } catch (_) {}
    }

    renderEnterSection();

    clearTimeout(state.enterHoldTimer);
    state.enterHoldTimer = setTimeout(() => {
      onEnterHoldComplete();
    }, ENTER_HOLD_MS);
  }

  function handleEnterPointerEnd(event) {
    if (!state.enterHolding) return;
    if (
      state.enterPointerId !== null &&
      event &&
      event.pointerId !== undefined &&
      event.pointerId !== state.enterPointerId
    ) {
      return;
    }
    if (event && typeof event.preventDefault === "function") {
      event.preventDefault();
    }
    cancelEnterHold(true);
  }

  function onEnterHoldComplete() {
    state.enterHoldTimer = null;
    if (!state.enterHolding) return;

    if (
      !canShowEnterSection() ||
      state.enterLocked ||
      state.enterInFlight ||
      state.enterRetryPayload ||
      !capabilityToken ||
      !state.requestId ||
      !state.targetEpoch
    ) {
      cancelEnterHold(false);
      return;
    }

    const holdMs = Math.max(
      ENTER_HOLD_MS,
      Math.round(nowPerf() - state.enterHoldStartPerf)
    );
    const ptrId = state.enterPointerId;
    state.enterHolding = false;
    state.enterPointerId = null;
    state.enterHoldStartPerf = 0;

    if (ui && ui.enterButton && ptrId !== null) {
      try {
        if (typeof ui.enterButton.releasePointerCapture === "function") {
          ui.enterButton.releasePointerCapture(ptrId);
        }
      } catch (_) {}
    }

    // 同一次長按只產生唯一一個 32 位十六進位 enter_id，網路逾時重送沿用此 payload
    const enterId = generateClientNonce();
    const payload = {
      v: 1,
      request_id: state.requestId,
      target_epoch: state.targetEpoch,
      enter_id: enterId,
      after_seq: state.appliedSeq,
      hold_ms: holdMs,
    };

    state.enterLocked = true;
    state.enterRetryCount = 0;
    sendEnterRequest(payload);
  }

  async function sendEnterRequest(payload) {
    if (!capabilityToken || !state.requestId || !state.targetEpoch) {
      state.enterLocked = false;
      renderUI();
      return;
    }
    if (state.enterInFlight) return;

    state.enterInFlight = true;
    state.enterLocked = true;
    setEnterFeedback(t("realtime.sending.enter"), 0);
    renderUI();

    const { ok, status, data, networkError } = await apiPost(
      "/api/realtime/enter",
      payload,
      true,
      false
    );

    if (
      state.requestId !== payload.request_id ||
      (state.targetEpoch && state.targetEpoch !== payload.target_epoch)
    ) {
      state.enterInFlight = false;
      state.enterRetryPayload = null;
      return;
    }

    if (networkError || status === 408 || status === 0) {
      // 網路逾時／結果不明：以同 enter_id 同內容原樣重送，不產生新 enter_id
      state.enterInFlight = false;
      if (state.phase !== "ACTIVE" || !capabilityToken) {
        state.enterRetryPayload = null;
        state.enterLocked = false;
        setEnterFeedback(null, 0);
        renderUI();
        return;
      }
      state.enterRetryPayload = payload;
      state.enterRetryCount += 1;
      const delay = Math.min(1000, 150 * state.enterRetryCount);
      setEnterFeedback(t("realtime.network.timeout.retrying.with.the.same.identifier"), 0);
      showAlert(t("realtime.network.timeout.resending.unchanged.with.the.same.enter.id"));
      clearTimeout(state.enterRetryTimer);
      state.enterRetryTimer = setTimeout(() => {
        state.enterRetryTimer = null;
        if (
          state.enterRetryPayload &&
          state.phase === "ACTIVE" &&
          state.targetEpoch === payload.target_epoch &&
          state.requestId === payload.request_id &&
          capabilityToken
        ) {
          const retryPayload = state.enterRetryPayload;
          state.enterRetryPayload = null;
          sendEnterRequest(retryPayload);
        }
      }, delay);
      renderUI();
      return;
    }

    state.enterInFlight = false;
    state.enterRetryPayload = null;
    state.enterRetryCount = 0;

    if (ok && data) {
      setEnterFeedback(null, 0);
      hideAlert();
      // 成功後維持按鈕鎖定直到 phase 變化，絕不自動再次送 Enter
      applySnapshot(data);
      if (state.phase === "PAUSED" && state.reason === "enter_sent") {
        showToast(t("realtime.enter.sent.click.a.field.on.your.computer.to"));
      }
      return;
    }

    const code = data && data.code;

    if (code === "enter_disabled") {
      state.enterAvailable = false;
      state.enterLocked = false;
      setEnterFeedback(null, 0);
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const msg = t("realtime.virtual.enter.is.disabled.on.your.computer");
      showAlert(msg);
      showToast(msg);
      renderUI();
      return;
    }

    if (code === "text_pending") {
      state.enterLocked = false;
      setEnterFeedback(t("realtime.text.is.still.syncing.try.again.shortly"), 3200);
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const msg = t("realtime.text.is.still.syncing.try.again.shortly");
      showAlert(msg);
      showToast(msg);
      renderUI();
      return;
    }

    if (code === "invalid_hold") {
      state.enterLocked = false;
      setEnterFeedback(t("realtime.hold.to.send"), 2400);
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const msg = CODE_ERROR_MAP.invalid_hold;
      showAlert(msg);
      showToast(msg);
      renderUI();
      return;
    }

    if (code === "busy" || code === "enter_busy") {
      state.enterLocked = false;
      setEnterFeedback(t("realtime.virtual.enter.is.being.processed.please.wait"), 2800);
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const msg = t("realtime.virtual.enter.is.being.processed.please.wait");
      showAlert(msg);
      showToast(msg);
      renderUI();
      return;
    }

    if (status === 429 && code === "rate_limited") {
      // 依 retry_after_ms 鎖定冷卻，但絕不自動重送 Enter
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const retryAfter =
        data && Number.isFinite(Number(data.retry_after_ms))
          ? Math.max(100, Math.ceil(Number(data.retry_after_ms)))
          : 1000;
      state.enterLocked = true;
      setEnterFeedback(t("realtime.sending.too.quickly.try.again.shortly"), retryAfter);
      const msg = t("realtime.sending.too.quickly.please.wait");
      showAlert(msg);
      showToast(msg);
      clearTimeout(state.enterCooldownTimer);
      state.enterCooldownTimer = setTimeout(() => {
        state.enterCooldownTimer = null;
        state.enterLocked = false;
        setEnterFeedback(null, 0);
        renderUI();
      }, retryAfter);
      renderUI();
      return;
    }

    if (
      code === "sequence_conflict" ||
      code === "queue_full" ||
      code === "epoch_mismatch" ||
      code === "request_mismatch" ||
      code === "expired" ||
      code === "invalid_capability" ||
      code === "epoch_limit" ||
      code === "invalid_phase"
    ) {
      state.enterLocked = false;
      setEnterFeedback(null, 0);
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const nextPhase =
        code === "expired" || code === "invalid_capability" ? "REVOKED" : "PAUSED";
      const errMsg = translateCodeError(code, status, data);
      handleEpochEnded(nextPhase, code, errMsg);
      showAlert(errMsg);
      return;
    }

    state.enterLocked = false;
    setEnterFeedback(null, 0);
    const fallbackMsg = translateCodeError(code, status, data);
    showAlert(fallbackMsg);
    if (data && typeof data.phase === "string") {
      applySnapshot(data);
    } else {
      renderUI();
    }
  }

  function archiveCurrentSegment(reasonCode) {
    if (!ui || !ui.textarea) return;
    const text = state.lastValidText || ui.textarea.value || "";
    if (text.length > 0) {
      state.lockedSegments.unshift({
        epoch: state.activeSegmentEpoch || state.targetEpoch || t("realtime.segment"),
        text,
        appliedSeq: state.appliedSeq,
        acceptedSeq: state.acceptedSeq,
        reasonCode,
      });
      if (state.lockedSegments.length > 10) {
        state.lockedSegments.length = 10;
      }
    }
    ui.textarea.value = "";
    state.lastValidText = "";
    state.lastValidStart = 0;
    state.lastValidEnd = 0;
    state.lastValidDir = "none";
    state.lastQueuedText = "";
  }

  function clearEpochQueues() {
    if (state.rafId !== null) {
      cancelAnimationFrame(state.rafId);
      clearTimeout(state.rafTimer);
      state.rafTimer = null;
      state.rafId = null;
    }
    clearTimeout(state.sendScheduleTimer);
    state.sendScheduleTimer = null;
    clearTimeout(state.retryTimer);
    state.retryTimer = null;
    state.pendingCapture = false;
    state.stateHistory.clear();
    state.pendingQueue = [];
    state.inFlightBatch = null;
    state.retryBatch = null;
    state.retryCount = 0;
    state.nextSeq = 1;
    clearEnterState();
  }

  function resetForNewEpoch(newEpoch) {
    if (ui && ui.textarea && (ui.textarea.value.length > 0 || state.lastValidText.length > 0)) {
      archiveCurrentSegment(state.reason || "target_changed");
    }
    clearEpochQueues();
    state.activeSegmentEpoch = newEpoch;
    state.acceptedSeq = 0;
    state.appliedSeq = 0;
    state.expectedSeq = 1;
    state.seqStatus = null;
    state.proof = null;
    state.localNotice = null;
    state.enterSnapshot = null;
    if (ui && ui.textarea) {
      ui.textarea.value = "";
    }
    state.lastValidText = "";
    state.lastValidStart = 0;
    state.lastValidEnd = 0;
    state.lastValidDir = "none";
    state.lastQueuedText = "";
  }

  function formatLeaseCountdown(ms) {
    const clamped = Math.max(0, Math.ceil(ms / 1000));
    const mins = Math.floor(clamped / 60);
    const secs = clamped % 60;
    return `${String(mins).padStart(2, "0")}:${String(secs).padStart(2, "0")}`;
  }

  function getCalibratedLeaseRemainingMs() {
    if (state.leaseRemainingMs === null || !Number.isFinite(state.leaseRemainingMs)) {
      return null;
    }
    const elapsed = Math.max(0, nowPerf() - state.leaseSyncedAtPerf);
    return Math.max(0, Math.round(state.leaseRemainingMs - elapsed));
  }

  function isQueuePaused() {
    return state.pendingQueue.length > QUEUE_PAUSE_THRESHOLD;
  }

  function renderLockedSegments() {
    if (!ui || !ui.lockedSection || !ui.lockedList) return;
    ui.lockedList.replaceChildren();
    ui.lockedSection.hidden = state.lockedSegments.length === 0;
    if (state.lockedSegments.length === 0) return;

    state.lockedSegments.forEach((seg) => {
      const item = document.createElement("div");
      item.className = "rt-locked-item";

      const meta = document.createElement("div");
      meta.className = "rt-locked-meta";

      const badge = document.createElement("span");
      badge.className = "rt-locked-badge";
      window.I18n.bind(badge, "textContent", () => t("realtime.locked.applied", { value0: seg.appliedSeq, value1: seg.acceptedSeq }));

      const reasonSpan = document.createElement("span");
      window.I18n.bind(reasonSpan, "textContent", () => translateReason(seg.reasonCode) || t("realtime.segment.ended"));

      meta.append(badge, reasonSpan);

      const textP = document.createElement("p");
      textP.className = "rt-locked-text";
      textP.textContent = seg.text;

      item.append(meta, textP);
      ui.lockedList.append(item);
    });
  }

  function renderUI() {
    if (!ui) return;

    const phase = state.phase || "DISABLED";
    ui.phasePill.dataset.phase = phase;

    const leaseMs = getCalibratedLeaseRemainingMs();
    const showLease =
      leaseMs !== null &&
      leaseMs > 0 &&
      (phase === "ARMED" || phase === "BINDING" || phase === "ACTIVE" || phase === "PAUSED");

    if (showLease) {
      ui.leasePill.hidden = false;
      window.I18n.bind(ui.leasePill, "textContent", () => t("realtime.permission.time.remaining", { value0: formatLeaseCountdown(leaseMs) }));
      ui.leasePill.classList.toggle("is-low", leaseMs <= 30000);
    } else {
      ui.leasePill.hidden = true;
    }

    const reasonText = translateReason(state.reason);
    const queuePaused = isQueuePaused();

    ui.statusBox.classList.remove("is-active", "is-warning", "is-danger");
    ui.checkCodeWrap.hidden = true;

    let phaseLabel = t("realtime.standby");
    let titleText = t("realtime.ready.for.real.time.typing");
    let detailText = t("realtime.start.real.time.typing.then.check.the.code.and");
    let showStart = true;
    let startLabel = t("realtime.start.real.time.typing");
    let showRelease = false;
    let canEdit = false;

    if (!state.available) {
      phaseLabel = t("realtime.disabled");
      titleText = t("realtime.real.time.typing.is.not.enabled.on.your.computer");
      detailText = t("realtime.open.the.native.fastinput.app.on.your.computer.to");
      ui.statusBox.classList.add("is-warning");
      showStart = true;
      startLabel = t("realtime.start.again");
      showRelease = false;
    } else if (phase === "DISABLED") {
      phaseLabel = t("realtime.standby");
      if (state.localNotice) {
        titleText = state.localNotice;
        detailText = reasonText || t("realtime.choose.start.again.to.request.real.time.typing.permission");
        ui.statusBox.classList.add("is-warning");
        startLabel = t("realtime.start.again");
      }
      showStart = true;
      showRelease = false;
    } else if (phase === "REQUESTED") {
      phaseLabel = t("realtime.awaiting.approval");
      titleText = t("realtime.check.the.code.and.approve.on.your.computer");
      detailText = t("realtime.check.that.the.2.digit.code.on.your.computer");
      ui.statusBox.classList.add("is-warning");
      ui.checkCodeWrap.hidden = false;
      ui.checkCodeDigits.textContent = state.checkCode || "--";
      showStart = false;
      showRelease = true;
    } else if (phase === "ARMED" || phase === "BINDING") {
      phaseLabel = t("realtime.waiting.for.a.field");
      titleText = t("realtime.click.the.field.you.want.to.type.in.on");
      detailText =
        phase === "BINDING"
          ? t("realtime.field.detected.confirming.an.empty.starting.point")
          : t("realtime.left.click.the.target.field.on.your.computer.such");
      ui.statusBox.classList.add("is-warning");
      showStart = false;
      showRelease = true;
    } else if (phase === "ACTIVE") {
      phaseLabel = t("realtime.typing");
      ui.statusBox.classList.add("is-active");
      if (queuePaused) {
        titleText = t("realtime.sending.please.wait");
        detailText = t("realtime.the.sync.queue.is.busy.sending.updates.to.your");
      } else {
        titleText = t("realtime.typing.into.your.computer.in.real.time");
        detailText = t("realtime.type.or.edit.below.to.update.the.current.field");
      }
      showStart = false;
      showRelease = true;
      canEdit = !queuePaused;
    } else if (phase === "PAUSED") {
      phaseLabel = t("realtime.paused");
      ui.statusBox.classList.add("is-warning");
      const withinLease = leaseMs === null || leaseMs > 0;
      const hasUnconfirmed = state.seqStatus === "applying" || state.appliedSeq < state.acceptedSeq;
      if (state.reason === "enter_sent") {
        titleText = t("realtime.enter.sent.click.a.field.on.your.computer.to");
        if (withinLease && !hasUnconfirmed && capabilityToken) {
          detailText = t("realtime.enter.sent.select.a.field.on.your.computer.to");
          showStart = false;
          showRelease = true;
        } else {
          detailText = t("realtime.enter.sent.select.a.field.on.your.computer.or");
          showStart = true;
          startLabel = t("realtime.start.again");
          showRelease = Boolean(capabilityToken);
        }
      } else {
        titleText = reasonText ? t("realtime.paused.2", { value0: reasonText }) : t("realtime.real.time.typing.paused");
        if (withinLease && !hasUnconfirmed && capabilityToken) {
          detailText = t("realtime.select.a.field.on.your.computer.to.start.a");
          showStart = false;
          showRelease = true;
        } else {
          detailText = t("realtime.select.a.field.on.your.computer.or.choose.start");
          showStart = true;
          startLabel = t("realtime.start.again");
          showRelease = Boolean(capabilityToken);
        }
      }
    } else if (phase === "UNKNOWN") {
      phaseLabel = t("realtime.result.unknown");
      ui.statusBox.classList.add("is-danger");
      titleText = reasonText ? t("realtime.result.unknown.2", { value0: reasonText }) : t("realtime.cannot.confirm.whether.the.last.text.update.was.applied");
      detailText = t("realtime.automatic.retries.have.stopped.to.avoid.duplicate.input.or");
      showStart = true;
      startLabel = t("realtime.start.again");
      showRelease = Boolean(capabilityToken);
    } else if (phase === "REVOKED") {
      phaseLabel = t("app.ended");
      ui.statusBox.classList.add("is-warning");
      titleText = reasonText ? t("realtime.ended.2", { value0: reasonText }) : (state.localNotice || t("realtime.real.time.typing.permission.ended"));
      detailText = t("realtime.previous.text.is.locked.and.shown.in.gray.choose");
      showStart = true;
      startLabel = t("realtime.start.again");
      showRelease = false;
    }

    ui.phaseText.textContent = phaseLabel;
    ui.statusTitle.textContent = titleText;
    ui.statusDetail.textContent = detailText;
    ui.startButton.hidden = !showStart;
    ui.startButton.textContent = startLabel;
    ui.releaseButton.hidden = !showRelease;

    // 編輯區鎖定與灰化控制（絕不覆寫使用者在 ACTIVE 正在輸入的 textarea.value）
    const hasDraft = Boolean(ui.textarea && ui.textarea.value.length > 0);
    ui.textarea.readOnly = !canEdit;
    ui.textarea.disabled = !canEdit && !hasDraft;
    ui.inputWrap.classList.toggle("is-active", canEdit);
    ui.inputWrap.classList.toggle("is-locked", !canEdit);

    if (phase === "ACTIVE") {
      ui.textarea.placeholder = queuePaused
        ? t("realtime.sending.please.wait.2")
        : t("realtime.type.here.to.update.your.computer.instantly.single.line");
    } else if (phase === "ARMED" || phase === "BINDING") {
      window.I18n.bind(ui.textarea, "placeholder", () => t("realtime.first.click.the.target.field.on.your.computer"));
    } else if (phase === "PAUSED") {
      window.I18n.bind(ui.textarea, "placeholder", () => t("realtime.paused.select.a.field.on.your.computer.again"));
    } else {
      window.I18n.bind(ui.textarea, "placeholder", () => t("realtime.approve.and.select.a.field.on.your.computer.then"));
    }

    // 同步進度顯示（accepted / applied 或已同步）
    const totalQueued = state.nextSeq - 1;
    if (phase === "ACTIVE" || phase === "PAUSED" || phase === "UNKNOWN") {
      if (queuePaused) {
        window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.sending.received.applied", { value0: state.acceptedSeq, value1: state.appliedSeq }));
      } else if (state.acceptedSeq === 0 && totalQueued === 0) {
        ui.syncProgress.textContent =
          phase === "ACTIVE" ? t("realtime.synced.ready.for.input") : t("realtime.this.segment.is.locked.read.only");
      } else if (
        phase === "ACTIVE" &&
        state.appliedSeq === state.acceptedSeq &&
        state.acceptedSeq === totalQueued &&
        !state.inFlightBatch &&
        state.pendingQueue.length === 0
      ) {
        window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.synced.received.applied", { value0: state.acceptedSeq, value1: state.appliedSeq }));
      } else if (phase !== "ACTIVE") {
        window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.locked.received.applied", { value0: state.acceptedSeq, value1: state.appliedSeq }));
      } else {
        window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.syncing.received.applied", { value0: state.acceptedSeq, value1: state.appliedSeq }));
      }
    } else if (phase === "REQUESTED") {
      window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.waiting.for.approval.on.your.computer"));
    } else if (phase === "ARMED" || phase === "BINDING") {
      window.I18n.bind(ui.syncProgress, "textContent", () => t("realtime.waiting.for.a.target.field.on.your.computer"));
    } else {
      window.I18n.bind(ui.syncProgress, "textContent", () => hasDraft ? t("realtime.locked.read.only") : t("realtime.not.started"));
    }

    const currentText = ui.textarea ? ui.textarea.value : "";
    const utf16Len = currentText.length;
    const utf8Len = utf8ByteLength(currentText);
    window.I18n.bind(ui.charCounter, "textContent", () => t("realtime.characters.b", { value0: utf16Len, value1: MAX_UTF16_UNITS, value2: utf8Len, value3: MAX_UTF8_BYTES }));
    ui.charCounter.classList.toggle(
      "is-near-limit",
      utf16Len >= MAX_UTF16_UNITS * 0.85 || utf8Len >= MAX_UTF8_BYTES * 0.85
    );
    ui.charCounter.classList.toggle(
      "is-at-limit",
      utf16Len >= MAX_UTF16_UNITS || utf8Len >= MAX_UTF8_BYTES
    );

    renderEnterSection();
    renderLockedSegments();
  }

  function pruneAckedHistory(appliedSeq) {
    if (!Number.isFinite(appliedSeq) || appliedSeq <= 0) return;
    for (const key of state.stateHistory.keys()) {
      if (key <= appliedSeq) {
        state.stateHistory.delete(key);
      }
    }
  }

  function stopClaimPolling() {
    if (state.claimTimer !== null) {
      clearInterval(state.claimTimer);
      state.claimTimer = null;
    }
  }

  function stopHeartbeat() {
    if (state.heartbeatTimer !== null) {
      clearInterval(state.heartbeatTimer);
      state.heartbeatTimer = null;
    }
  }

  function startHeartbeat() {
    stopHeartbeat();
    if (!capabilityToken) return;
    sendHeartbeat(false);
    state.heartbeatTimer = setInterval(() => {
      if (!capabilityToken) {
        stopHeartbeat();
        return;
      }
      sendHeartbeat(false);
    }, HEARTBEAT_INTERVAL_MS);
  }

  function updateStatusPollSpeed() {
    if (!state.activeTab) {
      if (state.statusTimer !== null) {
        clearInterval(state.statusTimer);
        state.statusTimer = null;
      }
      return;
    }
    const wantFast =
      state.phase === "REQUESTED" ||
      state.phase === "ARMED" ||
      state.phase === "BINDING";
    if (state.statusTimer !== null && state.statusFastMode === wantFast) {
      return;
    }
    if (state.statusTimer !== null) {
      clearInterval(state.statusTimer);
    }
    state.statusFastMode = wantFast;
    const interval = wantFast ? STATUS_FAST_POLL_MS : STATUS_SLOW_POLL_MS;
    state.statusTimer = setInterval(() => {
      if (!document.hidden && state.activeTab) {
        pollStatus();
      }
    }, interval);
  }

  function handleEpochEnded(nextPhase, reasonCode, noticeText) {
    // 保留目前 textarea 內的已送出文字並轉為唯讀灰化，直到新 epoch 才歸檔至歷史清單並清空
    clearEpochQueues();
    state.phase = nextPhase;
    state.enterAvailable = false;
    if (reasonCode) state.reason = reasonCode;
    if (noticeText) state.localNotice = noticeText;
    if (nextPhase === "REVOKED" || nextPhase === "DISABLED") {
      capabilityToken = null;
      stopHeartbeat();
      stopClaimPolling();
    }
    updateStatusPollSpeed();
    renderUI();
  }

  function applySnapshot(snap) {
    if (!snap || typeof snap !== "object") return;

    // 若使用者剛取消了尚未 claim 的 request，忽略舊 request 的 REQUESTED 狀態快照
    if (
      state.cancelledRequestId &&
      snap.request_id === state.cancelledRequestId &&
      snap.phase === "REQUESTED"
    ) {
      return;
    }

    if (typeof snap.server_boot_id === "string" && snap.server_boot_id) {
      if (state.serverBootId && state.serverBootId !== snap.server_boot_id) {
        capabilityToken = null;
        stopHeartbeat();
        stopClaimPolling();
        archiveCurrentSegment("native_disconnected");
        clearEpochQueues();
        state.confirmedEpoch = null;
        state.confirmingEpoch = null;
        state.activeSegmentEpoch = null;
      }
      state.serverBootId = snap.server_boot_id;
    }

    if (typeof snap.available === "boolean") {
      state.available = snap.available;
    }
    if (typeof snap.request_id === "string" && snap.request_id) {
      state.requestId = snap.request_id;
    }
    if (typeof snap.session_id === "string" && snap.session_id) {
      state.sessionId = snap.session_id;
    }
    if (typeof snap.check_code === "string" && snap.check_code) {
      state.checkCode = snap.check_code;
    }

    const prevPhase = state.phase;
    const nextPhase = typeof snap.phase === "string" ? snap.phase : state.phase;
    const nextEpoch =
      snap.target_epoch !== undefined ? snap.target_epoch : state.targetEpoch;

    if (Number.isFinite(Number(snap.accepted_seq))) {
      state.acceptedSeq = Number(snap.accepted_seq);
    }
    if (Number.isFinite(Number(snap.applied_seq))) {
      state.appliedSeq = Number(snap.applied_seq);
      pruneAckedHistory(state.appliedSeq);
    }
    if (Number.isFinite(Number(snap.expected_seq))) {
      state.expectedSeq = Number(snap.expected_seq);
    }
    if (snap.seq_status !== undefined) {
      state.seqStatus = snap.seq_status;
    }
    if (snap.proof !== undefined) {
      state.proof = snap.proof;
    }
    if (snap.reason !== undefined) {
      state.reason = snap.reason;
    }
    if (snap.lease_remaining_ms !== undefined) {
      if (snap.lease_remaining_ms === null) {
        state.leaseRemainingMs = null;
      } else if (Number.isFinite(Number(snap.lease_remaining_ms))) {
        state.leaseRemainingMs = Number(snap.lease_remaining_ms);
        state.leaseSyncedAtPerf = nowPerf();
      }
    }

    // M4 虛擬 Enter 快照欄位（缺省視為 false／空）
    state.enterAvailable = snap.enter_available === true;
    state.targetApp =
      typeof snap.target_app === "string" && snap.target_app.trim().length > 0
        ? snap.target_app.trim().slice(0, 32)
        : null;
    if (
      snap.enter &&
      typeof snap.enter === "object" &&
      typeof snap.enter.enter_id === "string" &&
      snap.enter.enter_id
    ) {
      state.enterSnapshot = {
        enter_id: snap.enter.enter_id,
        status: typeof snap.enter.status === "string" ? snap.enter.status : null,
      };
    } else {
      state.enterSnapshot = null;
    }

    state.phase = nextPhase;
    state.targetEpoch = nextEpoch;

    // 偵測到新 epoch：將舊段落歸檔鎖定、建立全新空白輸入段落並送 confirm_empty
    if (
      nextEpoch &&
      typeof nextEpoch === "string" &&
      (nextPhase === "BINDING" || nextPhase === "ACTIVE")
    ) {
      if (state.activeSegmentEpoch !== nextEpoch) {
        resetForNewEpoch(nextEpoch);
      }
      if (
        nextPhase === "BINDING" &&
        state.confirmedEpoch !== nextEpoch &&
        state.confirmingEpoch !== nextEpoch &&
        capabilityToken
      ) {
        sendConfirmEmpty(nextEpoch);
      }
      if (nextPhase === "ACTIVE") {
        state.confirmedEpoch = nextEpoch;
        state.confirmingEpoch = null;
      }
    }

    // 進入暫停／結果不明／撤銷時：停止當前傳送佇列並將已送文字鎖定唯讀（不重送舊文字）
    if (
      nextPhase === "PAUSED" ||
      nextPhase === "UNKNOWN" ||
      nextPhase === "REVOKED" ||
      (nextPhase === "DISABLED" && prevPhase !== "DISABLED")
    ) {
      clearEpochQueues();
      if (
        nextPhase === "PAUSED" &&
        state.reason === "enter_sent" &&
        prevPhase !== "PAUSED"
      ) {
        const enterSentMsg = t("realtime.enter.sent.click.a.field.on.your.computer.to");
        if (ui && ui.liveStatus) {
          ui.liveStatus.textContent = enterSentMsg;
        }
      }
    } else if (prevPhase !== nextPhase) {
      state.enterLocked = false;
    }

    if (nextPhase === "REVOKED" || (nextPhase === "DISABLED" && prevPhase !== "REQUESTED")) {
      capabilityToken = null;
      stopHeartbeat();
      stopClaimPolling();
    }

    updateStatusPollSpeed();
    renderUI();

    if (state.phase === "ACTIVE" && state.pendingQueue.length > 0 && !state.inFlightBatch) {
      schedulePumpQueue(0);
    }
  }

  async function apiPost(path, bodyObj, withCapability = false, keepalive = false) {
    const headers = {
      Accept: "application/json",
      "Content-Type": "application/json",
    };
    if (withCapability && capabilityToken) {
      headers["X-FI-Realtime"] = capabilityToken;
    }
    const bodyStr = JSON.stringify(bodyObj);
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 6000);
    try {
      const response = await fetch(path, {
        method: "POST",
        cache: "no-store",
        credentials: "same-origin",
        headers,
        body: bodyStr,
        keepalive,
        signal: controller.signal,
      });
      let data = null;
      try {
        data = await response.json();
      } catch (_) {
        data = null;
      }
      return { ok: response.ok, status: response.status, data, networkError: false };
    } catch (_) {
      return { ok: false, status: 0, data: null, networkError: true };
    } finally {
      clearTimeout(timeout);
    }
  }

  async function pollStatus() {
    if (state.statusInFlight) return;
    state.statusInFlight = true;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetch("/api/realtime/status", {
        method: "GET",
        cache: "no-store",
        credentials: "same-origin",
        headers: { Accept: "application/json" },
        signal: controller.signal,
      });
      let data = null;
      try {
        data = await response.json();
      } catch (_) {
        data = null;
      }
      if (response.ok && data) {
        applySnapshot(data);
      } else if (response.status === 401) {
        handleEpochEnded("DISABLED", "credential_revoked", t("realtime.phone.pairing.has.expired.pair.your.phone.first"));
      }
    } catch (_) {
      // 網路異常時不另開新 epoch，等待下次輪詢
    } finally {
      clearTimeout(timeout);
      state.statusInFlight = false;
    }
  }

  async function requestStart() {
    hideAlert();
    stopClaimPolling();
    stopHeartbeat();

    // 若先前仍有尚未釋放的 capability（如 PAUSED 或 UNKNOWN 狀態下按「重新開始」），先主動 release
    if (capabilityToken) {
      await releaseSession(false);
    }

    if (ui && ui.textarea && (ui.textarea.value.length > 0 || state.lastValidText.length > 0)) {
      archiveCurrentSegment(state.reason || "released");
    }

    clearEpochQueues();
    capabilityToken = null;
    state.cancelledRequestId = null;
    state.confirmedEpoch = null;
    state.confirmingEpoch = null;
    state.activeSegmentEpoch = null;
    state.targetEpoch = null;
    state.reason = null;
    state.localNotice = null;
    state.checkCode = null;
    state.enterAvailable = false;
    state.targetApp = null;
    state.enterSnapshot = null;

    state.clientNonce = generateClientNonce();
    state.requestStartedAt = Date.now();

    const { ok, status, data, networkError } = await apiPost(
      "/api/realtime/request",
      { v: 1, client_nonce: state.clientNonce },
      false,
      false
    );

    if (networkError) {
      showAlert(t("realtime.cannot.reach.your.computer.check.your.local.network"));
      renderUI();
      return;
    }

    if (!ok || !data) {
      const code = data && data.code;
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const msg = translateCodeError(code, status, data);
      showAlert(msg);
      showToast(msg);
      renderUI();
      return;
    }

    applySnapshot(data);
    startClaimPolling();
  }

  function startClaimPolling() {
    stopClaimPolling();
    state.claimTimer = setInterval(() => {
      attemptClaim();
    }, CLAIM_POLL_MS);
  }

  async function attemptClaim() {
    if (state.phase !== "REQUESTED" && state.phase !== "ARMED") {
      stopClaimPolling();
      return;
    }
    if (!state.requestId || !state.clientNonce) {
      return;
    }
    if (Date.now() - state.requestStartedAt > CLAIM_TIMEOUT_MS) {
      stopClaimPolling();
      state.cancelledRequestId = state.requestId;
      handleEpochEnded("DISABLED", "request_expired", t("realtime.not.approved.on.your.computer"));
      showToast(t("realtime.not.approved.on.your.computer"));
      return;
    }

    const { ok, status, data, networkError } = await apiPost(
      "/api/realtime/claim",
      {
        v: 1,
        request_id: state.requestId,
        client_nonce: state.clientNonce,
      },
      false,
      false
    );

    if (networkError) {
      return;
    }

    if (ok && data && typeof data.capability === "string" && data.capability) {
      capabilityToken = data.capability;
      stopClaimPolling();
      // 協定 §2.2：核准後只有約 3 秒的 heartbeat 期限，取得 capability 後立即啟動每秒一次 heartbeat
      applySnapshot(data);
      startHeartbeat();
      return;
    }

    const code = data && data.code;
    if (status === 409 && code === "approval_pending") {
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      return;
    }

    if (status === 410 || code === "expired" || code === "no_request") {
      stopClaimPolling();
      state.cancelledRequestId = state.requestId;
      handleEpochEnded("DISABLED", "request_expired", t("realtime.not.approved.on.your.computer"));
      showToast(t("realtime.not.approved.on.your.computer"));
      return;
    }

    if (status === 403 || code === "invalid_claim") {
      stopClaimPolling();
      handleEpochEnded("DISABLED", "protocol_error", t("realtime.could.not.claim.permission.start.again"));
      return;
    }
  }

  async function sendConfirmEmpty(epochToConfirm) {
    if (!capabilityToken || !state.requestId || !epochToConfirm) return;
    state.confirmingEpoch = epochToConfirm;

    const { ok, status, data } = await apiPost(
      "/api/realtime/heartbeat",
      {
        v: 1,
        request_id: state.requestId,
        target_epoch: epochToConfirm,
        confirm_empty: true,
      },
      true,
      false
    );

    if (ok && data) {
      state.confirmedEpoch = epochToConfirm;
      state.confirmingEpoch = null;
      applySnapshot(data);
      return;
    }

    state.confirmingEpoch = null;
    const code = data && data.code;
    if (
      status === 403 ||
      status === 410 ||
      code === "invalid_capability" ||
      code === "expired" ||
      code === "request_mismatch" ||
      code === "epoch_mismatch"
    ) {
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      } else {
        handleEpochEnded("REVOKED", code || "expired", translateCodeError(code, status, data));
      }
    }
  }

  async function sendHeartbeat(isConfirmEmpty = false) {
    if (!capabilityToken || !state.requestId || state.heartbeatInFlight) return;
    if (isConfirmEmpty && state.targetEpoch) {
      await sendConfirmEmpty(state.targetEpoch);
      return;
    }

    state.heartbeatInFlight = true;
    try {
      const payload = {
        v: 1,
        request_id: state.requestId,
      };
      if (
        state.targetEpoch &&
        (state.phase === "BINDING" || state.phase === "ACTIVE")
      ) {
        payload.target_epoch = state.targetEpoch;
      }

      const { ok, status, data } = await apiPost(
        "/api/realtime/heartbeat",
        payload,
        true,
        false
      );

      if (ok && data) {
        applySnapshot(data);
        return;
      }

      const code = data && data.code;
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      } else if (
        status === 403 ||
        status === 410 ||
        code === "invalid_capability" ||
        code === "expired"
      ) {
        handleEpochEnded("REVOKED", code || "expired", translateCodeError(code, status, data));
      }
    } finally {
      state.heartbeatInFlight = false;
    }
  }

  async function releaseSession(useKeepalive = false) {
    stopClaimPolling();
    stopHeartbeat();
    const token = capabilityToken;
    const reqId = state.requestId;
    capabilityToken = null;
    if (reqId) {
      state.cancelledRequestId = reqId;
    }

    clearEpochQueues();

    if (token) {
      const headers = {
        Accept: "application/json",
        "Content-Type": "application/json",
        "X-FI-Realtime": token,
      };
      const body = JSON.stringify(reqId ? { v: 1, request_id: reqId } : { v: 1 });
      try {
        const res = await fetch("/api/realtime/release", {
          method: "POST",
          cache: "no-store",
          credentials: "same-origin",
          headers,
          body,
          keepalive: Boolean(useKeepalive),
        });
        if (!useKeepalive) {
          let data = null;
          try {
            data = await res.json();
          } catch (_) {
            data = null;
          }
          if (data && typeof data.phase === "string") {
            applySnapshot(data);
            return;
          }
        }
      } catch (_) {
        // Ignore network error on release
      }
    }

    state.phase = token ? "REVOKED" : "DISABLED";
    state.targetEpoch = null;
    state.confirmedEpoch = null;
    state.confirmingEpoch = null;
    state.activeSegmentEpoch = null;
    state.enterAvailable = false;
    state.targetApp = null;
    state.enterSnapshot = null;
    state.reason = "released";
    state.localNotice = t("realtime.real.time.typing.ended");
    updateStatusPollSpeed();
    renderUI();
  }

  function handleBeforeInput(event) {
    if (!ui || !ui.textarea) return;
    const inputType = typeof event.inputType === "string" ? event.inputType : null;
    state.lastInputType = inputType;

    // 規格 §4／§12：文字區內的換行／Enter 一律攔截，絕不觸發虛擬 Enter
    if (inputType === "insertLineBreak" || inputType === "insertParagraph") {
      event.preventDefault();
      showAlert(t("realtime.line.breaks.are.not.supported.in.real.time.typing"));
      showToast(t("realtime.line.breaks.are.not.supported.in.real.time.typing"));
      return;
    }

    if (state.phase !== "ACTIVE" || isQueuePaused()) {
      event.preventDefault();
      if (isQueuePaused()) {
        showAlert(t("realtime.sending.please.wait"));
      }
      return;
    }
  }

  function handleKeyDown(event) {
    if (event.key === "Enter") {
      event.preventDefault();
      showAlert(t("realtime.line.breaks.are.not.supported.in.real.time.typing"));
      showToast(t("realtime.line.breaks.are.not.supported.in.real.time.typing"));
      return;
    }
    if (event.key === "Tab") {
      event.preventDefault();
      showAlert(t("realtime.tab.is.not.supported.in.real.time.typing"));
      showToast(t("realtime.tab.is.not.supported.in.real.time.typing"));
    }
  }

  function restoreLastValidEditorState() {
    if (!ui || !ui.textarea) return;
    ui.textarea.value = state.lastValidText;
    try {
      ui.textarea.setSelectionRange(
        state.lastValidStart,
        state.lastValidEnd,
        state.lastValidDir
      );
    } catch (_) {
      // Ignore selection error when not focused
    }
  }

  function captureEditorChange() {
    if (!ui || !ui.textarea) return;
    if (state.phase !== "ACTIVE" || !state.targetEpoch) return;

    const rawText = ui.textarea.value;
    const check = validateRealtimeText(rawText);
    if (!check.valid) {
      restoreLastValidEditorState();
      showAlert(check.reason);
      showToast(check.reason);
      renderUI();
      return;
    }

    const selStart = clampSelectionToSurrogateBoundary(rawText, ui.textarea.selectionStart);
    const selEnd = Math.max(
      selStart,
      clampSelectionToSurrogateBoundary(rawText, ui.textarea.selectionEnd)
    );
    const selDir = normalizeDirection(ui.textarea.selectionDirection);

    state.lastValidText = rawText;
    state.lastValidStart = selStart;
    state.lastValidEnd = selEnd;
    state.lastValidDir = selDir;

    // 協定 §4：純 selection 變動（只移動游標）不配發新 seq；只有文字真的變了才送新狀態
    if (rawText === state.lastQueuedText) {
      renderUI();
      return;
    }

    // 有新文字輸入時立即取消任何進行中的 Enter 長按並隱藏按鈕
    if (state.enterHolding) {
      cancelEnterHold(false);
    }

    hideAlert();
    scheduleCaptureFrame();
    renderUI();
  }

  function scheduleCaptureFrame() {
    state.pendingCapture = true;
    if (state.rafId !== null) return;
    // rAF 在背景／被節流的頁面可能不觸發，因此同時設 40 ms 計時器備援，先到者執行
    const flush = () => {
      if (state.rafId === null) return;
      cancelAnimationFrame(state.rafId);
      clearTimeout(state.rafTimer);
      state.rafId = null;
      state.rafTimer = null;
      if (!state.pendingCapture) return;
      state.pendingCapture = false;
      enqueueCapturedState();
    };
    state.rafId = requestAnimationFrame(flush);
    state.rafTimer = setTimeout(flush, 40);
  }

  function enqueueCapturedState() {
    if (!ui || !ui.textarea) return;
    if (state.phase !== "ACTIVE" || !state.targetEpoch) return;
    if (state.isComposing) return;

    const text = state.lastValidText;
    if (text === state.lastQueuedText) return;

    if (state.nextSeq > MAX_EPOCH_SEQ) {
      handleEpochEnded("PAUSED", "epoch_limit", CODE_ERROR_MAP.epoch_limit);
      showAlert(CODE_ERROR_MAP.epoch_limit);
      return;
    }

    const seq = state.nextSeq;
    state.nextSeq += 1;
    state.lastQueuedText = text;

    const item = {
      seq,
      prev_seq: seq - 1,
      text,
      selection_start: state.lastValidStart,
      selection_end: state.lastValidEnd,
      selection_direction: state.lastValidDir,
      composition: "committed",
      client_elapsed_ms: elapsedMs(),
    };

    if (state.lastInputType && INPUT_KIND_WHITELIST.has(state.lastInputType)) {
      item.input_kind = state.lastInputType;
    }
    state.lastInputType = null;

    state.stateHistory.set(seq, item);
    state.pendingQueue.push(item);

    renderUI();
    schedulePumpQueue();
  }

  function schedulePumpQueue(customDelayMs) {
    if (state.inFlightBatch || (state.pendingQueue.length === 0 && !state.retryBatch)) return;
    if (state.phase !== "ACTIVE" || !state.targetEpoch || !capabilityToken) return;

    clearTimeout(state.sendScheduleTimer);
    let waitMs = 0;
    if (typeof customDelayMs === "number") {
      waitMs = Math.max(0, Math.ceil(customDelayMs));
    } else {
      const sinceLast = nowPerf() - state.lastSendPerf;
      waitMs = sinceLast >= MIN_SEND_INTERVAL_MS ? 0 : Math.ceil(MIN_SEND_INTERVAL_MS - sinceLast);
    }

    if (waitMs <= 0) {
      pumpStatesQueue();
    } else {
      state.sendScheduleTimer = setTimeout(() => {
        state.sendScheduleTimer = null;
        pumpStatesQueue();
      }, waitMs);
    }
  }

  function buildNextBatch() {
    if (state.retryBatch && state.retryBatch.length > 0) {
      return state.retryBatch.slice();
    }
    const batch = [];
    for (let i = 0; i < state.pendingQueue.length && batch.length < MAX_BATCH_SIZE; i++) {
      const candidate = state.pendingQueue[i];
      if (batch.length > 0 && candidate.seq !== batch[batch.length - 1].seq + 1) {
        break;
      }
      const testBatch = batch.concat(candidate);
      const testBody = JSON.stringify({
        v: 1,
        request_id: state.requestId,
        target_epoch: state.targetEpoch,
        states: testBatch,
      });
      if (utf8ByteLength(testBody) > MAX_BODY_BYTES && batch.length > 0) {
        break;
      }
      batch.push(candidate);
    }
    return batch;
  }

  function rebuildPendingFromExpectedSeq(expectedSeq) {
    const startSeq = Math.max(1, Number(expectedSeq) || 1);
    const rebuilt = [];
    for (let s = startSeq; s < state.nextSeq; s++) {
      const saved = state.stateHistory.get(s);
      if (!saved) {
        handleEpochEnded("PAUSED", "sequence_conflict", t("realtime.cannot.recover.missing.text.updates.this.segment.is.paused"));
        return false;
      }
      rebuilt.push(saved);
    }
    state.retryBatch = null;
    state.pendingQueue = rebuilt;
    return true;
  }

  async function pumpStatesQueue() {
    if (state.inFlightBatch || (state.pendingQueue.length === 0 && !state.retryBatch)) return;
    if (state.phase !== "ACTIVE" || !state.targetEpoch || !capabilityToken || !state.requestId) return;

    const batchEpoch = state.targetEpoch;
    const batchRequestId = state.requestId;
    const batch = buildNextBatch();
    if (batch.length === 0) return;

    state.inFlightBatch = batch;
    state.lastSendPerf = nowPerf();
    renderUI();

    const { ok, status, data, networkError } = await apiPost(
      "/api/realtime/states",
      {
        v: 1,
        request_id: batchRequestId,
        target_epoch: batchEpoch,
        states: batch,
      },
      true,
      false
    );

    if (state.targetEpoch !== batchEpoch || state.requestId !== batchRequestId) {
      state.inFlightBatch = null;
      return;
    }

    if (networkError || status === 408 || status === 0) {
      // 網路逾時／結果不明：固定原批次（同序號同內容）原樣重送，絕不修改內容或混入新狀態
      state.retryBatch = batch;
      state.inFlightBatch = null;
      state.retryCount += 1;
      const delay = Math.min(1000, 120 * state.retryCount);
      showAlert(t("realtime.network.timeout.resending.unchanged.with.the.same.sequence.number"));
      state.retryTimer = setTimeout(() => {
        state.retryTimer = null;
        schedulePumpQueue(0);
      }, delay);
      renderUI();
      return;
    }

    state.retryCount = 0;

    if (ok && data) {
      const lastSentSeq = batch[batch.length - 1].seq;
      const acceptedSeq = Number.isFinite(Number(data.accepted_seq))
        ? Math.max(lastSentSeq, Number(data.accepted_seq))
        : lastSentSeq;
      state.retryBatch = null;
      state.pendingQueue = state.pendingQueue.filter((item) => item.seq > acceptedSeq);
      state.inFlightBatch = null;
      applySnapshot(data);
      if (state.phase === "ACTIVE" && state.pendingQueue.length > 0) {
        schedulePumpQueue();
      }
      return;
    }

    const code = data && data.code;
    state.inFlightBatch = null;

    if (status === 429 && code === "rate_limited") {
      // 依 retry_after_ms 等待後原樣重送同一批
      state.retryBatch = batch;
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const retryAfter =
        data && Number.isFinite(Number(data.retry_after_ms))
          ? Math.max(34, Math.ceil(Number(data.retry_after_ms)))
          : 50;
      state.retryTimer = setTimeout(() => {
        state.retryTimer = null;
        schedulePumpQueue(0);
      }, retryAfter);
      renderUI();
      return;
    }

    if (status === 409 && code === "sequence_gap") {
      // 依 expected_seq 補送原本的那些狀態
      const expSeq =
        data && Number.isFinite(Number(data.expected_seq))
          ? Number(data.expected_seq)
          : state.acceptedSeq + 1;
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      if (rebuildPendingFromExpectedSeq(expSeq)) {
        schedulePumpQueue(0);
      }
      return;
    }

    // 視為本 epoch 結束的錯誤碼
    if (
      code === "sequence_conflict" ||
      code === "queue_full" ||
      code === "epoch_mismatch" ||
      code === "request_mismatch" ||
      code === "expired" ||
      code === "invalid_capability" ||
      code === "epoch_limit" ||
      code === "invalid_phase"
    ) {
      if (data && typeof data.phase === "string") {
        applySnapshot(data);
      }
      const nextPhase =
        code === "expired" || code === "invalid_capability" ? "REVOKED" : "PAUSED";
      handleEpochEnded(nextPhase, code, translateCodeError(code, status, data));
      showAlert(translateCodeError(code, status, data));
      return;
    }

    const fallbackMsg = translateCodeError(code, status, data);
    showAlert(fallbackMsg);
    if (data && typeof data.phase === "string") {
      applySnapshot(data);
    } else {
      handleEpochEnded("PAUSED", "protocol_error", fallbackMsg);
    }
  }

  function handleSSEMessage(event) {
    if (!event || typeof event.data !== "string") return;
    let payload = null;
    try {
      payload = JSON.parse(event.data);
    } catch (_) {
      return;
    }
    if (!payload || typeof payload !== "object") return;

    // 協定 §5：reset_required / resync_required 時重新 GET status，不重播舊文字
    if (payload.reset_required === true || payload.code === "resync_required") {
      pollStatus();
      return;
    }

    applySnapshot(payload);
  }

  function ensureEventSource() {
    if (!state.activeTab) return;
    if (state.eventSource) {
      if (state.eventSource.readyState !== EventSource.CLOSED) {
        return;
      }
      closeEventSource();
    }
    if (typeof EventSource !== "function") return;
    try {
      const es = new EventSource("/api/events");
      // 只註冊 'realtime' 具名事件，絕不處理未具名的 data: 記事本事件
      es.addEventListener("realtime", handleSSEMessage);
      es.onerror = () => {
        // 連線異常時由 GET /api/realtime/status 取得權威狀態，不另開 epoch
      };
      state.eventSource = es;
    } catch (_) {
      state.eventSource = null;
    }
  }

  function closeEventSource() {
    if (state.eventSource) {
      try {
        state.eventSource.removeEventListener("realtime", handleSSEMessage);
        state.eventSource.close();
      } catch (_) {}
      state.eventSource = null;
    }
  }

  function startLeaseTicker() {
    if (state.leaseTimer !== null) return;
    state.leaseTimer = setInterval(() => {
      if (!state.activeTab || document.hidden) return;
      const rem = getCalibratedLeaseRemainingMs();
      if (rem !== null) {
        renderUI();
      }
    }, LEASE_TICK_MS);
  }

  function stopLeaseTicker() {
    if (state.leaseTimer !== null) {
      clearInterval(state.leaseTimer);
      state.leaseTimer = null;
    }
  }

  function activate() {
    if (!state.initialized) {
      init();
    }
    if (!state.initialized) return;
    state.activeTab = true;
    ensureEventSource();
    startLeaseTicker();
    updateStatusPollSpeed();
    pollStatus();
    renderUI();
  }

  function deactivate() {
    if (!state.initialized) return;
    state.activeTab = false;
    stopClaimPolling();
    stopHeartbeat();
    stopLeaseTicker();
    clearEnterState();
    if (state.statusTimer !== null) {
      clearInterval(state.statusTimer);
      state.statusTimer = null;
    }
    closeEventSource();
    if (
      capabilityToken ||
      state.phase === "REQUESTED" ||
      state.phase === "ARMED" ||
      state.phase === "BINDING" ||
      state.phase === "ACTIVE" ||
      state.phase === "PAUSED"
    ) {
      releaseSession(true);
    } else {
      clearEpochQueues();
      renderUI();
    }
  }

  function isActive() {
    return Boolean(state.activeTab);
  }

  function bindEvents() {
    if (!ui) return;

    ui.startButton.addEventListener("click", () => {
      if (!state.activeTab) {
        activate();
      }
      requestStart();
    });

    ui.releaseButton.addEventListener("click", () => {
      releaseSession(false);
    });

    ui.alertClose.addEventListener("click", () => {
      hideAlert();
    });

    ui.lockedClearButton.addEventListener("click", () => {
      state.lockedSegments = [];
      renderLockedSegments();
    });

    ui.textarea.addEventListener("beforeinput", handleBeforeInput);
    ui.textarea.addEventListener("keydown", handleKeyDown);

    ui.textarea.addEventListener("compositionstart", () => {
      state.isComposing = true;
      if (state.enterHolding) {
        cancelEnterHold(false);
      }
      renderEnterSection();
    });

    ui.textarea.addEventListener("compositionend", () => {
      state.isComposing = false;
      captureEditorChange();
    });

    ui.textarea.addEventListener("input", (event) => {
      if (state.isComposing || event.isComposing) {
        return;
      }
      captureEditorChange();
    });

    if (ui.enterButton) {
      // 攔截鍵盤與合成 click、禁用長按選單，僅允許透過 Pointer Events 長按滿 600ms 觸發
      ui.enterButton.addEventListener("keydown", (event) => {
        event.preventDefault();
        event.stopPropagation();
      });
      ui.enterButton.addEventListener("keyup", (event) => {
        event.preventDefault();
        event.stopPropagation();
      });
      ui.enterButton.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
      });
      ui.enterButton.addEventListener("contextmenu", (event) => {
        event.preventDefault();
      });
      ui.enterButton.addEventListener("pointerdown", handleEnterPointerDown);
      ui.enterButton.addEventListener("pointerup", handleEnterPointerEnd);
      ui.enterButton.addEventListener("pointercancel", handleEnterPointerEnd);
      ui.enterButton.addEventListener("pointerleave", handleEnterPointerEnd);
    }

    window.addEventListener("pagehide", () => {
      if (state.enterHolding) {
        cancelEnterHold(false);
      }
      if (capabilityToken) {
        releaseSession(true);
      }
    });

    window.addEventListener("blur", () => {
      if (state.enterHolding) {
        cancelEnterHold(false);
      }
    });

    document.addEventListener("visibilitychange", () => {
      if (document.hidden && state.enterHolding) {
        cancelEnterHold(false);
      }
      if (!document.hidden && state.activeTab) {
        ensureEventSource();
        pollStatus();
      }
    });

    window.addEventListener("pageshow", () => {
      if (!document.hidden && state.activeTab) {
        ensureEventSource();
        pollStatus();
      }
    });

    window.addEventListener("focus", () => {
      if (!document.hidden && state.activeTab) {
        pollStatus();
      }
    });
  }

  function init() {
    if (state.initialized) return;
    const card = byId("realtimeCard");
    if (!card) return;

    state.initialized = true;
    ui = ensureCardStructure(card);
    bindEvents();
    renderUI();

    if (!card.hidden) {
      activate();
    }
  }

  window.FastInputRealtime = {
    activate,
    deactivate,
    isActive,
    requestStart,
    release: () => releaseSession(false),
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
  document.addEventListener("languagechange", () => {
    state.localNotice = window.I18n.localize(state.localNotice);
    state.enterFeedbackMsg = window.I18n.localize(state.enterFeedbackMsg);
    if (state.initialized) { renderLockedSegments(); renderUI(); }
  });
})();
