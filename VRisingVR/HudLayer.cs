using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// Shows only the game's screen-space UI (HUD, pause menu, map, inventory) in VR, as a transparent
/// panel over the tabletop view.
///
/// All of V Rising's UI is UGUI "Screen Space - Overlay", which the eye cameras never draw. A hidden
/// helper camera with an HDRP customRender callback (HDRP skips its own pipeline for it) clears a
/// screen-sized texture to transparent and re-draws the overlay canvases into it with
/// ScriptableRenderContext.DrawUIOverlay, the same call HDRP uses to render UI into its own buffer.
/// The desktop UI is untouched.
/// </summary>
internal sealed class HudLayer
{
    private readonly OverlayPanel _panel = new("vrisingvr.hud", "V Rising HUD", opaque: false, sortOrder: 1);

    private GameObject _cameraObject;
    private Camera _camera;
    private HDAdditionalCameraData _cameraData;
    private CommandBuffer _commands;
    // Kept in a field so the converted IL2CPP delegate isn't collected while HDRP holds it.
    private Il2CppSystem.Action<ScriptableRenderContext, HDCamera> _callback;

    private RenderTexture _target;
    private IntPtr _targetNative;
    private bool _haveFrame;
    private bool _loggedFirstFrame;
    private Vector3 _smoothedDirection;
    private float _smoothedDistance;
    private bool _haveSmoothed;
    private float _placedDistance;
    private Vector3 _placedOrigin;
    private bool _callbackErrorLogged;

    // The game's 2D overhead layer (CharacterHUDsCanvas) while the health bars are drawn in 3D (DrawOverlayUI).
    private const int OverheadDisplay = 7; // a display nothing renders
    private const int BlankWidth = 1024, BlankHeight = 256;
    private const int AtlasWidth = 1024, AtlasHeight = 1024;
    private OverheadHudMode _overheadMode = OverheadHudMode.Keep;
    private GameHud _overheadSource;
    private bool _overheadFailed;
    private GameObject _overheadCameraObject;
    private Camera _overheadCamera; // disabled: DrawUIOverlay only reads its targetDisplay
    private RenderTexture _blank;   // transparent pixels, copied over the 2D entries that are drawn in 3D
    private RenderTexture _atlas;   // the 2D entries' own pixels, which the 3D entries show
    private CommandBuffer _cutCommands;
    private readonly List<int> _packOrder = new();

    public bool Visible => _panel.Visible;

    /// <summary>
    /// What the VR HUD shows of the game's overhead layer this frame, with <paramref name="source"/> giving the
    /// canvas and the bars drawn in 3D. The desktop is never changed.
    /// </summary>
    public void SetOverhead(OverheadHudMode mode, GameHud source)
    {
        _overheadMode = _overheadFailed || source == null ? OverheadHudMode.Keep : mode;
        _overheadSource = source;
        // Created here (LateUpdate), not inside the render callback.
        if (_overheadMode == OverheadHudMode.CutBars)
            EnsureOverheadResources();
    }

    /// <summary>The UI-only texture (for 3D captures).</summary>
    public RenderTexture Target => _target;

    /// <summary>Snap to the next anchor instead of gliding there (after a recenter).</summary>
    public void RequestPlacement()
    {
        _panel.RequestPlacement();
        _haveSmoothed = false;
    }

