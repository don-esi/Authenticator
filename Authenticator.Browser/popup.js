const api = typeof browser !== "undefined" ? browser : chrome;
const NATIVE_HOST = "com.authenticator.native";

const statusEl = document.getElementById("status");
const detailEl = document.getElementById("detail");
const candidatesEl = document.getElementById("candidates");

function setStatus(ok, text, detail) {
  statusEl.className = "row " + (ok ? "ok" : "bad");
  statusEl.textContent = text;
  detailEl.textContent = detail || "";
}

function sendNative(message) {
  return new Promise((resolve) => {
    try {
      api.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
        const err = api.runtime.lastError;
        if (err) {
          resolve({ ok: false, reason: "locked", message: err.message });
          return;
        }
        resolve(response || { ok: false, reason: "locked" });
      });
    } catch (e) {
      resolve({ ok: false, reason: "locked", message: String(e) });
    }
  });
}

function getActiveTab() {
  return new Promise((resolve) => {
    try {
      api.tabs.query({ active: true, currentWindow: true }, (tabs) => {
        resolve((tabs && tabs[0]) || null);
      });
    } catch (_) {
      resolve(null);
    }
  });
}

function originFromUrl(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    return { origin: u.origin, url: u.href };
  } catch (_) {
    return null;
  }
}

function clearCandidates() {
  candidatesEl.innerHTML = "";
}

function renderCandidates(candidates, tabId, page) {
  clearCandidates();
  if (!candidates || !candidates.length) return;

  setStatus(
    true,
    `${candidates.length} matching logins`,
    "Pick one to fill this page."
  );

  for (const c of candidates) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "cand";
    btn.innerHTML =
      `<span class="title"></span><span class="user"></span>`;
    btn.querySelector(".title").textContent = c.title || "(untitled)";
    btn.querySelector(".user").textContent = c.username || "(no username)";
    btn.addEventListener("click", () => fillCandidate(c.id, tabId, page));
    candidatesEl.appendChild(btn);
  }
}

async function fillCandidate(itemId, tabId, page) {
  setStatus(false, "Filling…", "");
  const response = await sendNative({
    type: "getCredentials",
    origin: page.origin,
    url: page.url,
    itemId
  });

  if (!response || !response.ok || !response.items || !response.items[0]) {
    setStatus(false, "Could not load login", (response && response.reason) || "");
    return;
  }

  const item = response.items[0];
  try {
    await api.runtime.sendMessage({
      type: "fillSelected",
      tabId,
      username: item.username || "",
      password: item.password || "",
      allowAutoLogin: !!item.allowAutoLogin
    });
    setStatus(true, "Filled", item.username || "");
    clearCandidates();
  } catch (e) {
    setStatus(false, "Fill failed", String(e));
  }
}

async function loadForActiveTab() {
  clearCandidates();
  const tab = await getActiveTab();
  if (!tab || !tab.url) {
    setStatus(false, "No active web tab", "Open an http(s) page, then reopen this popup.");
    return;
  }

  const page = originFromUrl(tab.url);
  if (!page) {
    setStatus(false, "Not a web page", tab.url);
    return;
  }

  setStatus(false, "Looking up logins…", page.origin);
  const response = await sendNative({
    type: "getCredentials",
    origin: page.origin,
    url: page.url
  });

  if (!response) {
    setStatus(false, "No response from vault", "");
    return;
  }

  if (response.ok && response.items && response.items.length === 1) {
    setStatus(true, "One match — filling", response.items[0].username || "");
    await api.runtime.sendMessage({
      type: "fillSelected",
      tabId: tab.id,
      username: response.items[0].username || "",
      password: response.items[0].password || "",
      allowAutoLogin: !!response.items[0].allowAutoLogin
    });
    return;
  }

  if (response.reason === "ambiguous" && response.candidates && response.candidates.length) {
    renderCandidates(response.candidates, tab.id, page);
    return;
  }

  if (response.reason === "locked") {
    setStatus(false, "Vault locked", response.message || "Unlock Authenticator.");
    return;
  }

  if (response.reason === "no_match") {
    setStatus(false, "No Auto-fill match", "Enable Auto-fill on a login for this site.");
    return;
  }

  setStatus(false, "Unexpected response", JSON.stringify(response));
}

function ping() {
  setStatus(false, "Contacting native host…", "");
  clearCandidates();
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
      loadForActiveTab();
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
