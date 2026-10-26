using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Poe2Verlay;

/// <summary>
/// Global low-level keyboard hook. Unlike RegisterHotKey it lets us swallow a key only while the game is
/// focused and watch Escape without stealing it.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    readonly Native.LowLevelKeyboardProc _proc;
    readonly HashSet<uint> _swallowed = new();
    IntPtr _hook;

    /// <summary>Return true to swallow the key.</summary>
    public Func<Keys, bool>? KeyDown;

    public KeyboardHook()
    {
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                var msg = (int)wParam;
                if ((info.flags & Native.LLKHF_INJECTED) == 0)
                {
                    if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
                    {
                        if (_swallowed.Contains(info.vkCode)) return 1; // auto-repeat of a key we took
                        var vk = (Keys)info.vkCode;
                        if (!IsModifier(vk) && KeyDown?.Invoke(vk | Modifiers()) == true)
                        {
                            _swallowed.Add(info.vkCode);
                            return 1;
                        }
                    }
                    else if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP && _swallowed.Remove(info.vkCode))
                    {
                        return 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("hook: " + ex.Message);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static bool IsModifier(Keys k) => k is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey
        or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    static Keys Modifiers()
    {
        var k = Keys.None;
        if (Native.GetAsyncKeyState(0x11) < 0) k |= Keys.Control;
        if (Native.GetAsyncKeyState(0x10) < 0) k |= Keys.Shift;
        if (Native.GetAsyncKeyState(0x12) < 0) k |= Keys.Alt;
        return k;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// Watches for a real mouse click (never blocks it). Only installed while price bars are on screen,
/// so the rest of the time the cursor never goes through our process.
/// </summary>
internal sealed class ClickWatcher : IDisposable
{
    readonly Native.LowLevelKeyboardProc _proc;
    readonly Action _onClick;
    IntPtr _hook;

    public ClickWatcher(Action onClick)
    {
        _onClick = onClick;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLMHF_INJECTED) == 0) _onClick();
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

public sealed class ParsedItem
{
    public string ItemClass = "";
    public string Rarity = "";
    public string Name = "";
    public string? BaseType;
    public int Stack = 1;
    public int? Level;
}

internal static partial class ItemText
{
    /// <summary>Hover an item in game and copy its text with Ctrl+C (what every PoE price checker does).</summary>
    public static async Task<string?> CopyHoveredItemAsync()
    {
        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { /* clipboard busy */ }

        var text = await CopyAsync(500);

        if (previous != null)
        {
            try { Clipboard.SetText(previous); } catch { /* best effort */ }
        }
        return text;
    }

    /// <summary>Send Ctrl+C and wait for the game to put item text on the clipboard. Null if nothing came.</summary>
    public static async Task<string?> CopyAsync(int timeoutMs)
    {
        var sequence = Native.GetClipboardSequenceNumber();
        var inputs = new[]
        {
            Native.Key(0x11, false), Native.Key(0x43, false), Native.Key(0x43, true), Native.Key(0x11, true),
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());

        var waited = 0;
        while (waited < timeoutMs)
        {
            await Task.Delay(10);
            waited += 10;
            if (Native.GetClipboardSequenceNumber() == sequence) continue;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    return text != null && text.Contains("Rarity:") ? text : null;
                }
                catch
                {
                    await Task.Delay(10); // the game may still hold the clipboard open
                }
            }
            return null;
        }
        return null;
    }

    public static ParsedItem? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        var r = lines.FindIndex(l => l.StartsWith("Rarity:", StringComparison.Ordinal));
        if (r < 0 || r + 1 >= lines.Count) return null;

        var item = new ParsedItem { Rarity = lines[r]["Rarity:".Length..].Trim(), Name = lines[r + 1] };
        var cls = lines.FirstOrDefault(l => l.StartsWith("Item Class:", StringComparison.Ordinal));
        if (cls != null) item.ItemClass = cls["Item Class:".Length..].Trim();
        if (r + 2 < lines.Count && !lines[r + 2].StartsWith("---", StringComparison.Ordinal)) item.BaseType = lines[r + 2];

        var stack = StackRegex().Match(text);
        if (stack.Success && int.TryParse(Regex.Replace(stack.Groups[1].Value, @"\D", ""), out var n) && n > 0) item.Stack = n;
        var level = LevelRegex().Match(text);
        if (level.Success) item.Level = int.Parse(level.Groups[1].Value);
        return item;
    }

    [GeneratedRegex(@"Stack Size:\s*([\d,\. ]+?)\s*/")]
    private static partial Regex StackRegex();

    [GeneratedRegex(@"^Level:\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex LevelRegex();

    /// <summary>Find poe.ninja prices for a copied item.</summary>
    public static List<PriceEntry>? Lookup(ItemMatcher matcher, ParsedItem item)
    {
        if (matcher.Lookup(item.Name) is { } byName)
        {
            if (item.BaseType == null) return byName;
            var sameBase = byName.Where(e => !e.IsUnique || string.Equals(e.BaseType, item.BaseType, StringComparison.OrdinalIgnoreCase)).ToList();
            return sameBase.Count > 0 ? sameBase : byName;
        }
        if (item.Level is { } lvl && matcher.Lookup($"{item.Name} (Level {lvl})") is { } leveled) return leveled;
        if (item.BaseType != null && matcher.Lookup(item.BaseType) is { } byBase) return byBase;
        // Unidentified unique: the text only has the base. Priced as the cheapest unique on that base.
        if (item.Rarity == "Unique" && item.BaseType == null && matcher.UniquesOnBase(item.Name) is { } onBase) return onBase;
        return null;
    }
}
