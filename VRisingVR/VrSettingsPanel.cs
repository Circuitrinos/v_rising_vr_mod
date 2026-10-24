using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace VRisingVR;

/// <summary>
/// In-game VR settings panel with sliders, toggled with a key and used with the mouse. It is
/// screen-space UI, so it shows on the desktop and, through the HUD layer and the virtual screen, in VR.
/// Values apply live and are saved to the config file when the panel closes.
/// </summary>
internal sealed class VrSettingsPanel
{
    private sealed class Row
    {
        public ConfigEntry<float> Entry;
        public string Label;
        public string Format;
        public Slider Slider;
        public Text Text;
        public float Min, Max;
        /// <summary>The value last shown on the slider, to tell a drag from a change made elsewhere.</summary>
        public float Shown;
    }

    private readonly List<Row> _rows = new();
    private GameObject _root;
    private Font _font;
    private bool _visible;
    private bool _failed;
    private float _saveAt = -1f;
    private bool _saveHooked;
    /// <summary>A desk window key's row: a button that, once clicked, takes the next key or mouse button pressed.</summary>
    private sealed class KeyRow
    {
        public ConfigEntry<string> Entry;
        public string Label;
        public string ShortName; // in clash messages: "show key"
        public Text Text;
        public string Message;
        public float MessageUntil;
    }

    private readonly List<KeyRow> _keyRows = new();
    private KeyRow _capturing; // the row waiting for its new key, if any

    public bool Visible => _visible;

