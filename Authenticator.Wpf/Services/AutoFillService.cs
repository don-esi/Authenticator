using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using Authenticator.Crypto;
using Authenticator.Data.Models;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Fills credentials into the target window via UI Automation when possible, else SendInput.
/// Auto-login also invokes Next / Submit / Sign-in style buttons for multi-step forms.
/// </summary>
public sealed class AutoFillService
{
    public static AutoFillService Instance { get; } = new();

    private static readonly string[] NextButtonNames =
    [
        "next", "continue", "weiter", "suivant", "siguiente", "avançar", "proximo", "próximo"
    ];

    /// <summary>Final-step only — do not include Next/Continue (stale step-1 buttons still match).</summary>
    private static readonly string[] SubmitButtonNames =
    [
        "sign in", "signin", "log in", "login", "log on", "submit",
        "anmelden", "connexion", "iniciar sesión", "entrar", "verify", "confirm"
    ];

    private static readonly string[] UsernameHints =
    [
        "username", "user name", "user", "email", "e-mail", "login", "account",
        "phone", "userid", "user id", "identifier"
    ];

    private static readonly string[] BrowserChromeEditHints =
    [
        "address and search bar", "address bar", "search or enter address",
        "search with google or enter address", "search with bing or enter address",
        "omnibox", "find in page", "search tabs", "search the web",
        "type a question", "ask copilot"
    ];

    public async Task FillLoginAsync(
        VaultItem item,
        IntPtr targetHwnd,
        bool autoLogin = false,
        bool includeTotp = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var user = item.Username ?? "";
        var pass = item.Password ?? "";

        if (targetHwnd != IntPtr.Zero)
            SetForegroundWindow(targetHwnd);

        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { });
        await Task.Delay(250, ct);

        var usedUia = false;
        try
        {
            usedUia = await FillViaUiAutomationAsync(targetHwnd, user, pass, autoLogin, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "UIA autofill failed; falling back to SendInput.", nameof(AutoFillService));
        }

        if (!usedUia)
        {
            // Avoid typing into the omnibox: focus page document when possible.
            TryFocusPageDocument(targetHwnd);
            await Task.Delay(100, ct);
            TypeText(user);
            TypeTab();
            TypeText(pass);
            if (autoLogin)
                TypeEnter();
        }

        if (includeTotp || autoLogin)
            await TryTypeTotpAsync(item, autoLogin, ct);

        try
        {
            await new VaultItemService().TouchLastUsedAsync(item.Id, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to record last-used for '{item.Title}'.", nameof(AutoFillService));
        }

        LoggingConfiguration.LogInfo(
            $"Auto-fill completed for '{item.Title}' (login={autoLogin}, uia={usedUia}).",
            nameof(AutoFillService));
    }

    private static async Task<bool> FillViaUiAutomationAsync(
        IntPtr hwnd, string username, string password, bool autoLogin, CancellationToken ct)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var root = AutomationElement.FromHandle(hwnd);
        if (root is null)
            return false;

        // Prefer page document — never treat the omnibox as a login field.
        var page = FindPageContentRoot(root) ?? root;
        var userField = FindUsernameField(page, root);
        var passField = FindPasswordField(page, root);

        if (userField is null && passField is null)
            return false;

        var twoStep = passField is null && userField is not null;

        if (userField is not null)
        {
            // Keystrokes so React/Angular enable Continue (ValuePattern skips JS events).
            SetFieldValue(userField, username, preferTyping: true);
            await Task.Delay(300, ct);
        }

        if (twoStep)
        {
            await AdvancePastUsernameAsync(hwnd, userField!, ct);
            // Chromium often does not expose the password edit to UIA — still type after a settle delay.
            passField = await WaitForPasswordFieldAsync(hwnd, ct, maxWaitMs: 2500);
            if (passField is null)
            {
                try
                {
                    var latest = AutomationElement.FromHandle(hwnd);
                    if (latest is not null)
                        passField = FindPasswordField(FindPageContentRoot(latest) ?? latest, latest);
                }
                catch (ElementNotAvailableException)
                {
                    // Blind-type path below.
                }
            }
        }

        if (passField is not null)
        {
            SetFieldValue(passField, password, preferTyping: true);
        }
        else if (twoStep && !string.IsNullOrEmpty(password))
        {
            // Blind type: after Continue/Enter the site usually focuses the password box.
            await Task.Delay(200, ct);
            TypeText(password);
        }
        else if (!string.IsNullOrEmpty(password) && userField is not null)
        {
            try { userField.SetFocus(); } catch { /* ignore */ }
            TypeTab();
            TypeText(password);
        }
        else if (string.IsNullOrEmpty(password))
        {
            return userField is not null;
        }

