/* Authenticator Vault — background (MV3 service worker). Chrome / Edge / Firefox. */
const api = typeof browser !== "undefined" ? browser : chrome;
const NATIVE_HOST = "com.authenticator.native";

let lastFillKey = "";

function setBadge(text, color) {
  try {
    api.action.setBadgeText({ text: text || "" });
    if (color) api.action.setBadgeBackgroundColor({ color });
  } catch (_) {
    /* older APIs */
  }
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

function hostFromUrl(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    return u.hostname.replace(/^www\./i, "").toLowerCase();
  } catch (_) {
    return null;
  }
}

async function notifyTabChanged(url) {
  const host = hostFromUrl(url);
  if (!host) return;
  await sendNative({ type: "tabChanged", host });
}

async function tryAutoFill(tabId, url) {
  if (!url || !/^https?:/i.test(url)) return;

  await notifyTabChanged(url);

  const origin = (() => {
    try {
      return new URL(url).origin;
    } catch (_) {
      return null;
    }
  })();
  if (!origin) return;

  const key = `${tabId}|${origin}`;
  const response = await sendNative({ type: "getCredentials", origin, url });

  if (!response || !response.ok) {
    if (response && response.reason === "locked") setBadge("!", "#B45309");
    else if (response && response.reason === "ambiguous") setBadge("?", "#2563EB");
    else setBadge("", null);
    return;
  }

  const items = response.items || [];
  if (items.length !== 1 || items[0].score !== 100) {
    setBadge("", null);
    return;
  }

  if (lastFillKey === key) return;
  lastFillKey = key;

  const item = items[0];
  try {
    await api.tabs.sendMessage(tabId, {
      type: "fillLogin",
      username: item.username || "",
      password: item.password || "",
      allowAutoLogin: !!item.allowAutoLogin
    });
    setBadge("✓", "#15803D");
  } catch (_) {
    // Content script may not be ready — inject and retry once.
    try {
      await api.scripting.executeScript({
        target: { tabId },
        files: ["content.js"]
      });
      await api.tabs.sendMessage(tabId, {
        type: "fillLogin",
        username: item.username || "",
        password: item.password || "",
        allowAutoLogin: !!item.allowAutoLogin
      });
      setBadge("✓", "#15803D");
    } catch (e2) {
      setBadge("x", "#B91C1C");
      lastFillKey = "";
    }
  }
}

api.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.status === "complete" && tab.url) {
    tryAutoFill(tabId, tab.url);
  } else if (changeInfo.url) {
    lastFillKey = "";
    notifyTabChanged(changeInfo.url);
  }
});

api.tabs.onActivated.addListener(async (activeInfo) => {
  try {
    const tab = await api.tabs.get(activeInfo.tabId);
    if (tab && tab.url) {
      lastFillKey = "";
      tryAutoFill(tab.id, tab.url);
    }
  } catch (_) {
    /* ignore */
  }
});

api.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === "spaNavigated" && sender.tab && sender.tab.id != null) {
    lastFillKey = "";
    tryAutoFill(sender.tab.id, msg.url || sender.tab.url);
    sendResponse({ ok: true });
  }
  return false;
});

setBadge("", null);
