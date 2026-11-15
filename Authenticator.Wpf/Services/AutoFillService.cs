using System.Runtime.InteropServices;
using System.Windows.Threading;
using Authenticator.Crypto;
using Authenticator.Data.Models;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Types credentials (+ optional TOTP) into the foreground window using SendInput.
/// </summary>
public sealed class AutoFillService
{
    public static AutoFillService Instance { get; } = new();

    public async Task FillLoginAsync(VaultItem item, bool submit = false, bool includeTotp = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var user = item.Username ?? "";
        var pass = item.Password ?? "";

        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { });
        await Task.Delay(200, ct);

        TypeText(user);
        TypeTab();
        TypeText(pass);

        if (submit)
            TypeEnter();

        if (includeTotp)
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

            if (!string.IsNullOrWhiteSpace(secret))
            {
                var delay = TotpGenerator.GetDelayMsUntilUsableWindow(period, TotpGenerator.LoginMinRemainingSeconds);
                if (delay > 0)
                    await Task.Delay(delay, ct);

                var code = TotpGenerator.TryGenerate(secret, "SHA1", 6, period);
                if (!string.IsNullOrEmpty(code))
                {
                    await Task.Delay(600, ct);
                    TypeText(code);
                    if (submit)
                        TypeEnter();
                }
            }
        }

        try
        {
            await new VaultItemService().TouchLastUsedAsync(item.Id, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to record last-used for '{item.Title}'.", nameof(AutoFillService));
        }

        LoggingConfiguration.LogInfo($"Auto-fill completed for '{item.Title}' (submit={submit}, totp={includeTotp}).", nameof(AutoFillService));
    }

    public static void TypeText(string text)
    {
        foreach (var ch in text)
            SendChar(ch);
    }

    public static void TypeTab() => SendVirtualKey(0x09); // VK_TAB

    public static void TypeEnter() => SendVirtualKey(0x0D); // VK_RETURN

    public static void TypeSelectAll()
    {
        SendVirtualKey(0x11, keyUp: false); // CTRL down
        SendVirtualKey(0x41); // A
        SendVirtualKey(0x11, keyDown: false); // CTRL up
    }

    private static void SendChar(char ch)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = 0x0004 } // KEYEVENTF_UNICODE
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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