    /// <summary>
    /// Call once per frame from LateUpdate while VR is running. The anchor says where the HUD's center
    /// should be (normally the direction to the character's feet) and how far away (pulled closer when
    /// its lowest edge would reach the ground). The panel glides to it rather than jumping.
    /// </summary>
    public void Tick(bool show, HudAnchor anchor)
    {
        if (!show)
        {
            if (_panel.Visible)
                _panel.Hide();
            SetCameraEnabled(false);
            _haveFrame = false;
            // When it comes back, start at the anchor instead of sliding over from where it was.
            _haveSmoothed = false;
            return;
        }

        if (!EnsureCamera())
            return;
        // Unity renders into render textures bottom-up on D3D (like the eyes), so flip V.
        if (!_panel.EnsureCreated(Settings.HudWidth.Value, flipVertical: true))
            return;

        if (anchor.Valid)
        {
            // A new origin (a recenter) snaps rather than gliding from the old layout.
            if (_haveSmoothed && (_placedOrigin - anchor.Origin).sqrMagnitude > 1e-6f)
                _haveSmoothed = false;
            if (!_haveSmoothed)
            {
                _smoothedDirection = anchor.Direction;
                _smoothedDistance = anchor.Distance;
                _haveSmoothed = true;
            }
            else
            {
                float k = 1f - MathF.Exp(-6f * Time.unscaledDeltaTime);
                _smoothedDirection = (_smoothedDirection + (anchor.Direction - _smoothedDirection) * k).normalized;
                _smoothedDistance += (anchor.Distance - _smoothedDistance) * k;
                // The ground limit shrinks as you zoom in; never lag behind it into the ground.
                _smoothedDistance = MathF.Min(_smoothedDistance, anchor.Distance);
            }

            // Placed every frame (as Valve's own overlay helper does): skipping small moves turned the
            // start and end of each glide into visible steps.
            _panel.PlaceAlong(anchor.Origin, _smoothedDirection, _smoothedDistance);
            _placedDistance = _smoothedDistance;
            _placedOrigin = anchor.Origin;
        }
        if (!_panel.Placed)
            return; // no anchor yet
        // The width scales with the distance, so the HUD keeps its size in view when the ground limit
        // pulls it closer (the ground limit counts on this).
        _panel.SetWidth(Settings.HudWidth.Value * _placedDistance / MathF.Max(Settings.HudDistance.Value, 0.05f));
        if (!_panel.Visible)
            _panel.Show();

        EnsureTarget();
        SetCameraEnabled(true);

        // The UI was drawn into the target during last frame's HDRP render, which the render thread
        // has executed by the time it reaches this event.
        if (_haveFrame)
        {
            _panel.SetTexture(_targetNative);
            _haveFrame = false;
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Plugin.Logger.LogInfo($"HUD layer receiving frames ({_target.width}x{_target.height}).");
            }
        }
    }

    public void Destroy()
    {
        _panel.Destroy();
        if (_cameraData != null && _callback != null)
            _cameraData.remove_customRender(_callback);
        if (_cameraObject != null)
            UnityEngine.Object.Destroy(_cameraObject);
        if (_target != null)
        {
            _target.Release();
            UnityEngine.Object.Destroy(_target);
        }
        _commands?.Release();
        if (_overheadCameraObject != null)
            UnityEngine.Object.Destroy(_overheadCameraObject);
        if (_blank != null)
        {
            _blank.Release();
            UnityEngine.Object.Destroy(_blank);
        }
        if (_atlas != null)
        {
            _atlas.Release();
            UnityEngine.Object.Destroy(_atlas);
        }
        _cutCommands?.Release();
        _overheadCameraObject = null;
        _overheadCamera = null;
        _blank = null;
        _atlas = null;
        _cutCommands = null;

        _cameraObject = null;
        _camera = null;
        _cameraData = null;
        _callback = null;
        _commands = null;
        _target = null;
        _targetNative = IntPtr.Zero;
        _haveFrame = false;
        _loggedFirstFrame = false;
        _haveSmoothed = false;
    }

    private bool EnsureCamera()
    {
        if (_camera != null)
            return true;

        _cameraObject = new GameObject("VRisingVR_HudCamera");
        UnityEngine.Object.DontDestroyOnLoad(_cameraObject);
        _camera = _cameraObject.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.cullingMask = 0;
        _camera.clearFlags = CameraClearFlags.Nothing;
        _camera.depth = -100f;

        _camera.eventMask = 0;      // never receives UI or mouse events
        _camera.targetDisplay = 0;  // DrawUIOverlay draws the canvases of this display (all game canvases use 0)

        _cameraData = _cameraObject.GetComponent<HDAdditionalCameraData>();
        if (_cameraData == null)
            _cameraData = _cameraObject.AddComponent<HDAdditionalCameraData>();
        // HDRP still runs HDCamera.Update for custom-render cameras; keep it from evaluating volumes or
        // allocating screen-sized history buffers for features this camera never uses.
        _cameraData.volumeLayerMask = 0;
        _cameraData.hasPersistentHistory = true;
        _cameraData.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
        FrameSettings fs = _cameraData.m_RenderingPathCustomFrameSettings;
        FrameSettingsOverrideMask mask = default;
        foreach (FrameSettingsField field in new[]
                 {
                     FrameSettingsField.Volumetrics, FrameSettingsField.ReprojectionForVolumetrics, FrameSettingsField.SSR,
                     FrameSettingsField.SSGI, FrameSettingsField.Refraction, FrameSettingsField.Distortion,
                     FrameSettingsField.Water, FrameSettingsField.MotionVectors, FrameSettingsField.Postprocess,
                 })
            VRRig.SetFrameSetting(ref fs, ref mask, field, false);
        _cameraData.m_RenderingPathCustomFrameSettings = fs;
        _cameraData.renderingPathCustomFrameSettingsOverrideMask = mask;
        _cameraData.customRenderingSettings = true;

        _commands = new CommandBuffer { name = "VRisingVR HUD" };
        _callback = (Action<ScriptableRenderContext, HDCamera>)OnCustomRender;
        _cameraData.add_customRender(_callback);
        Plugin.Logger.LogInfo("HUD layer camera created.");
        return true;
    }

    private void SetCameraEnabled(bool enabled)
    {
        if (_camera != null && _camera.enabled != enabled)
            _camera.enabled = enabled;
    }

    private void EnsureTarget()
    {
        // Same size as the window, so the UI lays out exactly as on the desktop.
        int w = Screen.width;
        int h = Screen.height;
        if (_target != null && _target.IsCreated() && _target.width == w && _target.height == h)
            return;

        if (_target != null)
        {
            _target.Release();
            UnityEngine.Object.Destroy(_target);
        }

        // 24-bit depth gives a depth-stencil buffer: UGUI Masks clip with the stencil, and without one
        // masked images (e.g. the day/night indicator) draw in full as big squares.
        _target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "VRisingVR_HUD",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        _target.Create();
        _targetNative = _target.GetNativeTexturePtr();
        _camera.targetTexture = _target;
        _haveFrame = false;
    }

    /// <summary>
    /// DrawUIOverlay draws the overlay canvases of the given camera's display (0, like all of the game's), into
    /// the bound texture, when the context is submitted. While the health bars are drawn in 3D, the game's
    /// overhead layer (CharacterHUDsCanvas, the bottom-most canvas) is moved to another display for this draw:
    /// Canvas.targetDisplay only stores a byte, with no rebuild. With CutBars it's drawn on its own first (via a
    /// camera on that display), the 2D entries now drawn in 3D (bars, names, levels) are blanked out with exact
    /// pixel copies, and the other canvases go on top. HDRP only submits after this callback, so it's submitted
    /// here before the canvas is moved back; the desktop's own UI draw comes later in the frame.
    /// </summary>
    private void DrawOverlayUI(ScriptableRenderContext context)
    {
        OverheadHudMode mode = _overheadMode;
        GameHud hud = _overheadSource;
        Canvas overhead = mode != OverheadHudMode.Keep && hud != null && hud.Refresh() ? hud.OverheadCanvas : null;
        if (overhead == null || overhead.targetDisplay != 0)
        {
            context.DrawUIOverlay(_camera);
            return;
        }

        bool cut = mode == OverheadHudMode.CutBars && _overheadCamera != null && _blank != null && _blank.IsCreated()
                   && _atlas != null && _atlas.IsCreated() && _cutCommands != null;
        bool moved = false;
        try
        {
            overhead.targetDisplay = OverheadDisplay;
            moved = true;
            if (cut)
            {
                context.DrawUIOverlay(_overheadCamera);
                RecordCuts(hud);
                context.ExecuteCommandBuffer(_cutCommands);
                _cutCommands.Clear();
            }
            context.DrawUIOverlay(_camera);
            context.Submit();
        }
        catch (Exception e)
        {
            // From now on the VR HUD shows the overhead layer as the desktop does.
            _overheadFailed = true;
            _overheadMode = OverheadHudMode.Keep;
            Plugin.Logger.LogError($"HUD layer: handling the 2D overhead bars failed, so the VR HUD shows them again: {e}");
            throw;
        }
        finally
        {
            if (moved)
            {
                try { overhead.targetDisplay = 0; }
                catch (Exception e) { Plugin.Logger.LogError($"HUD layer: moving the overhead canvas back failed: {e.Message}"); }
            }
        }
    }

    /// <summary>
    /// Copy each element of the 2D entries that's drawn in 3D (bars, names, level badges, icons) into the atlas
    /// (so the 3D entry shows the game's own pixels), then blank it: copy transparent pixels over its screen
    /// rectangle, in tiles of the blank texture's size. The overlay canvas's world space is screen pixels from the
    /// bottom-left, the same origin CopyTexture uses. An element that runs off the screen isn't copied, nor is a
    /// bar core that overlaps another character's core (its copy would carry the other's fill); WorldBars draws
    /// those bars plain, so bar pieces are blanked anyway. Labels and glow pieces are copied even where entries
    /// overlap, as the desktop shows them; a label that can't be copied stays on the flat HUD.
    /// </summary>
    private void RecordCuts(GameHud hud)
    {
        const int gap = 2; // between atlas entries, so filtering never bleeds into a neighbour
        List<GameHud.Cut> cuts = hud.Cuts;
        _cutCommands.Clear();
        var blank = new RenderTargetIdentifier(_blank);
        var atlas = new RenderTargetIdentifier(_atlas);
        var target = new RenderTargetIdentifier(_target);
        _cutCommands.SetRenderTarget(blank);
        _cutCommands.ClearRenderTarget(true, true, Color.clear);
        _cutCommands.SetRenderTarget(atlas);
        _cutCommands.ClearRenderTarget(false, true, Color.clear);
        int w = _target.width, h = _target.height;

        // 1) the pixels, packed in rows, tallest first (rows waste less space)
        _packOrder.Clear();
        for (int i = 0; i < cuts.Count; i++)
        {
            float height = cuts[i].Max.Y - cuts[i].Min.Y;
            int at = _packOrder.Count;
            while (at > 0 && cuts[_packOrder[at - 1]].Max.Y - cuts[_packOrder[at - 1]].Min.Y < height)
                at--;
            _packOrder.Insert(at, i);
        }
        int ax = 0, ay = 0, rowHeight = 0;
        foreach (int i in _packOrder)
        {
            GameHud.Cut c = cuts[i];
            c.InAtlas = false;
            bool onScreen = c.Min.X >= 0f && c.Min.Y >= 0f && c.Max.X <= w && c.Max.Y <= h;
            Bounds(c, w, h, out int x0, out int y0, out int x1, out int y1);
            int cw = x1 - x0, ch = y1 - y0;
            if (onScreen && (!c.IsCore || !OverlapsOtherCore(cuts, i)) && cw > 0 && ch > 0 && cw <= AtlasWidth)
            {
                if (ax + cw > AtlasWidth)
                {
                    ax = 0;
                    ay += rowHeight + gap;
                    rowHeight = 0;
                }
                if (ay + ch <= AtlasHeight)
                {
                    _cutCommands.CopyTexture(target, 0, 0, x0, y0, cw, ch, atlas, 0, 0, ax, ay);
                    // Screen pixel P is atlas pixel P - (x0, y0) + (ax, ay).
                    var shift = new System.Numerics.Vector2(ax - x0, ay - y0);
                    var size = new System.Numerics.Vector2(AtlasWidth, AtlasHeight);
                    c.UV0 = (c.Q0 + shift) / size;
                    c.UV1 = (c.Q1 + shift) / size;
                    c.UV2 = (c.Q2 + shift) / size;
                    c.UV3 = (c.Q3 + shift) / size;
                    c.InAtlas = true;
                    ax += cw + gap;
                    rowHeight = Math.Max(rowHeight, ch);
                }
            }
            cuts[i] = c;
        }
        hud.Atlas = _atlas;
        hud.AtlasFrame = Time.frameCount;

        // 2) blank them on the HUD (exactly the rectangles that were copied, or would have been): every bar, which
        // is drawn plain if it wasn't copied, and the labels that were copied
        foreach (GameHud.Cut c in cuts)
        {
            if (!c.IsBar && !c.InAtlas)
                continue;
            Bounds(c, w, h, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y < y1; y += BlankHeight)
            {
                for (int x = x0; x < x1; x += BlankWidth)
                {
                    int cw = Math.Min(BlankWidth, x1 - x);
                    int ch = Math.Min(BlankHeight, y1 - y);
                    _cutCommands.CopyTexture(blank, 0, 0, 0, 0, cw, ch, target, 0, 0, x, y);
                }
            }
        }
        // Back to the HUD texture for the other canvases.
        _cutCommands.SetRenderTarget(target);
    }

    /// <summary>The cut's bounding box in whole pixels, clipped to the screen.</summary>
    private static void Bounds(GameHud.Cut c, int w, int h, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Max(0, (int)MathF.Floor(c.Min.X));
        y0 = Math.Max(0, (int)MathF.Floor(c.Min.Y));
        x1 = Math.Min(w, (int)MathF.Ceiling(c.Max.X));
        y1 = Math.Min(h, (int)MathF.Ceiling(c.Max.Y));
    }

    /// <summary>
    /// Whether cut <paramref name="index"/>'s (rolled) box overlaps another character's bar core. As in v0.11, only
    /// another bar refuses a core: names, icons and glows in a copy only blur it, another bar would mix fills.
    /// </summary>
    private static bool OverlapsOtherCore(List<GameHud.Cut> cuts, int index)
    {
        GameHud.Cut a = cuts[index];
        for (int j = 0; j < cuts.Count; j++)
        {
            GameHud.Cut b = cuts[j];
            if (j == index || !b.IsCore || b.Owner == a.Owner)
                continue;
            if (GameHud.Intersects(a, b))
                return true;
        }
        return false;
    }

    private void EnsureOverheadResources()
    {
        if (_overheadCamera == null)
        {
            _overheadCameraObject = new GameObject("VRisingVR_OverheadHudCamera");
            UnityEngine.Object.DontDestroyOnLoad(_overheadCameraObject);
            _overheadCamera = _overheadCameraObject.AddComponent<Camera>();
            _overheadCamera.enabled = false;
            _overheadCamera.cullingMask = 0;
            _overheadCamera.eventMask = 0;
            _overheadCamera.targetDisplay = OverheadDisplay;
        }
        if (_blank == null || !_blank.IsCreated())
        {
            if (_blank != null)
            {
                _blank.Release();
                UnityEngine.Object.Destroy(_blank);
            }
            // Same color format as the HUD texture (CopyTexture needs it); no depth buffer.
            _blank = new RenderTexture(BlankWidth, BlankHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "VRisingVR_HudBlank",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            _blank.Create();
        }
        if (_atlas == null || !_atlas.IsCreated())
        {
            if (_atlas != null)
            {
                _atlas.Release();
                UnityEngine.Object.Destroy(_atlas);
            }
            _atlas = new RenderTexture(AtlasWidth, AtlasHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "VRisingVR_BarAtlas",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            _atlas.Create();
        }
        _cutCommands ??= new CommandBuffer { name = "VRisingVR HUD cut-outs" };
    }

    /// <summary>Called by HDRP on the main thread, in place of rendering the helper camera.</summary>
    private void OnCustomRender(ScriptableRenderContext context, HDCamera hdCamera)
    {
        try
        {
            if (_target == null || _camera == null)
                return;

            _commands.Clear();
            _commands.SetRenderTarget(new RenderTargetIdentifier(_target));
            _commands.ClearRenderTarget(true, true, Color.clear);
            context.ExecuteCommandBuffer(_commands);
            _commands.Clear();

            DrawOverlayUI(context);
            _haveFrame = true;
        }
        catch (Exception e)
        {
            if (!_callbackErrorLogged)
            {
                _callbackErrorLogged = true;
                Plugin.Logger.LogError($"HUD layer render failed: {e}");
            }
        }
    }
}

/// <summary>Where the HUD layer should sit, in OpenVR tracking space.</summary>
internal struct HudAnchor
{
    public bool Valid;
    /// <summary>The eye position the HUD is laid out from.</summary>
    public Vector3 Origin;
    /// <summary>Unit vector from the eyes to the HUD's center.</summary>
    public Vector3 Direction;
    /// <summary>Meters from the eyes to the HUD.</summary>
    public float Distance;
}
