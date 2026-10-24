using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VRisingVR;

/// <summary>
/// Hotkey edge detection read straight from Win32, so it works regardless of how the game
/// configures Unity's InputSystem (update mode, action maps, UI focus).
/// </summary>
internal static class Hotkeys
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    private const int VkEscape = 0x1B;

    private static readonly Dictionary<int, bool> s_wasDown = new();
    private static readonly HashSet<string> s_warned = new();

    /// <summary>Mouse buttons a binding can name, by what they are (players number the side buttons differently).</summary>
    private static readonly (string Name, int Vk)[] MouseButtons =
    {
        ("MouseMiddle", 0x04), ("MouseBack", 0x05), ("MouseForward", 0x06),
    };

    /// <summary>Everything a binding can name, for capturing one: keys by their InputSystem name, then mouse buttons.</summary>
    private static (string Name, int Vk)[] s_bindable;
    private static readonly Dictionary<int, bool> s_captureDown = new();
    // Bindings already parsed, since the desk window's keys are looked up every frame.
    private static readonly Dictionary<string, int[]> s_parsed = new();
    private static readonly char[] Separators = { ',', ' ' };

    /// <summary>
    /// True on the frame the key goes down while the game window has focus.
    /// Poll every bound key once per frame so the edge state stays current.
    /// </summary>
    public static bool Pressed(Key key)
    {
        int vk = ToVirtualKey(key);
        if (vk == 0)
        {
            Warn(key.ToString());
            return false;
        }
        return Edge(vk);
    }

    /// <summary>True while the key is held and the game window has focus (no edge detection).</summary>
    public static bool IsDown(Key key) => IsDown(ToVirtualKey(key));

    /// <summary>
    /// Like <see cref="Pressed(Key)"/>, for a binding: one or more names separated by commas, each a key (G, F3,
    /// Digit5, ...) or MouseMiddle/MouseBack/MouseForward. True when any of them goes down.
    /// </summary>
    public static bool Pressed(string binding)
    {
        int[] vks = ParseBinding(binding);
        bool pressed = false;
        foreach (int vk in vks)
            pressed |= Edge(vk); // every one, so each edge state stays current
        return pressed;
    }

    /// <summary>True while any of the binding's keys or buttons is held and the game window has focus.</summary>
    public static bool IsDown(string binding)
    {
        foreach (int vk in ParseBinding(binding))
        {
            if (IsDown(vk))
                return true;
        }
        return false;
    }

    /// <summary>True on the frame Escape goes down (cancels capturing a binding).</summary>
    public static bool EscapePressed() => Edge(VkEscape);

    /// <summary>The mouse cursor's position on the desktop in pixels (y grows downward), while the game window has focus.</summary>
    public static bool TryGetCursor(out int x, out int y)
    {
        x = y = 0;
        if (!Application.isFocused || !GetCursorPos(out Point point))
            return false;
        x = point.X;
        y = point.Y;
        return true;
    }

    private static bool s_wheelFailed;

    /// <summary>
    /// Scroll wheel notches this frame (positive = scrolled away from you), from the InputSystem: Win32 has no way to
    /// poll the wheel. The game sees the same scroll.
    /// </summary>
    public static float WheelNotches()
    {
        if (s_wheelFailed || !Application.isFocused)
            return 0f;
        try
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return 0f;
            float y = mouse.scroll.ReadValue().y;
            // Windows reports 120 per notch; newer InputSystem versions normalize that to 1.
            return MathF.Abs(y) >= 30f ? y / 120f : y;
        }
        catch (Exception e)
        {
            s_wheelFailed = true;
            Plugin.Logger.LogWarning($"Can't read the mouse wheel ({e.Message}); the desk window can't be turned with it.");
            return 0f;
        }
    }

    /// <summary>Whether the binding (one or more names) includes the key or button with this virtual-key code.</summary>
    public static bool Uses(string binding, int vk) => vk != 0 && Array.IndexOf(ParseBinding(binding), vk) >= 0;

    /// <summary>The virtual-key codes a binding names; names that aren't supported are skipped with a warning.</summary>
    private static int[] ParseBinding(string binding)
    {
        binding ??= "";
        if (s_parsed.TryGetValue(binding, out int[] parsed))
            return parsed;
        var vks = new List<int>();
        foreach (string name in binding.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            int vk = ToVirtualKey(name);
            if (vk == 0)
                Warn(name);
            else if (!vks.Contains(vk))
                vks.Add(vk);
        }
        return s_parsed[binding] = vks.ToArray();
    }

    /// <summary>The virtual-key code of one binding name, or 0 if it names nothing supported.</summary>
    public static int ToVirtualKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        name = name.Trim();
        foreach ((string mouseName, int vk) in MouseButtons)
        {
            if (string.Equals(name, mouseName, StringComparison.OrdinalIgnoreCase))
                return vk;
        }
        // A lone digit means that digit's key; any other number would parse as a raw enum value.
        if (name.Length == 1 && name[0] >= '0' && name[0] <= '9')
            return name[0] == '0' ? ToVirtualKey(Key.Digit0) : ToVirtualKey(Key.Digit1 + (name[0] - '1'));
        if (int.TryParse(name, out _) || name.Contains(','))
            return 0;
        return Enum.TryParse(name, true, out Key key) ? ToVirtualKey(key) : 0;
    }

    /// <summary>How a binding reads in messages ("MouseBack or G"): digits and the backquote as themselves, the rest by name.</summary>
    public static string DisplayName(string binding)
    {
        string[] names = (binding ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (name.Length == 6 && name.StartsWith("Digit", StringComparison.OrdinalIgnoreCase))
                names[i] = name.Substring(5);
            else if (name.Equals("Backquote", StringComparison.OrdinalIgnoreCase))
                names[i] = "`";
        }
        return names.Length == 0 ? "(none)" : string.Join(" or ", names);
    }

    /// <summary>Start capturing a new binding: inputs already held now don't count until released.</summary>
    public static void BeginCapture()
    {
        s_captureDown.Clear();
        foreach ((_, int vk) in Bindable())
            s_captureDown[vk] = IsDown(vk);
    }

    /// <summary>The name of the first bindable key or mouse button pressed since the last call, or null.</summary>
    public static string CapturePress()
    {
        string pressed = null;
        foreach ((string name, int vk) in Bindable())
        {
            bool down = IsDown(vk);
            s_captureDown.TryGetValue(vk, out bool wasDown);
            s_captureDown[vk] = down;
            if (down && !wasDown)
                pressed ??= name;
        }
        return pressed;
    }

    private static void Warn(string name)
    {
        if (s_warned.Add(name ?? ""))
            Plugin.Logger.LogWarning($"Hotkey '{name}' is not supported. Use a letter, digit, F1-F12, numpad digit, arrow, " +
                                     "navigation or punctuation key, CapsLock, or MouseMiddle, MouseBack or MouseForward.");
    }

    private static bool Edge(int vk)
    {
        bool down = IsDown(vk);
        s_wasDown.TryGetValue(vk, out bool wasDown);
        s_wasDown[vk] = down;
        return down && !wasDown;
    }

    private static bool IsDown(int vk) => vk != 0 && Application.isFocused && (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static (string Name, int Vk)[] Bindable()
    {
        if (s_bindable != null)
            return s_bindable;
        var list = new List<(string, int)>();
        foreach (Key key in Enum.GetValues(typeof(Key)))
        {
            int vk = ToVirtualKey(key);
            // Enum aliases share a value; keep one name per key.
            if (vk != 0 && !list.Exists(entry => entry.Item2 == vk))
                list.Add((key.ToString(), vk));
        }
        list.AddRange(MouseButtons);
        return s_bindable = list.ToArray();
    }

    /// <summary>The Win32 virtual-key code of a key, or 0 if it isn't supported.</summary>
    public static int ToVirtualKey(Key key)
    {
        int k = (int)key;
        if (key >= Key.F1 && key <= Key.F12) return 0x70 + (k - (int)Key.F1);
        if (key >= Key.A && key <= Key.Z) return 0x41 + (k - (int)Key.A);
        if (key >= Key.Digit1 && key <= Key.Digit9) return 0x31 + (k - (int)Key.Digit1);
        if (key >= Key.Numpad0 && key <= Key.Numpad9) return 0x60 + (k - (int)Key.Numpad0);

        return key switch
        {
            Key.Digit0 => 0x30,
            Key.LeftArrow => 0x25,
            Key.UpArrow => 0x26,
            Key.RightArrow => 0x27,
            Key.DownArrow => 0x28,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Backquote => 0xC0,
            Key.CapsLock => 0x14,
            // Punctuation, by its place on a US layout (the InputSystem names keys by position).
            Key.Semicolon => 0xBA,
            Key.Equals => 0xBB,
            Key.Comma => 0xBC,
            Key.Minus => 0xBD,
            Key.Period => 0xBE,
            Key.Slash => 0xBF,
            Key.LeftBracket => 0xDB,
            Key.Backslash => 0xDC,
            Key.RightBracket => 0xDD,
            Key.Quote => 0xDE,
            _ => 0,
        };
    }
}
