// Chunked, resumable upload: each chunk becomes one S3 multipart part on the server.
(() => {
  "use strict";

  const PARALLEL = 3;
  const RETRIES = 4;
  const csrf = document.querySelector('meta[name="csrf-token"]').content;
  const form = document.getElementById("upload");
  const fileInput = document.getElementById("file");
  const button = document.getElementById("upload-button");
  const progress = document.getElementById("progress");
  const fill = document.getElementById("progress-fill");
  const progressText = document.getElementById("progress-text");
  const errorBox = document.getElementById("upload-error");
  const maxSize = Number(form.dataset.maxSize);

  const text = {
    starting: "ატვირთვის დაწყება… / Starting upload…",
    resuming: "ატვირთვა გრძელდება… / Resuming upload…",
    finishing: "ფაილის შემოწმება… / Checking the file…",
    sessionExpired: { ka: "სესიას ვადა გაუვიდა. გახსენით ბმული ხელახლა.", en: "Your session has expired. Please open the link again." },
    network: { ka: "კავშირი შეწყდა. სცადეთ ხელახლა — ატვირთვა გაგრძელდება.", en: "Connection lost. Try again — the upload will resume." },
    tooLarge: { ka: "ფაილი ძალიან დიდია (მაქს. 2 GB).", en: "The file is too large (max 2 GB)." },
  };

  class UploadError extends Error {
    constructor(message, code) { super(message.en); this.bilingual = message; this.code = code; }
  }

  form.querySelectorAll('input[name="type"]').forEach(radio =>
    radio.addEventListener("change", () => { fileInput.accept = radio.dataset.accept; }));

  async function api(method, url, body, isBinary) {
    let response;
    try {
      response = await fetch(url, {
        method,
        credentials: "same-origin",
        headers: {
          "X-CSRF-TOKEN": csrf,
          "Content-Type": isBinary ? "application/octet-stream" : "application/json",
        },
        body: body === undefined ? undefined : isBinary ? body : JSON.stringify(body),
      });
    } catch {
      throw new UploadError(text.network, "NETWORK");
    }
    if (response.status === 401) throw new UploadError(text.sessionExpired, "SESSION");
    if (response.status === 204) return null;
    const data = await response.json().catch(() => null);
    if (!response.ok) throw new UploadError(data?.message ?? text.network, data?.code ?? String(response.status));
    return data;
  }

  const resumeKey = (type, file) => `datahub-upload:${type}:${file.name}:${file.size}:${file.lastModified}`;

  function show(done, total) {
    const pct = total ? Math.floor((done / total) * 100) : 0;
    fill.style.width = pct + "%";
    progressText.textContent = `${pct}% · ${(done / 1048576).toFixed(0)} / ${(total / 1048576).toFixed(0)} MB`;
  }

  function showError(err) {
    const m = err.bilingual ?? text.network;
    errorBox.textContent = "";
    errorBox.append(m.ka, Object.assign(document.createElement("span"), { className: "en block", textContent: m.en }));
    errorBox.hidden = false;
  }

  async function sendPart(uploadId, file, partNumber, chunkSize) {
    const start = (partNumber - 1) * chunkSize;
    const blob = file.slice(start, Math.min(start + chunkSize, file.size));
    for (let attempt = 1; ; attempt++) {
      try {
        await api("PUT", `/portal-api/uploads/${uploadId}/parts/${partNumber}`, blob, true);
        return blob.size;
      } catch (err) {
        if (err.code !== "NETWORK" && !/^5\d\d$/.test(err.code) || attempt >= RETRIES) throw err;
        await new Promise(r => setTimeout(r, 1000 * 2 ** attempt));
      }
    }
  }

  async function upload(type, file) {
    const key = resumeKey(type, file);
    let session = null, completed = new Set();

    const savedId = localStorage.getItem(key);
    if (savedId) {
      try {
        const p = await api("GET", `/portal-api/uploads/${savedId}/parts`);
        session = { uploadId: p.uploadId, partCount: p.partCount, chunkSizeBytes: JSON.parse(localStorage.getItem(key + ":chunk")) };
        completed = new Set(p.completedParts);
        progressText.textContent = text.resuming;
      } catch (err) {
        if (err.code === "SESSION") throw err;
        localStorage.removeItem(key);
      }
    }
    if (!session) {
      progressText.textContent = text.starting;
      session = await api("POST", "/portal-api/uploads", { type, fileName: file.name, sizeBytes: file.size });
      localStorage.setItem(key, session.uploadId);
      localStorage.setItem(key + ":chunk", JSON.stringify(session.chunkSizeBytes));
    }

    const { uploadId, partCount, chunkSizeBytes } = session;
    const pending = [];
    let done = 0;
    for (let n = 1; n <= partCount; n++) {
      if (completed.has(n)) done += Math.min(chunkSizeBytes, file.size - (n - 1) * chunkSizeBytes);
      else pending.push(n);
    }
    show(done, file.size);

    const worker = async () => {
      while (pending.length) {
        const n = pending.shift();
        done += await sendPart(uploadId, file, n, chunkSizeBytes);
        show(done, file.size);
      }
    };
    await Promise.all(Array.from({ length: Math.min(PARALLEL, pending.length) }, worker));

    progressText.textContent = text.finishing;
    await api("POST", `/portal-api/uploads/${uploadId}/complete`, { sha256: null });
    localStorage.removeItem(key);
    localStorage.removeItem(key + ":chunk");
  }

  form.addEventListener("submit", async e => {
    e.preventDefault();
    const file = fileInput.files[0];
    const type = form.querySelector('input[name="type"]:checked').value;
    errorBox.hidden = true;
    if (!file) return;
    if (file.size > maxSize) { showError(new UploadError(text.tooLarge, "UPLOAD_TOO_LARGE")); return; }

    button.disabled = true;
    fileInput.disabled = true;
    progress.hidden = false;
    try {
      await upload(type, file);
      window.location.assign("/status");
    } catch (err) {
      showError(err);
      button.disabled = false;
      fileInput.disabled = false;
    }
  });
})();
