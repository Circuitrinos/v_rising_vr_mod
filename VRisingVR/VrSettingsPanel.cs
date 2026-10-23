using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
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
        };
        // Only for players who set up the Steam Frame passthrough server.
        if (Settings.PassthroughEnabled.Value)
        {
            rows.Add((Settings.PassthroughOpacity, "Passthrough opacity", 0.1f, 1f, "0.00"));
            rows.Add((Settings.PassthroughLookDownAngle, "Passthrough look-down angle (degrees)", 10f, 85f, "0"));
            rows.Add((Settings.PassthroughFieldOfView, "Passthrough field of view (degrees)", 40f, 160f, "0"));
            rows.Add((Settings.PassthroughVerticalOffset, "Passthrough vertical offset (degrees)", -45f, 45f, "0.0"));
            rows.Add((Settings.PassthroughHorizontalOffset, "Passthrough horizontal offset (degrees)", -45f, 45f, "0.0"));
        }

        const float width = 680f;
        // Shorter rows when there are many, so the panel still fits a 1080p screen.
        float rowHeight = rows.Count > 12 ? 54f : 64f;
        float height = 90f + rows.Count * rowHeight + 20f;

        RectTransform panel = CreateRect("Panel", _root.transform, new Vector2(width, height));
        Image background = panel.gameObject.AddComponent<Image>();
        background.color = new Color(0.05f, 0.05f, 0.08f, 0.9f);

        CreateText(panel, $"VR settings  ({Settings.SettingsKey.Value} closes)", new Vector2(0f, height / 2f - 40f),
            new Vector2(width - 40f, 44f), 26);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            float y = height / 2f - 90f - i * rowHeight - rowHeight / 2f;
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