    public void Toggle()
    {
        if (_failed)
            return;
        try
        {
            if (_visible) Hide();
            else Show();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>Call every frame; applies slider changes to the settings.</summary>
    public void Tick()
    {
        if (!_visible || _failed)
            return;
        try
        {
            foreach (Row row in _rows)
            {
                float value = row.Slider.value;
                if (Mathf.Abs(value - row.Shown) > 1e-4f)
                {
                    // The slider was dragged.
                    row.Entry.Value = value;
                    row.Shown = value;
                }
                else if (Mathf.Abs(row.Entry.Value - row.Shown) > 1e-4f)
                {
                    // Changed elsewhere (F10/F11, the config range): follow it rather than writing the slider back.
                    ShowValue(row);
                }
                if (row.Text != null)
                    row.Text.text = $"{row.Label}: {row.Entry.Value.ToString(row.Format)}";
            }
            TickKeyRows();

            // Edits are saved shortly after they stop, so nothing is lost if the game exits with the panel open.
            if (_saveAt >= 0f && Time.unscaledTime >= _saveAt)
            {
                _saveAt = -1f;
                Settings.File.Save();
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Show()
    {
        EnsureCreated();
        foreach (Row row in _rows)
            ShowValue(row);
        if (!_saveHooked)
        {
            _saveHooked = true;
            Settings.File.SettingChanged += (_, _) =>
            {
                if (_visible)
                    _saveAt = Time.unscaledTime + 0.5f;
            };
        }
        // Save after a short pause rather than on every slider step.
        Settings.File.SaveOnConfigSet = false;
        _root.SetActive(true);
        _visible = true;
        Plugin.Logger.LogInfo($"VR settings panel opened ({Settings.SettingsKey.Value} closes it).");
    }

    private void Hide()
    {
        if (_root != null)
            _root.SetActive(false);
        _visible = false;
        _capturing = null;
        _saveAt = -1f;
        Settings.File.Save();
        Settings.File.SaveOnConfigSet = true;
        Plugin.Logger.LogInfo("VR settings panel closed; settings saved.");
    }

    private void Fail(Exception e)
    {
        _failed = true;
        Plugin.Logger.LogError($"VR settings panel disabled after an error: {e}");
        try { if (_root != null) _root.SetActive(false); } catch { }
        try { Settings.File.Save(); } catch { }
        Settings.File.SaveOnConfigSet = true;
    }

    /// <summary>Save pending edits (the game is quitting with the panel open).</summary>
    public void SaveIfOpen()
    {
        if (_visible)
        {
            try { Settings.File.Save(); } catch { }
        }
    }

    /// <summary>Show the entry's value without the slider clamping it back into the setting.</summary>
    private static void ShowValue(Row row)
    {
        float value = row.Entry.Value;
        row.Slider.minValue = Mathf.Min(row.Min, value);
        row.Slider.maxValue = Mathf.Max(row.Max, value);
        row.Slider.SetValueWithoutNotify(value);
        row.Shown = row.Slider.value;
    }

    private void EnsureCreated()
    {
        if (_root != null)
            return;

        _font = UiFont.Get();
        _root = new GameObject("VRisingVR_SettingsPanel");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue - 1; // above the game UI, below the VR cursor
        // Sizes below are in 1080p pixels; scaled to the window's height, the panel (nearly 1080 tall) always fits.
        CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;
        _root.AddComponent<GraphicRaycaster>();

        var rows = new List<(ConfigEntry<float> entry, string label, float min, float max, string format)>
        {
            (Settings.HudDistance, "HUD distance (m)", 0.4f, 3f, "0.00"),
            (Settings.HudWidth, "HUD width (m)", 0.4f, 3f, "0.00"),
            (Settings.HudPitchOffset, "HUD tilt offset (degrees)", -40f, 40f, "0"),
            (Settings.HudGroundMargin, "HUD ground margin", 0.2f, 1f, "0.00"),
            (Settings.ReticleSize, "Ground cursor size (game m)", 0.1f, 1.5f, "0.00"),
            (Settings.BarWidthMeters, "Health bar width (m)", 0.02f, 0.3f, "0.00"),
            (Settings.WorldScale, "World scale", 1f, 30f, "0.0"),
            (Settings.NearClipMeters, "Near clip (real m)", 0.02f, 1.5f, "0.00"),
            (Settings.StereoStrength, "Stereo strength", 0f, 1.5f, "0.00"),
            (Settings.HeightOffset, "View height (game m)", -20f, 20f, "0.0"),
            (Settings.ScreenDistance, "Menu screen distance (m)", 1f, 5f, "0.0"),
            // The window onto your desk shows while this panel is open, so these can line it up with the keyboard.
            (Settings.DeskForward, "Desk window ahead (m)", 0.1f, 1.5f, "0.00"),
            (Settings.DeskRight, "Desk window right (m)", -1f, 1f, "0.00"),
            (Settings.DeskBelowEyes, "Desk below your eyes (m)", 0.1f, 1.2f, "0.00"),
            (Settings.DeskWidth, "Desk window width (m)", 0.2f, 2f, "0.00"),
            (Settings.DeskDepth, "Desk window depth (m)", 0.1f, 1f, "0.00"),
            (Settings.DeskYaw, "Desk window turn (degrees)", -180f, 180f, "0"),
            (Settings.DeskLookDownAngle, "Desk look-down (degrees)", 10f, 85f, "0"),
        };

        const float width = 680f;
        // The desk window's two button rows first, then the sliders, short enough that the panel fits 1080 (scaled) pixels.
        const int buttonRows = 2;
        int rowCount = rows.Count + buttonRows;
        float rowHeight = Mathf.Min(64f, 950f / rowCount);
        float height = 90f + rowCount * rowHeight + 20f;

        RectTransform panel = CreateRect("Panel", _root.transform, new Vector2(width, height));
        Image background = panel.gameObject.AddComponent<Image>();
        background.color = new Color(0.05f, 0.05f, 0.08f, 0.9f);

        CreateText(panel, $"VR settings  ({Settings.SettingsKey.Value} closes)", new Vector2(0f, height / 2f - 40f),
            new Vector2(width - 40f, 44f), 26);
        float firstRowY = height / 2f - 90f - rowHeight / 2f;
        AddKeyRow(panel, firstRowY, width - 40f, rowHeight - 12f, Settings.DeskKey, "Show the desk window", "show key");
        AddKeyRow(panel, firstRowY - rowHeight, width - 40f, rowHeight - 12f, Settings.DeskMoveKey, "Show and move it with the mouse", "move key");

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            float y = firstRowY - (i + buttonRows) * rowHeight;
            Text label = CreateText(panel, row.label, new Vector2(-width / 4f + 10f, y), new Vector2(width / 2f - 20f, rowHeight), 20);

            GameObject sliderObject = DefaultControls.CreateSlider(new DefaultControls.Resources());
            sliderObject.transform.SetParent(panel, false);
            RectTransform sliderRect = sliderObject.GetComponent<RectTransform>();
            sliderRect.anchoredPosition = new Vector2(width / 4f, y);
            sliderRect.sizeDelta = new Vector2(width / 2f - 40f, 26f);
            Slider slider = sliderObject.GetComponent<Slider>();
            slider.minValue = row.min;
            slider.maxValue = row.max;

            var entryRow = new Row { Entry = row.entry, Label = row.label, Format = row.format, Slider = slider, Text = label, Min = row.min, Max = row.max };
            ShowValue(entryRow);
            _rows.Add(entryRow);
        }

        _root.SetActive(false);
    }

    /// <summary>
    /// A full-width button with a text label. Returns false if the click can't be hooked up (the label still shows),
    /// so the rest of the panel keeps working.
    /// </summary>
    private bool CreateButtonRow(RectTransform panel, float y, float width, float height, Action onClick, out Text label)
    {
        GameObject buttonObject = DefaultControls.CreateButton(new DefaultControls.Resources());
        buttonObject.transform.SetParent(panel, false);
        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(width, height);
        buttonObject.GetComponent<Image>().color = new Color(0.2f, 0.22f, 0.3f, 1f);
        label = buttonObject.GetComponentInChildren<Text>();
        if (_font != null)
            label.font = _font;
        label.fontSize = 20;
        label.color = Color.white;
        try
        {
            buttonObject.GetComponent<Button>().onClick.AddListener((UnityAction)onClick);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"A VR settings panel button doesn't respond to clicks ({e.Message}).");
            return false;
        }
    }

    private void AddKeyRow(RectTransform panel, float y, float width, float height, ConfigEntry<string> entry, string label, string shortName)
    {
        var row = new KeyRow { Entry = entry, Label = label, ShortName = shortName };
        if (!CreateButtonRow(panel, y, width, height, () => StartCapture(row), out row.Text))
        {
            row.Message = $"{label}: change it in the config file (DeskWindow.{entry.Definition.Key})";
            row.MessageUntil = float.MaxValue;
        }
        _keyRows.Add(row);
    }

    private void StartCapture(KeyRow row)
    {
        Hotkeys.BeginCapture();
        _capturing = row;
    }

    private void TickKeyRows()
    {
        if (_capturing != null)
        {
            if (Hotkeys.EscapePressed())
                _capturing = null;
            else if (Hotkeys.CapturePress() is string binding)
                SetKey(_capturing, binding);
        }
        foreach (KeyRow row in _keyRows)
        {
            if (row.Text == null)
                continue;
            row.Text.text = row == _capturing ? "Press the new key or mouse button   (Esc cancels)"
                : Time.unscaledTime < row.MessageUntil ? row.Message
                : $"{row.Label}: {Hotkeys.DisplayName(row.Entry.Value)}   (click to change)";
        }
    }

    private void SetKey(KeyRow row, string binding)
    {
        _capturing = null;
        string name = Hotkeys.DisplayName(binding);
        string clash = KeyUsing(row, binding);
        if (clash != null)
        {
            ShowMessage(row, $"{name} is already the {clash}. Click to try another.");
            return;
        }
        row.Entry.Value = binding;
        ShowMessage(row, $"Set to {name}. Check the game doesn't use it.");
        Plugin.Logger.LogInfo($"DeskWindow.{row.Entry.Definition.Key} set to {binding}.");
    }

    private static void ShowMessage(KeyRow row, string message)
    {
        row.Message = message;
        row.MessageUntil = Time.unscaledTime + 4f;
    }

    /// <summary>Which of the mod's hotkeys, or the other desk window key, the binding would collide with, or null.</summary>
    private string KeyUsing(KeyRow row, string binding)
    {
        int vk = Hotkeys.ToVirtualKey(binding);
        foreach (KeyRow other in _keyRows)
        {
            if (other != row && Hotkeys.Uses(other.Entry.Value, vk))
                return other.ShortName;
        }
        foreach ((ConfigEntry<Key> entry, string what) in new[]
                 {
                     (Settings.SettingsKey, "settings panel"), (Settings.ToggleKey, "VR on/off"), (Settings.RecenterKey, "recenter"),
                     (Settings.ScreenToggleKey, "virtual screen"), (Settings.CaptureKey, "3D capture"),
                     (Settings.IsolationKey, "diagnostics"), (Settings.ScaleDownKey, "world scale"), (Settings.ScaleUpKey, "world scale"),
                 })
        {
            if (Hotkeys.ToVirtualKey(entry.Value) == vk)
                return $"mod's {what} key";
        }
        return null;
    }

    private static RectTransform CreateRect(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = size;
        return rect;
    }

    private Text CreateText(Transform parent, string content, Vector2 position, Vector2 size, int fontSize)
    {
        RectTransform rect = CreateRect("Text", parent, size);
        rect.anchoredPosition = position;
        Text text = rect.gameObject.AddComponent<Text>();
        if (_font != null)
            text.font = _font;
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        text.text = content;
        return text;
    }
}
