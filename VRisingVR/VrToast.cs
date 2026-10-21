using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace VRisingVR;

/// <summary>Built-in UI font, shared by the mod's runtime UI.</summary>
internal static class UiFont
{
    private static Font _font;
    private static bool _tried;

    public static Font Get()
    {
        if (_tried)
            return _font;
        _tried = true;
        foreach (string name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try
            {
                UnityEngine.Object font = Resources.GetBuiltinResource(Il2CppType.Of<Font>(), name);
                if (font != null)
                    return _font = font.Cast<Font>();
            }
            catch
            {
                // try the next name
            }
        }
        Plugin.Logger.LogWarning("No built-in font found; mod UI text will not be shown.");
        return null;
    }
}

/// <summary>
/// A short message at the top of the screen ("Recentered", "Isolation level 2", ...). It is screen-space
/// UI, so it shows in VR on the HUD layer and the virtual screen, as well as on the desktop.
/// </summary>
internal sealed class VrToast
{
    private GameObject _root;
    private Text _text;
    private float _remaining;
    private bool _failed;

    public void Show(string message, float seconds = 3f)
    {
        if (_failed)
            return;
        try
        {
            EnsureCreated();
            _text.text = message;
            _root.SetActive(true);
            _remaining = seconds;
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Logger.LogError($"VR messages disabled after an error: {e}");
        }
    }

    public void Tick()
    {
        if (_root == null || _remaining <= 0f)
            return;
        _remaining -= Time.unscaledDeltaTime;
        if (_remaining <= 0f)
            _root.SetActive(false);
    }

    private void EnsureCreated()
    {
        if (_root != null)
            return;

        _root = new GameObject("VRisingVR_Toast");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue - 2;

        var panelObject = new GameObject("Panel");
        panelObject.transform.SetParent(_root.transform, false);
        RectTransform panel = panelObject.AddComponent<RectTransform>();
        panel.anchorMin = new Vector2(0.5f, 1f);
        panel.anchorMax = new Vector2(0.5f, 1f);
        panel.pivot = new Vector2(0.5f, 1f);
        panel.anchoredPosition = new Vector2(0f, -140f);
        panel.sizeDelta = new Vector2(1000f, 64f);
        Image background = panelObject.AddComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.7f);
        background.raycastTarget = false;

        var textObject = new GameObject("Text");
        textObject.transform.SetParent(panel, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16f, 0f);
        textRect.offsetMax = new Vector2(-16f, 0f);
        _text = textObject.AddComponent<Text>();
        Font font = UiFont.Get();
        if (font != null)
            _text.font = font;
        _text.fontSize = 28;
        _text.color = Color.white;
        _text.alignment = TextAnchor.MiddleCenter;
        _text.raycastTarget = false;

        _root.SetActive(false);
    }
}