        await Task.Delay(250, ct);

        if (autoLogin)
            await SubmitPasswordStepAsync(hwnd, passField, ct);

        return true;
    }

    /// <summary>
    /// Advance email → password step: physical click Continue if found, else Enter on the email field.
    /// </summary>
    private static async Task AdvancePastUsernameAsync(
        IntPtr hwnd, AutomationElement userField, CancellationToken ct)
    {
        SetForegroundWindow(hwnd);
        await Task.Delay(100, ct);

        AutomationElement? root = null;
        AutomationElement? page = null;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
            page = root is null ? null : FindPageContentRoot(root) ?? root;
        }
        catch (ElementNotAvailableException)
        {
            // Enter fallback.
        }

        // Prefer a real mouse click — InvokePattern is unreliable on Chromium web content.
        var clicked = false;
        if (page is not null)
            clicked = TryClickButton(page, NextButtonNames);
        if (!clicked && root is not null)
            clicked = TryClickButton(root, NextButtonNames);

        if (!clicked)
        {
            try { userField.SetFocus(); } catch { /* ignore */ }
            await Task.Delay(80, ct);
            TypeEnter();
        }

        // Give SPAs time to swap to the password step.
        await Task.Delay(900, ct);
    }

    private static async Task<AutomationElement?> WaitForPasswordFieldAsync(
        IntPtr hwnd, CancellationToken ct, int maxWaitMs = 2500)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is not null)
                {
                    var page = FindPageContentRoot(root) ?? root;
                    var pass = FindPasswordField(page, root);
                    if (pass is not null)
                        return pass;
                }
            }
            catch (ElementNotAvailableException)
            {
                // Retry.
            }

            await Task.Delay(150, ct);
        }

        return null;
    }

    private static async Task SubmitPasswordStepAsync(
        IntPtr hwnd, AutomationElement? passField, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        SetForegroundWindow(hwnd);

        AutomationElement? root = null;
        AutomationElement? page = null;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
            page = root is null ? null : FindPageContentRoot(root) ?? root;
            if (page is not null && root is not null)
                passField = FindPasswordField(page, root) ?? passField;
        }
        catch (ElementNotAvailableException)
        {
            // Enter fallback.
        }

        var clicked = false;
        if (page is not null)
            clicked = TryClickButton(page, SubmitButtonNames);
        if (!clicked && root is not null)
            clicked = TryClickButton(root, SubmitButtonNames);

        if (passField is not null)
        {
            try { passField.SetFocus(); } catch { /* ignore */ }
        }

        await Task.Delay(80, ct);
        // Enter submits most password steps even when the Sign-in button isn't in UIA.
        TypeEnter();
    }

    /// <summary>Document / web-content subtree when Chromium exposes it.</summary>
    private static AutomationElement? FindPageContentRoot(AutomationElement root)
    {
        try
        {
            var doc = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (doc is not null)
                return doc;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }

    private static AutomationElement? FindUsernameField(AutomationElement page, AutomationElement window)
    {
        var candidates = CollectLoginEdits(page);
        if (candidates.Count == 0 && !ReferenceEquals(page, window))
            candidates = CollectLoginEdits(window);

        AutomationElement? best = null;
        var bestScore = int.MinValue;
        foreach (var edit in candidates)
        {
            if (IsPasswordEdit(edit) || IsBrowserChromeEdit(edit) || !IsLikelyVisible(edit))
                continue;

            var score = ScoreUsernameField(edit);
            if (score > bestScore)
            {
                bestScore = score;
                best = edit;
            }
        }

        // Require at least "non-chrome page edit" (score >= 0); never fall back to omnibox.
        return bestScore >= 0 ? best : null;
    }

    private static AutomationElement? FindPasswordField(AutomationElement page, AutomationElement window)
    {
        foreach (var scope in DistinctScopes(page, window))
        {
            foreach (var edit in CollectLoginEdits(scope))
            {
                if (!IsPasswordEdit(edit) || IsBrowserChromeEdit(edit) || !IsLikelyVisible(edit))
                    continue;
                return edit;
            }
        }

        return null;
    }

    private static IEnumerable<AutomationElement> DistinctScopes(AutomationElement page, AutomationElement window)
    {
        yield return page;
        if (!ReferenceEquals(page, window))
            yield return window;
    }

    private static List<AutomationElement> CollectLoginEdits(AutomationElement root)
    {
        var list = new List<AutomationElement>();
        try
        {
            var found = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (found is null)
                return list;
            foreach (AutomationElement el in found)
                list.Add(el);
        }
        catch (ElementNotAvailableException)
        {
            // Window went away.
        }

        return list;
    }

    private static int ScoreUsernameField(AutomationElement edit)
    {
        try
        {
            var name = (edit.Current.Name ?? "").ToLowerInvariant();
            var id = (edit.Current.AutomationId ?? "").ToLowerInvariant();
            var help = "";
            try { help = (edit.Current.HelpText ?? "").ToLowerInvariant(); }
            catch { /* optional */ }

            var blob = $"{name} {id} {help}";
            var score = 0;

            if (UsernameHints.Any(h => blob.Contains(h, StringComparison.Ordinal)))
                score += 50;

            // Prefer focused page field (user already clicked the login box).
            try
            {
                if (edit.Current.HasKeyboardFocus)
                    score += 40;
            }
            catch { /* ignore */ }

            // Prefer edits lower on the page than the toolbar strip.
            var top = edit.Current.BoundingRectangle.Top;
            if (!double.IsNaN(top) && top > 120)
                score += 10;
            else if (!double.IsNaN(top) && top <= 80)
                score -= 30;

            return score;
        }
        catch (ElementNotAvailableException)
        {
            return int.MinValue;
        }
    }

    private static bool IsBrowserChromeEdit(AutomationElement edit)
    {
        try
        {
            var name = (edit.Current.Name ?? "").ToLowerInvariant();
            var id = (edit.Current.AutomationId ?? "").ToLowerInvariant();
            if (BrowserChromeEditHints.Any(h =>
                    name.Contains(h, StringComparison.Ordinal) ||
                    id.Contains(h.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal)))
                return true;

            // Chromium omnibox is typically a wide edit in the top chrome strip.
            var rect = edit.Current.BoundingRectangle;
            if (!rect.IsEmpty && rect.Top < 90 && rect.Width > 280 && rect.Height is > 18 and < 48)
                return true;

            return false;
        }
        catch (ElementNotAvailableException)
        {
            return true;
        }
    }

    private static bool IsPasswordEdit(AutomationElement edit)
    {
        try
        {
            if (edit.Current.IsPassword)
                return true;
            var name = (edit.Current.Name ?? "").ToLowerInvariant();
            var id = (edit.Current.AutomationId ?? "").ToLowerInvariant();
            return name.Contains("password", StringComparison.Ordinal) ||
                   name.Contains("passwd", StringComparison.Ordinal) ||
                   id.Contains("password", StringComparison.Ordinal) ||
                   id.Contains("passwd", StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsLikelyVisible(AutomationElement el)
    {
        try
        {
            if (el.Current.IsOffscreen)
                return false;
            var rect = el.Current.BoundingRectangle;
            return !rect.IsEmpty && rect.Width > 2 && rect.Height > 2;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void TryFocusPageDocument(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var page = root is null ? null : FindPageContentRoot(root);
            page?.SetFocus();
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void SetFieldValue(AutomationElement field, string value, bool preferTyping = false)
    {
        try
        {
            field.SetFocus();
        }
        catch
        {
            // Continue — typing may still land correctly.
        }

        // Prefer real keystrokes so React/Angular enable Continue/Next.
        if (preferTyping)
        {
            TypeSelectAll();
            TypeText(value);
            return;
        }

        if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) &&
            patternObj is ValuePattern valuePattern)
        {
            try
            {
                valuePattern.SetValue(value);
                return;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "ValuePattern.SetValue failed; using SendInput.", nameof(AutoFillService));
            }
        }

        TypeSelectAll();
        TypeText(value);
    }

    /// <summary>
    /// Find Continue / Sign in as a Button and click its center with the mouse.
    /// Chromium web buttons often ignore InvokePattern.
    /// </summary>
    private static bool TryClickButton(AutomationElement root, IReadOnlyList<string> names)
    {
        try
        {
            AutomationElement? best = null;
            var bestScore = int.MinValue;
            foreach (var btn in FindControls(root, ControlType.Button))
            {
                try
                {
                    if (!IsLikelyVisible(btn) || !btn.Current.IsEnabled)
                        continue;
                    var name = (btn.Current.Name ?? "").Trim().ToLowerInvariant();
                    var id = (btn.Current.AutomationId ?? "").Trim().ToLowerInvariant();
                    var score = ScoreButtonName(name, id, names);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = btn;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Skip.
                }
            }

            if (best is null || bestScore < 0)
                return false;

            return ClickElement(best);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool ClickElement(AutomationElement el)
    {
        try
        {
            var pt = el.GetClickablePoint();
            if (double.IsNaN(pt.X) || double.IsNaN(pt.Y) || pt.X <= 0 || pt.Y <= 0)
            {
                var rect = el.Current.BoundingRectangle;
                if (rect.IsEmpty)
                    return false;
                pt = new System.Windows.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            }

            var x = (int)Math.Round(pt.X);
            var y = (int)Math.Round(pt.Y);
            if (!SetCursorPos(x, y))
                return false;

            // Small move settle then left click.
            Thread.Sleep(40);
            MouseClick();
            return true;
        }
        catch (NoClickablePointException)
        {
            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Mouse click on login button failed.", nameof(AutoFillService));
            return false;
        }
    }

    private static void MouseClick()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = 0, // INPUT_MOUSE
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } // LEFTDOWN
        };
        inputs[1] = new INPUT
        {
            type = 0,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } // LEFTUP
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static List<AutomationElement> FindControls(AutomationElement root, ControlType type)
    {
        var list = new List<AutomationElement>();
        try
        {
            var found = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            if (found is null)
                return list;
            foreach (AutomationElement el in found)
                list.Add(el);
        }
        catch (ElementNotAvailableException)
        {
            // Ignore.
        }

        return list;
    }

    private static int ScoreButtonName(string name, string id, IReadOnlyList<string> names)
    {
        var best = -1;
        for (var i = 0; i < names.Count; i++)
        {
            var n = names[i];
            var compact = n.Replace(" ", "", StringComparison.Ordinal);
            // Prefer exact / whole-label matches over loose Contains (e.g. "next article").
            int score;
            if (name == n || id == compact)
                score = 200 - i;
            else if (name.StartsWith(n + " ", StringComparison.Ordinal) ||
                     name.EndsWith(" " + n, StringComparison.Ordinal))
                score = 120 - i;
            else if (name.Contains(n, StringComparison.Ordinal) && name.Length <= n.Length + 8)
                score = 80 - i;
            else if (id.Contains(compact, StringComparison.Ordinal) && id.Length <= compact.Length + 12)
                score = 60 - i;
            else
                continue;

            if (score > best)
                best = score;
        }

        return best;
    }

    private static async Task TryTypeTotpAsync(VaultItem item, bool submit, CancellationToken ct)
    {
        var secret = item.TotpSecret;
        var period = 30;
        if (item.LinkedAccountId is int accountId)
        {
            var db = new DatabaseService();
            var account = await db.GetAccountByIdAsync(accountId);
            if (account is not null)
            {
                secret = account.SecretKey;
                period = account.Period;
            }
        }

        if (string.IsNullOrWhiteSpace(secret))
            return;

        var delay = TotpGenerator.GetDelayMsUntilUsableWindow(period, TotpGenerator.LoginMinRemainingSeconds);
        if (delay > 0)
            await Task.Delay(delay, ct);

        var code = TotpGenerator.TryGenerate(secret, "SHA1", 6, period);
        if (string.IsNullOrEmpty(code))
            return;

        await Task.Delay(600, ct);
        TypeText(code);
        if (submit)
            TypeEnter();
    }

    public static void TypeText(string text)
    {
        foreach (var ch in text)
            SendChar(ch);
    }

    public static void TypeTab() => SendVirtualKey(0x09);

    public static void TypeEnter() => SendVirtualKey(0x0D);

    public static void TypeSelectAll()
    {
        SendVirtualKey(0x11, keyUp: false);
        SendVirtualKey(0x41);
        SendVirtualKey(0x11, keyDown: false);
    }

    private static void SendChar(char ch)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = 0x0004 }
            }
        };
        inputs[1] = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = 0x0004 | 0x0002 }
            }
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(ushort vk, bool keyDown = true, bool keyUp = true)
    {
        var list = new List<INPUT>();
        if (keyDown)
        {
            list.Add(new INPUT
            {
                type = 1,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } }
            });
        }
        if (keyUp)
        {
            list.Add(new INPUT
            {
                type = 1,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0002 } }
            });
        }
        var arr = list.ToArray();
        _ = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
