const api = typeof browser !== "undefined" ? browser : chrome;
const NATIVE_HOST = "com.authenticator.native";

const statusEl = document.getElementById("status");
const detailEl = document.getElementById("detail");

function setStatus(ok, text, detail) {
  statusEl.className = "row " + (ok ? "ok" : "bad");
  statusEl.textContent = text;
  detailEl.textContent = detail || "";
}

function ping() {
  setStatus(false, "Contacting native host…", "");
  api.runtime.sendNativeMessage(NATIVE_HOST, { type: "ping" }, (response) => {
    const err = api.runtime.lastError;
    if (err) {
      setStatus(
        false,
        "Native host failed to start",
        err.message +
          " — Settings → Browser autofill → Register native host, then reload this extension."
      );
      return;
    }
    if (response && response.ok && response.unlocked) {
      setStatus(
        true,
        "Connected — vault unlocked",
        "Auto-fill runs on matching sites when Auto-fill is enabled on the login."
      );
      return;
    }
    if (response && response.ok && response.unlocked === false) {
      setStatus(false, "Host OK — vault locked", "Unlock Authenticator, then click Test connection.");
      return;
    }
    if (response && response.reason === "unavailable") {
      setStatus(
        false,
        "Desktop bridge not listening",
        (response.message || "") +
          " Restart Authenticator unlocked (needs rebuild with TCP bridge on 127.0.0.1:38456), reload this extension, then Test connection."
      );
      return;
    }
    if (response && response.reason === "locked") {
      setStatus(false, "Vault locked", response.message || "Unlock Authenticator.");
      return;
    }
    setStatus(false, "Unexpected response", JSON.stringify(response || {}));
  });
}

document.getElementById("ping").addEventListener("click", ping);
ping();
