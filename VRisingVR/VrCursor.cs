using System;
using System.Runtime.InteropServices;
using System.Text;
using ProjectM;
using UnityEngine;
using UnityEngine.UI;

namespace VRisingVR;

/// <summary>
/// The game draws its mouse cursor as an OS hardware cursor, which never appears in any capture.
/// This draws the same cursor image (the game's current CursorData texture and hotspot) as a
/// top-most screen-space UI element at the mouse position. The virtual screen and the HUD layer
/// both capture the screen-space UI, so the cursor appears in VR in both. On the desktop it sits
/// exactly under the hardware cursor.
/// </summary>
internal sealed class VrCursor
{
    private GameObject _root;
    private RectTransform _rect;
    private RawImage _image;
    private Texture2D _fallback;
    private int _shownType = int.MinValue;
    private bool _active;
    private IntPtr _window;
    private float _windowLookupCooldown;

    private bool _failed;

    public void Tick(bool show)
    {
        if (_failed)
            return;
        try
        {
            if (!show)
            {
                SetActive(false);
                return;
            }

            if (!EnsureCreated())
                return;

            if (!Cursor.visible || !TryGetMousePosition(out Vector2 position))
            {
                SetActive(false);
                return;
            }

            UpdateImage();
            _rect.anchoredPosition = position;
            SetActive(true);
        }
        catch (Exception e)
        {
            // Some UI APIs may be stripped from this build; fail once, loudly, and leave the game alone.
            _failed = true;
            Plugin.Logger.LogError($"VR cursor disabled after an error: {e}");
            try { SetActive(false); } catch { }
        }
    }

    public void Destroy()
    {
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
        if (_fallback != null)
            UnityEngine.Object.Destroy(_fallback);
        _root = null;
        _rect = null;
        _image = null;
        _fallback = null;
        _shownType = int.MinValue;
        _active = false;
    }

    private bool EnsureCreated()
    {
        if (_root != null)
            return true;

        _root = new GameObject("VRisingVR_Cursor");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue; // above all game UI

        var imageObject = new GameObject("Cursor");
        imageObject.transform.SetParent(_root.transform, false);
        _image = imageObject.AddComponent<RawImage>();
        _image.raycastTarget = false; // never blocks the game's own UI input
        _rect = imageObject.GetComponent<RectTransform>();
        _rect.anchorMin = Vector2.zero;
        _rect.anchorMax = Vector2.zero;

        _root.SetActive(false);
        _active = false;
        Plugin.Logger.LogInfo("VR cursor created.");
        return true;
    }

    private void SetActive(bool active)
    {
        if (_root != null && _active != active)
        {
            _root.SetActive(active);
            _active = active;
        }
    }

    /// <summary>Use the game's own cursor image for its current cursor type (arrow, crosshair, ...).</summary>
    private void UpdateImage()
    {
        int type = (int)CursorController._CursorType;
        if (type == _shownType)
            return;
        _shownType = type;

        Texture2D texture = null;
        Vector2 hotspot = default;
        try
        {
            var datas = CursorController._CursorDatas;
            if (datas != null)
            {
                for (int i = 0; i < datas.Length; i++)
                {
                    CursorData data = datas[i];
                    if (data != null && (int)data.CursorType == type && data.Texture != null)
                    {
                        texture = data.Texture;
                        hotspot = data.Hotspot;
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read the game's cursor: {e.Message}");
        }

        if (texture == null)
        {
            texture = Fallback();
            hotspot = new Vector2(texture.width / 2f, texture.height / 2f);
        }

        _image.texture = texture;
        _rect.sizeDelta = new Vector2(texture.width, texture.height);
        // Cursor hotspots are measured from the top-left; UI pivots from the bottom-left.
        _rect.pivot = new Vector2(hotspot.x / texture.width, 1f - hotspot.y / texture.height);
        Plugin.Logger.LogInfo($"VR cursor image: type {type}, {texture.width}x{texture.height}, hotspot {hotspot}.");
    }

    /// <summary>A white dot with a dark outline, used when the game's cursor data isn't available.</summary>
    private Texture2D Fallback()
    {
        if (_fallback != null)
            return _fallback;

        const int size = 24;
        var pixels = new Color32[size * size];
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * size + x] = d <= 6f ? new Color32 { r = 255, g = 255, b = 255, a = 255 }
                    : d <= 9f ? new Color32 { r = 0, g = 0, b = 0, a = 255 }
                    : new Color32 { r = 0, g = 0, b = 0, a = 0 };
            }
        }

        _fallback = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        _fallback.SetPixels32(pixels);
        _fallback.Apply(false, false);
        return _fallback;
    }

    /// <summary>Mouse position in Unity screen pixels (origin bottom-left), if it is inside the game window.</summary>
    private bool TryGetMousePosition(out Vector2 position)
    {
        position = default;
        if (_window == IntPtr.Zero)
        {
            // Not Process.MainWindowHandle: with the BepInEx console open that can be the console window.
            _windowLookupCooldown -= Time.unscaledDeltaTime;
            if (_windowLookupCooldown > 0f)
                return false;
            _windowLookupCooldown = 1f;
            _window = FindUnityWindow();
            if (_window == IntPtr.Zero)
                return false;
        }

        if (!GetCursorPos(out POINT p) || !ScreenToClient(_window, ref p) || !GetClientRect(_window, out RECT r))
            return false;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0 || p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h)
            return false;

        position = new Vector2(p.X * (Screen.width / (float)w), Screen.height - p.Y * (Screen.height / (float)h));
        return true;
    }

    private static IntPtr FindUnityWindow()
    {
        uint pid = (uint)Environment.ProcessId;
        IntPtr found = IntPtr.Zero;
        var className = new StringBuilder(64);
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != pid)
                return true;
            className.Clear();
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() != "UnityWndClass")
                return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
            Plugin.Logger.LogInfo("Found the game window for the VR cursor.");
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
}
