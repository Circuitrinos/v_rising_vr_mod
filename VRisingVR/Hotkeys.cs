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

    private static readonly Dictionary<int, bool> s_wasDown = new();
    private static readonly HashSet<Key> s_warned = new();

    /// <summary>
    /// True on the frame the key goes down while the game window has focus.
    /// Poll every bound key once per frame so the edge state stays current.
    /// </summary>
    public static bool Pressed(Key key)
    {
        int vk = ToVirtualKey(key);
        if (vk == 0)
        {
            if (s_warned.Add(key))
                Plugin.Logger.LogWarning($"Hotkey {key} is not supported. Use F1-F12, A-Z, 0-9, numpad digits, arrows or navigation keys.");
            return false;
        }

        bool down = Application.isFocused && (GetAsyncKeyState(vk) & 0x8000) != 0;
        s_wasDown.TryGetValue(vk, out bool wasDown);
        s_wasDown[vk] = down;
        return down && !wasDown;
    }

    private static int ToVirtualKey(Key key)
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
            _ => 0,
        };
    }
}
