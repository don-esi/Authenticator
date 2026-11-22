/* Authenticator Vault — content script: DOM fill + two-step login. */
(function () {
  if (window.__authenticatorVaultContent) return;
  window.__authenticatorVaultContent = true;

  const api = typeof browser !== "undefined" ? browser : chrome;

  const NEXT_LABELS = ["next", "continue", "weiter", "suivant", "siguiente", "próximo", "proximo"];
  const SUBMIT_LABELS = [
    "sign in",
    "signin",
    "log in",
    "login",
    "log on",
    "submit",
    "verify",
    "confirm",
    "anmelden",
    "entrar"
  ];

  function visible(el) {
    if (!el || el.disabled) return false;
    const style = window.getComputedStyle(el);
    if (style.display === "none" || style.visibility === "hidden" || style.opacity === "0")
      return false;
    const r = el.getBoundingClientRect();
    return r.width > 2 && r.height > 2;
  }

  function setNativeValue(input, value) {
    const proto = window.HTMLInputElement.prototype;
    const desc = Object.getOwnPropertyDescriptor(proto, "value");
    if (desc && desc.set) desc.set.call(input, value);
    else input.value = value;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
  }

  function isUsernameField(el) {
    if (!el || el.type === "password" || el.type === "hidden") return false;
    if (!visible(el)) return false;
    const t = (el.type || "").toLowerCase();
    if (["email", "text", "tel", "url", "search"].includes(t) || t === "") {
      const blob = `${el.name} ${el.id} ${el.autocomplete} ${el.placeholder} ${el.getAttribute("aria-label") || ""}`.toLowerCase();
      if (/pass/.test(blob)) return false;
      return true;
    }
    return false;
  }

  function findUsername() {
    const inputs = [...document.querySelectorAll("input")].filter(isUsernameField);
    const scored = inputs.map((el) => {
      const blob = `${el.name} ${el.id} ${el.autocomplete} ${el.placeholder} ${el.getAttribute("aria-label") || ""}`.toLowerCase();
      let score = 1;
      if (/email|user|login|account|phone|identifier/.test(blob)) score += 5;
      if (el === document.activeElement) score += 3;
      return { el, score };
    });
    scored.sort((a, b) => b.score - a.score);
    return scored.length ? scored[0].el : null;
  }

  function findPassword() {
    return (
      [...document.querySelectorAll('input[type="password"]')].find(visible) || null
    );
  }

  function buttonLabel(el) {
    return (
      el.innerText ||
      el.textContent ||
      el.value ||
      el.getAttribute("aria-label") ||
      el.getAttribute("name") ||
      ""
    )
      .trim()
      .toLowerCase();
  }

  function findButton(labels) {
    const candidates = [
      ...document.querySelectorAll("button, input[type=submit], input[type=button], [role=button]")
    ].filter(visible);
    for (const label of labels) {
      for (const el of candidates) {
        const text = buttonLabel(el);
        if (text === label || text.includes(label)) return el;
      }
    }
    return null;
  }

  function click(el) {
    if (!el) return false;
    el.focus();
    el.click();
    return true;
  }

  function waitForPassword(ms) {
    return new Promise((resolve) => {
      const existing = findPassword();
      if (existing) {
        resolve(existing);
        return;
      }
      const start = Date.now();
      const obs = new MutationObserver(() => {
        const p = findPassword();
        if (p) {
          obs.disconnect();
          resolve(p);
        } else if (Date.now() - start > ms) {
          obs.disconnect();
          resolve(null);
        }
      });
      obs.observe(document.documentElement, { childList: true, subtree: true });
      setTimeout(() => {
        obs.disconnect();
        resolve(findPassword());
      }, ms);
    });
  }

  async function fillLogin(msg) {
    const username = msg.username || "";
    const password = msg.password || "";
    const autoLogin = !!msg.allowAutoLogin;

    let userField = findUsername();
    let passField = findPassword();

    if (userField && username) {
      userField.focus();
      setNativeValue(userField, username);
      await delay(200);
    }

    // Two-step: Auto-fill still advances to the password field; Auto-login only submits.
    passField = findPassword();
    if (!passField && userField && password) {
      if (!click(findButton(NEXT_LABELS))) {
        userField.focus();
        userField.dispatchEvent(
          new KeyboardEvent("keydown", { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true })
        );
        userField.dispatchEvent(
          new KeyboardEvent("keyup", { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true })
        );
      }
      passField = await waitForPassword(4000);
    }

    if (passField && password) {
      passField.focus();
      setNativeValue(passField, password);
      await delay(200);
      if (autoLogin) {
        if (!click(findButton(SUBMIT_LABELS))) {
          passField.focus();
          passField.dispatchEvent(
            new KeyboardEvent("keydown", { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true })
          );
        }
      }
    } else if (!passField && password) {
      // Password step may not expose type=password yet — type into focused field.
      const active = document.activeElement;
      if (active && active.tagName === "INPUT" && active.type !== "email") {
        setNativeValue(active, password);
        await delay(150);
        if (autoLogin) click(findButton(SUBMIT_LABELS));
      }
    }
  }

  function delay(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  api.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg && msg.type === "fillLogin") {
      fillLogin(msg).then(() => sendResponse({ ok: true })).catch((e) =>
        sendResponse({ ok: false, error: String(e) })
      );
      return true;
    }
    return false;
  });

  // SPA navigations
  const pushState = history.pushState;
  history.pushState = function () {
    pushState.apply(this, arguments);
    api.runtime.sendMessage({ type: "spaNavigated", url: location.href });
  };
  window.addEventListener("popstate", () => {
    api.runtime.sendMessage({ type: "spaNavigated", url: location.href });
  });
})();
