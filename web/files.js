(() => {
  "use strict";

  const { t } = window.I18n;

  const POLL_INTERVAL_MS = 10000;
  const EXPIRY_TICK_MS = 60000;
  const MAX_CONCURRENT_UPLOADS = 2;
  const VALID_ID_RE = /^[0-9a-f]{16}$/;

  const state = {
    initialized: false,
    loaded: false,
    loading: false,
    files: [],
    usedBytes: 0,
    maxFileBytes: null,
    maxTotalBytes: null,
    maxFiles: null,
    ttlSeconds: null,
    revision: null,
    uploads: [],
    nextUploadUid: 1,
    confirmingDeleteId: null,
    deletingIds: new Set(),
    pollTimer: null,
    expiryTimer: null,
    toastTimer: null,
    fetchPromise: null,
  };

  let ui = null;

  function byId(id) {
    return document.getElementById(id);
  }

  function formatBytes(bytes) {
    return window.I18n.bytes(bytes);
  }

  function truncateMiddle(name, maxChars = 32) {
    const str = String(name || "");
    const chars = Array.from(str);
    if (chars.length <= maxChars) return str;
    const dotIndex = str.lastIndexOf(".");
    let extChars = [];
    let baseChars = chars;
    if (dotIndex > 0 && dotIndex < str.length - 1) {
      const extStr = str.slice(dotIndex);
      const candidateExt = Array.from(extStr);
      if (candidateExt.length <= 10) {
        extChars = candidateExt;
        baseChars = Array.from(str.slice(0, dotIndex));
      }
    }
    const available = Math.max(6, maxChars - extChars.length - 1);
    const headLen = Math.ceil(available * 0.65);
    const tailLen = Math.max(2, available - headLen);
    const head = baseChars.slice(0, headLen).join("");
    const tail = baseChars.slice(baseChars.length - tailLen).join("");
    return `${head}…${tail}${extChars.join("")}`;
  }

  function formatRemainingTime(expiresAtSec, nowSec = Math.floor(Date.now() / 1000)) {
    const exp = Number(expiresAtSec);
    if (!Number.isFinite(exp)) return t("files.stored");
    const diff = exp - nowSec;
    if (diff <= 0) return t("files.expired");
    if (diff < 60) return t("files.less.than.a.minute.left");
    if (diff < 3600) {
      const mins = Math.ceil(diff / 60);
      return t("common.minute", { count: mins });
    }
    const hours = Math.ceil(diff / 3600);
    return t("common.hour", { count: hours });
  }

  function mapErrorMessage(status, code, serverError) {
    const localized = window.I18n.errorMessage({ code, message: serverError });
    if (localized) return localized;
    if (code === "file_too_large" || status === 413) {
      return t("files.file.exceeds.the.per.file.size.limit");
    }
    if (code === "quota_exceeded" || status === 507) {
      return t("files.temporary.storage.is.full");
    }
    if (code === "too_many_files" || status === 409) {
      return t("files.temporary.storage.has.reached.its.file.limit");
    }
    if (code === "busy" || status === 503) {
      return t("files.too.many.uploads.in.progress.try.again.later");
    }
    if (code === "not_paired" || status === 401) {
      return t("files.pairing.has.expired.pair.again.and.retry");
    }
    if (code === "origin_rejected" || status === 403) {
      return t("files.origin.verification.failed");
    }
    if (code === "bad_request" || status === 400) {
      return serverError || t("files.invalid.request.format");
    }
    if (typeof serverError === "string" && serverError.trim()) {
      return serverError.trim();
    }
    if (status === 0) {
      return t("files.connection.interrupted.check.your.local.network");
    }
    return t("files.action.failed.error", { value0: status || window.I18n.ref(t("common.unknown")) });
  }

  function announceStatus(message) {
    if (ui && ui.liveStatus && message) {
      window.I18n.bind(ui.liveStatus, "textContent", () => message);
    }
  }

  function showToast(message, duration = 3500) {
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

  function showAlert(message, isError = true) {
    if (!ui || !ui.alert || !ui.alertText) return;
    window.I18n.bind(ui.alertText, "textContent", () => message);
    ui.alert.classList.toggle("is-error", Boolean(isError));
    ui.alert.hidden = false;
    announceStatus(message);
  }

  function hideAlert() {
    if (!ui || !ui.alert || !ui.alertText) return;
    ui.alert.hidden = true;
    ui.alertText.textContent = "";
  }

  function ensureCardStructure(card) {
    if (byId("filesList") && byId("filesInput") && byId("filesDropzone")) {
      return {
        card,
        countPill: byId("filesCount"),
        refreshButton: byId("filesRefreshButton"),
        quotaBar: byId("filesQuotaBar"),
        quotaFill: byId("filesQuotaFill"),
        quotaText: byId("filesQuotaText"),
        quotaCountText: byId("filesQuotaCountText"),
        dropzone: byId("filesDropzone"),
        dropzoneHint: byId("filesDropzoneHint"),
        selectButton: byId("filesSelectButton"),
        fileInput: byId("filesInput"),
        alert: byId("filesAlert"),
        alertText: byId("filesAlertText"),
        alertClose: byId("filesAlertClose"),
        uploadsList: byId("filesUploadsList"),
        filesList: byId("filesList"),
        emptyNote: byId("filesEmpty"),
        liveStatus: byId("filesLiveStatus"),
      };
    }

    card.replaceChildren();
    card.setAttribute("aria-labelledby", "filesHeading");

    const header = document.createElement("div");
    header.className = "files-header";

    const headingGroup = document.createElement("div");
    headingGroup.className = "files-heading-group";

    const badge = document.createElement("span");
    badge.className = "files-icon-badge";
    badge.setAttribute("aria-hidden", "true");
    badge.textContent = "⇅";

    const titleWrap = document.createElement("div");
    const kicker = document.createElement("p");
    kicker.className = "section-kicker";
    window.I18n.bind(kicker, "textContent", () => t("files.two.way.file.transfer"));

    const h2 = document.createElement("h2");
    h2.id = "filesHeading";
    h2.className = "files-title";
    window.I18n.bind(h2, "textContent", () => t("files.temporary.files"));

    const countPill = document.createElement("span");
    countPill.id = "filesCount";
    countPill.className = "files-count-pill";
    countPill.textContent = "0";
    h2.append(countPill);

    titleWrap.append(kicker, h2);
    headingGroup.append(badge, titleWrap);

    const refreshButton = document.createElement("button");
    refreshButton.id = "filesRefreshButton";
    refreshButton.type = "button";
    refreshButton.className = "button button-secondary files-refresh-btn";
    window.I18n.bindAttribute(refreshButton, "aria-label", () => t("files.refresh.file.list"));
    window.I18n.bind(refreshButton, "textContent", () => t("files.refresh"));

    header.append(headingGroup, refreshButton);

    const desc = document.createElement("p");
    desc.className = "files-description";
    window.I18n.bind(desc, "textContent", () => t("files.transfer.files.between.your.phone.and.computer.uploads.are"));

    const quotaBox = document.createElement("div");
    quotaBox.className = "files-quota";

    const quotaRow = document.createElement("div");
    quotaRow.className = "files-quota-row";

    const quotaText = document.createElement("span");
    quotaText.id = "filesQuotaText";
    window.I18n.bind(quotaText, "textContent", () => t("files.used.0.b.loading"));

    const quotaCountText = document.createElement("span");
    quotaCountText.id = "filesQuotaCountText";
    quotaCountText.className = "files-quota-counts";
    window.I18n.bind(quotaCountText, "textContent", () => t("files.0.files"));

    quotaRow.append(quotaText, quotaCountText);

    const quotaBar = document.createElement("div");
    quotaBar.id = "filesQuotaBar";
    quotaBar.className = "files-progress-track";
    quotaBar.setAttribute("role", "progressbar");
    window.I18n.bindAttribute(quotaBar, "aria-label", () => t("files.temporary.storage.usage"));
    quotaBar.setAttribute("aria-valuemin", "0");
    quotaBar.setAttribute("aria-valuemax", "100");
    quotaBar.setAttribute("aria-valuenow", "0");

    const quotaFill = document.createElement("div");
    quotaFill.id = "filesQuotaFill";
    quotaFill.className = "files-progress-fill";
    quotaBar.append(quotaFill);

    quotaBox.append(quotaRow, quotaBar);

    const dropzone = document.createElement("div");
    dropzone.id = "filesDropzone";
    dropzone.className = "files-dropzone";

    const dropTitle = document.createElement("p");
    dropTitle.className = "files-dropzone-title";
    window.I18n.bind(dropTitle, "textContent", () => t("files.drop.files.here.or.use.the.button.below"));

    const dropHint = document.createElement("p");
    dropHint.id = "filesDropzoneHint";
    dropHint.className = "files-dropzone-hint";
    window.I18n.bind(dropHint, "textContent", () => t("files.you.can.select.multiple.files"));

    const selectButton = document.createElement("button");
    selectButton.id = "filesSelectButton";
    selectButton.type = "button";
    selectButton.className = "button button-primary files-select-btn";
    window.I18n.bind(selectButton, "textContent", () => t("files.choose.files"));

    const fileInput = document.createElement("input");
    fileInput.id = "filesInput";
    fileInput.className = "files-input-hidden";
    fileInput.type = "file";
    fileInput.multiple = true;
    window.I18n.bindAttribute(fileInput, "aria-label", () => t("files.choose.files.to.upload.to.temporary.storage"));

    dropzone.append(dropTitle, dropHint, selectButton, fileInput);

    const alert = document.createElement("div");
    alert.id = "filesAlert";
    alert.className = "files-alert is-error";
    alert.setAttribute("role", "status");
    alert.setAttribute("aria-live", "polite");
    alert.hidden = true;

    const alertText = document.createElement("p");
    alertText.id = "filesAlertText";
    alertText.className = "files-alert-text";

    const alertClose = document.createElement("button");
    alertClose.id = "filesAlertClose";
    alertClose.type = "button";
    alertClose.className = "files-alert-close";
    window.I18n.bindAttribute(alertClose, "aria-label", () => t("files.dismiss.message"));
    window.I18n.bind(alertClose, "textContent", () => t("files.close"));

    alert.append(alertText, alertClose);

    const uploadsList = document.createElement("div");
    uploadsList.id = "filesUploadsList";
    uploadsList.className = "files-uploads";
    uploadsList.setAttribute("aria-live", "polite");
    uploadsList.hidden = true;

    const filesList = document.createElement("div");
    filesList.id = "filesList";
    filesList.className = "files-list";
    filesList.setAttribute("aria-live", "polite");

    const emptyNote = document.createElement("p");
    emptyNote.id = "filesEmpty";
    emptyNote.className = "files-empty";
    window.I18n.bind(emptyNote, "textContent", () => t("files.no.files.here.yet.upload.a.file.from.your"));

    const liveStatus = document.createElement("div");
    liveStatus.id = "filesLiveStatus";
    liveStatus.className = "files-sr-status";
    liveStatus.setAttribute("role", "status");
    liveStatus.setAttribute("aria-live", "polite");

    card.append(header, desc, quotaBox, dropzone, alert, uploadsList, filesList, emptyNote, liveStatus);

    return {
      card,
      countPill,
      refreshButton,
      quotaBar,
      quotaFill,
      quotaText,
      quotaCountText,
      dropzone,
      dropzoneHint,
      selectButton,
      fileInput,
      alert,
      alertText,
      alertClose,
      uploadsList,
      filesList,
      emptyNote,
      liveStatus,
    };
  }

  function getVisibleFiles() {
    const nowSec = Math.floor(Date.now() / 1000);
    return state.files.filter((f) => {
      if (!f || typeof f !== "object") return false;
      const exp = Number(f.expires_at);
      return !Number.isFinite(exp) || exp > nowSec;
    });
  }

  function renderQuota() {
    if (!ui) return;
    const visibleFiles = getVisibleFiles();
    const usedBytes = visibleFiles.reduce((acc, item) => acc + (Number(item.size) || 0), 0);
    state.usedBytes = usedBytes;

    if (ui.countPill) {
      ui.countPill.textContent = window.I18n.number(visibleFiles.length);
    }

    const maxTotal = Number(state.maxTotalBytes);
    const maxFiles = Number(state.maxFiles);
    const maxSingle = Number(state.maxFileBytes);
    const ttlHours = Number.isFinite(Number(state.ttlSeconds))
      ? Math.max(1, Math.round(Number(state.ttlSeconds) / 3600))
      : 24;

    if (ui.quotaText) {
      if (Number.isFinite(maxTotal) && maxTotal > 0) {
        window.I18n.bind(ui.quotaText, "textContent", () => t("files.used", { value0: formatBytes(usedBytes), value1: formatBytes(maxTotal) }));
      } else {
        window.I18n.bind(ui.quotaText, "textContent", () => t("files.used.2", { value0: formatBytes(usedBytes) }));
      }
    }

    if (ui.quotaCountText) {
      if (Number.isFinite(maxFiles) && maxFiles > 0) {
        window.I18n.bind(ui.quotaCountText, "textContent", () => t("files.count", { count: visibleFiles.length, limit: maxFiles }));
      } else {
        window.I18n.bind(ui.quotaCountText, "textContent", () => t("common.file", { count: visibleFiles.length }));
      }
    }

    const percent = Number.isFinite(maxTotal) && maxTotal > 0
      ? Math.min(100, Math.max(0, Math.round((usedBytes / maxTotal) * 100)))
      : 0;

    if (ui.quotaBar) {
      ui.quotaBar.setAttribute("aria-valuenow", String(percent));
    }
    if (ui.quotaFill) {
      ui.quotaFill.style.width = `${percent}%`;
      ui.quotaFill.classList.toggle("is-warning", percent >= 75 && percent < 90);
      ui.quotaFill.classList.toggle("is-danger", percent >= 90);
    }

    if (ui.dropzoneHint && Number.isFinite(maxSingle) && maxSingle > 0) {
      window.I18n.bind(ui.dropzoneHint, "textContent", () => t("files.per.file.total.kept.for.hours", { value0: formatBytes(maxSingle), value1: formatBytes(maxTotal), value2: ttlHours }));
    }
  }

  function renderUploads() {
    if (!ui || !ui.uploadsList) return;
    ui.uploadsList.replaceChildren();
    ui.uploadsList.hidden = state.uploads.length === 0;
    if (state.uploads.length === 0) return;

    state.uploads.forEach((item) => {
      const row = document.createElement("div");
      row.className = "files-upload-item";
      if (item.status === "error") {
        row.classList.add("is-error");
      }

      const top = document.createElement("div");
      top.className = "files-upload-top";

      const info = document.createElement("div");
      info.className = "files-upload-info";

      const nameEl = document.createElement("strong");
      nameEl.className = "files-upload-name";
      nameEl.textContent = truncateMiddle(item.file.name, 32);
      nameEl.title = item.file.name;

      const statusEl = document.createElement("span");
      statusEl.className = "files-upload-status";
      if (item.status === "queued") {
        window.I18n.bind(statusEl, "textContent", () => t("files.queued", { value0: formatBytes(item.file.size) }));
      } else if (item.status === "uploading") {
        window.I18n.bind(statusEl, "textContent", () => t("files.uploading", { value0: formatBytes(item.file.size), value1: item.progress }));
      } else if (item.status === "error") {
        window.I18n.bind(statusEl, "textContent", () => t("files.upload.error.summary", { value0: formatBytes(item.file.size), value1: window.I18n.ref(item.errorMessage || t("common.upload.failed")) }));
      }

      info.append(nameEl, statusEl);

      const actions = document.createElement("div");
      actions.className = "files-upload-actions";

      if (item.status === "error") {
        const retryBtn = document.createElement("button");
        retryBtn.type = "button";
        retryBtn.className = "button button-primary files-upload-btn";
        window.I18n.bind(retryBtn, "textContent", () => t("files.retry"));
        window.I18n.bindAttribute(retryBtn, "aria-label", () => t("files.retry.uploading", { value0: item.file.name }));
        retryBtn.addEventListener("click", () => retryUpload(item.uid));
        actions.append(retryBtn);
      }

      const cancelBtn = document.createElement("button");
      cancelBtn.type = "button";
      cancelBtn.className = "button button-secondary files-upload-btn";
      window.I18n.bind(cancelBtn, "textContent", () => item.status === "error" ? t("app.remove") : t("files.cancel"));
      cancelBtn.setAttribute(
        "aria-label",
        t("files.upload.cancel.label", { value0: item.status === "error" ? t("common.remove.failed.upload") : t("common.cancel.upload"), value1: item.file.name })
      );
      cancelBtn.addEventListener("click", () => cancelUpload(item.uid));
      actions.append(cancelBtn);

      top.append(info, actions);

      const track = document.createElement("div");
      track.className = "files-progress-track";
      track.setAttribute("role", "progressbar");
      window.I18n.bindAttribute(track, "aria-label", () => t("files.upload.progress.for", { value0: item.file.name }));
      track.setAttribute("aria-valuemin", "0");
      track.setAttribute("aria-valuemax", "100");
      track.setAttribute("aria-valuenow", String(item.progress));

      const fill = document.createElement("div");
      fill.className = "files-progress-fill";
      if (item.status === "error") {
        fill.classList.add("is-danger");
      }
      fill.style.width = `${item.progress}%`;
      track.append(fill);

      row.append(top, track);
      ui.uploadsList.append(row);
    });
  }

  function renderFileList() {
    if (!ui || !ui.filesList || !ui.emptyNote) return;
    const visibleFiles = getVisibleFiles();
    const nowSec = Math.floor(Date.now() / 1000);

    ui.filesList.replaceChildren();
    ui.emptyNote.hidden = visibleFiles.length > 0;

    if (
      state.confirmingDeleteId &&
      !visibleFiles.some((f) => f.id === state.confirmingDeleteId)
    ) {
      state.confirmingDeleteId = null;
    }

    visibleFiles.forEach((file) => {
      const id = String(file.id || "");
      const rawName = String(file.name || "file");
      const card = document.createElement("div");
      card.className = "files-item";

      const main = document.createElement("div");
      main.className = "files-item-main";

      const info = document.createElement("div");
      info.className = "files-item-info";

      const nameEl = document.createElement("strong");
      nameEl.className = "files-item-name";
      nameEl.textContent = truncateMiddle(rawName, 34);
      nameEl.title = rawName;

      const meta = document.createElement("div");
      meta.className = "files-item-meta";

      const sourceBadge = document.createElement("span");
      const isPhone = file.source === "phone";
      sourceBadge.className = `files-source-badge${isPhone ? " is-phone" : ""}`;
      window.I18n.bind(sourceBadge, "textContent", () => isPhone ? t("files.phone") : t("files.computer"));

      const sizeSpan = document.createElement("span");
      sizeSpan.textContent = formatBytes(file.size);

      const sep = document.createElement("span");
      sep.className = "files-meta-sep";
      sep.setAttribute("aria-hidden", "true");
      sep.textContent = "·";

      const ttlSpan = document.createElement("span");
      ttlSpan.textContent = formatRemainingTime(file.expires_at, nowSec);

      meta.append(sourceBadge, sizeSpan, sep, ttlSpan);
      info.append(nameEl, meta);

      const actions = document.createElement("div");
      actions.className = "files-item-actions";

      const downloadLink = document.createElement("a");
      downloadLink.className = "button button-secondary files-action-btn";
      if (VALID_ID_RE.test(id)) {
        downloadLink.href = `/api/files/${id}`;
      } else {
        downloadLink.href = "#";
      }
      downloadLink.setAttribute("download", rawName);
      window.I18n.bindAttribute(downloadLink, "aria-label", () => t("files.download", { value0: rawName }));
      window.I18n.bind(downloadLink, "textContent", () => t("files.download.2"));

      const isDeleting = state.deletingIds.has(id);
      const deleteBtn = document.createElement("button");
      deleteBtn.type = "button";
      deleteBtn.className = "button button-danger files-action-btn";
      deleteBtn.disabled = isDeleting;
      window.I18n.bindAttribute(deleteBtn, "aria-label", () => t("files.delete", { value0: rawName }));
      deleteBtn.setAttribute("aria-expanded", String(state.confirmingDeleteId === id));
      window.I18n.bind(deleteBtn, "textContent", () => isDeleting ? t("files.deleting") : t("files.delete.2"));
      deleteBtn.addEventListener("click", () => {
        if (state.confirmingDeleteId === id) {
          state.confirmingDeleteId = null;
        } else {
          state.confirmingDeleteId = id;
        }
        renderFileList();
      });

      actions.append(downloadLink, deleteBtn);
      main.append(info, actions);
      card.append(main);

      if (state.confirmingDeleteId === id && !isDeleting) {
        const confirmBar = document.createElement("div");
        confirmBar.className = "files-confirm-bar";
        confirmBar.setAttribute("role", "group");
        window.I18n.bindAttribute(confirmBar, "aria-label", () => t("files.confirm.deletion.of", { value0: rawName }));

        const confirmText = document.createElement("span");
        confirmText.className = "files-confirm-text";
        window.I18n.bind(confirmText, "textContent", () => t("files.delete.this.file.from.temporary.storage"));

        const confirmButtons = document.createElement("div");
        confirmButtons.className = "files-confirm-buttons";

        const confirmYes = document.createElement("button");
        confirmYes.type = "button";
        confirmYes.className = "button button-danger files-action-btn";
        window.I18n.bind(confirmYes, "textContent", () => t("files.delete.file"));
        window.I18n.bindAttribute(confirmYes, "aria-label", () => t("files.confirm.deletion.of.2", { value0: rawName }));
        confirmYes.addEventListener("click", () => executeDelete(id, rawName));

        const confirmNo = document.createElement("button");
        confirmNo.type = "button";
        confirmNo.className = "button button-secondary files-action-btn";
        window.I18n.bind(confirmNo, "textContent", () => t("files.cancel"));
        window.I18n.bindAttribute(confirmNo, "aria-label", () => t("files.cancel.deletion.of", { value0: rawName }));
        confirmNo.addEventListener("click", () => {
          state.confirmingDeleteId = null;
          renderFileList();
        });

        confirmButtons.append(confirmYes, confirmNo);
        confirmBar.append(confirmText, confirmButtons);
        card.append(confirmBar);
      }

      ui.filesList.append(card);
    });
  }

  function renderAll() {
    renderQuota();
    renderUploads();
    renderFileList();
  }

  function applyFilesPayload(data) {
    if (!data || typeof data !== "object") return;
    if (Array.isArray(data.files)) {
      const copy = data.files.slice();
      copy.sort((a, b) => (Number(b.created_at) || 0) - (Number(a.created_at) || 0));
      state.files = copy;
    }
    if (Number.isFinite(Number(data.used_bytes))) {
      state.usedBytes = Number(data.used_bytes);
    }
    if (Number.isFinite(Number(data.max_file_bytes))) {
      state.maxFileBytes = Number(data.max_file_bytes);
    }
    if (Number.isFinite(Number(data.max_total_bytes))) {
      state.maxTotalBytes = Number(data.max_total_bytes);
    }
    if (Number.isFinite(Number(data.max_files))) {
      state.maxFiles = Number(data.max_files);
    }
    if (Number.isFinite(Number(data.ttl_seconds))) {
      state.ttlSeconds = Number(data.ttl_seconds);
    }
    if (Number.isFinite(Number(data.revision))) {
      state.revision = Number(data.revision);
    }
    state.loaded = true;
    renderAll();
  }

  async function fetchFiles() {
    if (!state.initialized) return false;
    if (state.fetchPromise) return state.fetchPromise;

    state.loading = true;
    if (ui && ui.refreshButton) ui.refreshButton.disabled = true;

    state.fetchPromise = (async () => {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 8000);
      try {
        const response = await fetch("/api/files", {
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
        if (!response.ok || !data) {
          const code = data && data.code;
          const serverErr = data && (data.message || data.error);
          const msg = mapErrorMessage(response.status, code, serverErr);
          if (!state.loaded) {
            showAlert(msg, true);
          }
          return false;
        }
        applyFilesPayload(data);
        return true;
      } catch (_) {
        if (!state.loaded) {
          showAlert(t("files.cannot.reach.temporary.storage.on.your.computer.check.your"), true);
        }
        return false;
      } finally {
        clearTimeout(timeout);
        state.loading = false;
        state.fetchPromise = null;
        if (ui && ui.refreshButton) ui.refreshButton.disabled = false;
      }
    })();

    return state.fetchPromise;
  }

  async function executeDelete(id, fileName) {
    if (!VALID_ID_RE.test(id) || state.deletingIds.has(id)) return;
    state.deletingIds.add(id);
    state.confirmingDeleteId = null;
    renderFileList();

    try {
      const response = await fetch("/api/files/delete", {
        method: "POST",
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ id }),
      });
      let data = null;
      try {
        data = await response.json();
      } catch (_) {
        data = null;
      }

      if (response.ok) {
        state.files = state.files.filter((f) => f.id !== id);
        if (data && Number.isFinite(Number(data.revision))) {
          state.revision = Number(data.revision);
        }
        renderAll();
        const doneMsg = t("files.deleted", { value0: truncateMiddle(fileName, 24) });
        showToast(doneMsg);
        announceStatus(doneMsg);
        await fetchFiles();
      } else if (response.status === 404) {
        state.files = state.files.filter((f) => f.id !== id);
        renderAll();
        showToast(t("files.file.is.missing.or.expired"));
        await fetchFiles();
      } else {
        const msg = mapErrorMessage(response.status, data && data.code, data && (data.message || data.error));
        showAlert(t("files.deletion.failed", { value0: window.I18n.ref(msg) }), true);
        showToast(t("files.deletion.failed", { value0: window.I18n.ref(msg) }));
      }
    } catch (_) {
      showAlert(t("files.connection.interrupted.cannot.delete.the.file"), true);
      showToast(t("files.connection.interrupted.cannot.delete.the.file"));
    } finally {
      state.deletingIds.delete(id);
      renderFileList();
    }
  }

  function countPendingUploads() {
    return state.uploads.filter(
      (u) => u.status === "queued" || u.status === "uploading"
    ).length;
  }

  function sumPendingUploadBytes() {
    return state.uploads
      .filter((u) => u.status === "queued" || u.status === "uploading")
      .reduce((acc, u) => acc + (Number(u.file.size) || 0), 0);
  }

  async function handleSelectedFiles(fileList) {
    if (!fileList || fileList.length === 0) return;
    const incoming = Array.from(fileList);

    hideAlert();

    if (!state.loaded) {
      const ok = await fetchFiles();
      if (!ok && !state.loaded) {
        showAlert(t("files.cannot.read.the.storage.limit.check.your.connection.before"), true);
        showToast(t("files.cannot.read.storage.information.try.again.later"));
        return;
      }
    }

    const visibleFiles = getVisibleFiles();
    let projectedCount = visibleFiles.length + countPendingUploads();
    let projectedBytes =
      visibleFiles.reduce((acc, f) => acc + (Number(f.size) || 0), 0) +
      sumPendingUploadBytes();

    const maxFileBytes = Number(state.maxFileBytes);
    const maxTotalBytes = Number(state.maxTotalBytes);
    const maxFiles = Number(state.maxFiles);

    const rejectedReasons = [];
    let acceptedCount = 0;

    incoming.forEach((file) => {
      const size = Number(file.size) || 0;
      const shortName = truncateMiddle(file.name, 26);

      if (Number.isFinite(maxFiles) && maxFiles > 0 && projectedCount + 1 > maxFiles) {
        rejectedReasons.push(
          t("files.was.not.uploaded.the.limit.of.files.has.been", { value0: shortName, value1: maxFiles })
        );
        return;
      }

      if (Number.isFinite(maxFileBytes) && maxFileBytes > 0 && size > maxFileBytes) {
        rejectedReasons.push(
          t("files.was.not.uploaded.it.exceeds.the.per.file.limit", { value0: shortName, value1: formatBytes(size), value2: formatBytes(maxFileBytes) })
        );
        return;
      }

      if (Number.isFinite(maxTotalBytes) && maxTotalBytes > 0 && projectedBytes + size > maxTotalBytes) {
        const remaining = Math.max(0, maxTotalBytes - projectedBytes);
        rejectedReasons.push(
          t("files.was.not.uploaded.only.of.storage.remains", { value0: shortName, value1: formatBytes(size), value2: formatBytes(remaining) })
        );
        return;
      }

      projectedCount += 1;
      projectedBytes += size;
      acceptedCount += 1;

      state.uploads.push({
        uid: state.nextUploadUid++,
        file,
        status: "queued",
        progress: 0,
        errorMessage: null,
        xhr: null,
      });
    });

    if (rejectedReasons.length > 0) {
      const combined = rejectedReasons.join(" ");
      showAlert(combined, true);
      showToast(rejectedReasons[0]);
    }

    if (acceptedCount > 0) {
      renderUploads();
      pumpUploadQueue();
    }
  }

  function pumpUploadQueue() {
    const activeCount = state.uploads.filter((u) => u.status === "uploading").length;
    if (activeCount >= MAX_CONCURRENT_UPLOADS) return;

    const availableSlots = MAX_CONCURRENT_UPLOADS - activeCount;
    const nextItems = state.uploads
      .filter((u) => u.status === "queued")
      .slice(0, availableSlots);

    nextItems.forEach((item) => startSingleUpload(item));
  }

  function startSingleUpload(item) {
    item.status = "uploading";
    item.progress = 0;
    item.errorMessage = null;
    renderUploads();

    const xhr = new XMLHttpRequest();
    item.xhr = xhr;

    const encodedName = encodeURIComponent(item.file.name);
    xhr.open("POST", `/api/files?name=${encodedName}`, true);
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.setRequestHeader("Accept", "application/json");

    xhr.upload.addEventListener("progress", (event) => {
      if (event.lengthComputable && event.total > 0) {
        const pct = Math.min(100, Math.max(0, Math.round((event.loaded / event.total) * 100)));
        if (pct !== item.progress) {
          item.progress = pct;
          renderUploads();
        }
      }
    });

    xhr.addEventListener("load", () => {
      item.xhr = null;
      let data = null;
      try {
        data = JSON.parse(xhr.responseText || "null");
      } catch (_) {
        data = null;
      }

      if (xhr.status >= 200 && xhr.status < 300) {
        state.uploads = state.uploads.filter((u) => u.uid !== item.uid);
        if (data && data.file && typeof data.file === "object") {
          const exists = state.files.some((f) => f.id === data.file.id);
          if (!exists) {
            state.files.unshift(data.file);
          }
        }
        if (data && Number.isFinite(Number(data.revision))) {
          state.revision = Number(data.revision);
        }
        renderAll();
        const msg = t("files.uploaded", { value0: truncateMiddle(item.file.name, 24) });
        showToast(msg);
        announceStatus(msg);
        pumpUploadQueue();
        fetchFiles();
        return;
      }

      const code = data && data.code;
      const serverErr = data && (data.message || data.error);
      item.status = "error";
      item.errorMessage = mapErrorMessage(xhr.status, code, serverErr);
      renderUploads();
      showToast(t("files.upload.failed", { value0: window.I18n.ref(item.errorMessage) }));
      announceStatus(t("files.upload.of.failed", { value0: item.file.name, value1: window.I18n.ref(item.errorMessage) }));
      pumpUploadQueue();
    });

    xhr.addEventListener("error", () => {
      item.xhr = null;
      item.status = "error";
      item.errorMessage = t("files.connection.interrupted.upload.failed");
      renderUploads();
      showToast(t("files.upload.failed", { value0: window.I18n.ref(item.errorMessage) }));
      announceStatus(t("files.upload.of.failed.2", { value0: item.file.name }));
      pumpUploadQueue();
    });

    xhr.addEventListener("timeout", () => {
      item.xhr = null;
      item.status = "error";
      item.errorMessage = t("files.upload.timed.out.try.again.later");
      renderUploads();
      showToast(t("files.upload.failed", { value0: window.I18n.ref(item.errorMessage) }));
      pumpUploadQueue();
    });

    xhr.addEventListener("abort", () => {
      item.xhr = null;
      state.uploads = state.uploads.filter((u) => u.uid !== item.uid);
      renderUploads();
      pumpUploadQueue();
    });

    try {
      xhr.send(item.file);
    } catch (_) {
      item.xhr = null;
      item.status = "error";
      item.errorMessage = t("files.cannot.read.the.file.or.send.the.request");
      renderUploads();
      pumpUploadQueue();
    }
  }

  function cancelUpload(uid) {
    const target = state.uploads.find((u) => u.uid === uid);
    if (!target) return;
    if (target.xhr) {
      try {
        target.xhr.abort();
      } catch (_) {}
    } else {
      state.uploads = state.uploads.filter((u) => u.uid !== uid);
      renderUploads();
      pumpUploadQueue();
    }
    announceStatus(t("files.upload.of.canceled", { value0: target.file.name }));
  }

  async function retryUpload(uid) {
    const target = state.uploads.find((u) => u.uid === uid);
    if (!target) return;

    await fetchFiles();
    const visibleFiles = getVisibleFiles();
    const otherPendingCount = state.uploads.filter(
      (u) => u.uid !== uid && (u.status === "queued" || u.status === "uploading")
    ).length;
    const otherPendingBytes = state.uploads
      .filter((u) => u.uid !== uid && (u.status === "queued" || u.status === "uploading"))
      .reduce((acc, u) => acc + (Number(u.file.size) || 0), 0);

    const usedBytes = visibleFiles.reduce((acc, f) => acc + (Number(f.size) || 0), 0) + otherPendingBytes;
    const fileSize = Number(target.file.size) || 0;

    if (Number.isFinite(state.maxFiles) && visibleFiles.length + otherPendingCount + 1 > state.maxFiles) {
      target.errorMessage = t("files.the.limit.of.files.has.been.reached", { value0: state.maxFiles });
      renderUploads();
      showToast(target.errorMessage);
      return;
    }
    if (Number.isFinite(state.maxFileBytes) && fileSize > state.maxFileBytes) {
      target.errorMessage = t("files.exceeds.the.per.file.limit.of", { value0: formatBytes(state.maxFileBytes) });
      renderUploads();
      showToast(target.errorMessage);
      return;
    }
    if (Number.isFinite(state.maxTotalBytes) && usedBytes + fileSize > state.maxTotalBytes) {
      target.errorMessage = t("files.not.enough.temporary.storage.space");
      renderUploads();
      showToast(target.errorMessage);
      return;
    }

    target.status = "queued";
    target.progress = 0;
    target.errorMessage = null;
    renderUploads();
    pumpUploadQueue();
  }

  function bindEvents() {
    if (!ui) return;

    if (ui.selectButton && ui.fileInput) {
      ui.selectButton.addEventListener("click", () => {
        ui.fileInput.click();
      });
    }

    if (ui.fileInput) {
      ui.fileInput.addEventListener("change", () => {
        if (ui.fileInput.files && ui.fileInput.files.length > 0) {
          handleSelectedFiles(ui.fileInput.files);
          ui.fileInput.value = "";
        }
      });
    }

    if (ui.dropzone) {
      let dragDepth = 0;
      ui.dropzone.addEventListener("dragenter", (event) => {
        event.preventDefault();
        event.stopPropagation();
        dragDepth += 1;
        ui.dropzone.classList.add("is-dragover");
      });

      ui.dropzone.addEventListener("dragover", (event) => {
        event.preventDefault();
        event.stopPropagation();
        if (event.dataTransfer) {
          event.dataTransfer.dropEffect = "copy";
        }
        ui.dropzone.classList.add("is-dragover");
      });

      ui.dropzone.addEventListener("dragleave", (event) => {
        event.preventDefault();
        event.stopPropagation();
        dragDepth = Math.max(0, dragDepth - 1);
        if (dragDepth === 0) {
          ui.dropzone.classList.remove("is-dragover");
        }
      });

      ui.dropzone.addEventListener("drop", (event) => {
        event.preventDefault();
        event.stopPropagation();
        dragDepth = 0;
        ui.dropzone.classList.remove("is-dragover");
        const dt = event.dataTransfer;
        if (dt && dt.files && dt.files.length > 0) {
          handleSelectedFiles(dt.files);
        }
      });
    }

    if (ui.refreshButton) {
      ui.refreshButton.addEventListener("click", () => {
        hideAlert();
        fetchFiles();
      });
    }

    if (ui.alertClose) {
      ui.alertClose.addEventListener("click", hideAlert);
    }

    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) {
        fetchFiles();
      }
    });

    window.addEventListener("focus", () => {
      if (!document.hidden) {
        fetchFiles();
      }
    });

    window.addEventListener("online", () => {
      if (!document.hidden) {
        fetchFiles();
      }
    });
  }

  function startTimers() {
    clearInterval(state.pollTimer);
    state.pollTimer = setInterval(() => {
      if (!document.hidden) {
        fetchFiles();
      }
    }, POLL_INTERVAL_MS);

    clearInterval(state.expiryTimer);
    state.expiryTimer = setInterval(() => {
      if (!document.hidden) {
        renderAll();
      }
    }, EXPIRY_TICK_MS);
  }

  function notifyRevision(nextRevision) {
    if (!state.initialized) return;
    const rev = Number(nextRevision);
    if (!Number.isFinite(rev)) return;
    if (state.revision === null || rev !== state.revision) {
      fetchFiles();
    }
  }

  function init() {
    if (state.initialized) return;
    const card = byId("filesCard");
    if (!card) return;

    state.initialized = true;
    ui = ensureCardStructure(card);
    bindEvents();
    renderAll();
    fetchFiles();
    startTimers();
  }

  window.FastInputFiles = {
    notifyRevision,
    refresh: fetchFiles,
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
  document.addEventListener("languagechange", () => {
    state.uploads.forEach(item => { item.errorMessage = window.I18n.localize(item.errorMessage); });
    if (state.initialized) renderAll();
  });
})();
