// Polls processing status while the job is queued or running.
(() => {
  "use strict";

  const box = document.getElementById("status");
  if (box.dataset.poll !== "true") return;

  const ka = document.getElementById("status-ka");
  const en = document.getElementById("status-en");
  const errorBox = document.getElementById("status-error");

  async function poll() {
    try {
      const response = await fetch("/portal-api/status", { credentials: "same-origin" });
      if (!response.ok) return schedule(15000);
      const s = await response.json();
      ka.textContent = s.message.ka;
      en.textContent = s.message.en;
      box.className = "status status--" + (s.jobStatus ?? "none").toLowerCase();

      if (s.error) {
        errorBox.textContent = "";
        errorBox.append(s.error.ka, Object.assign(document.createElement("span"), { className: "en block", textContent: s.error.en }));
        errorBox.hidden = false;
      }
      if (s.jobStatus === "queued" || s.jobStatus === "processing") schedule(5000);
    } catch {
      schedule(15000);
    }
  }

  const schedule = ms => setTimeout(poll, ms);
  schedule(3000);
})();
