using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace VRisingVR;

/// <summary>
/// Saves the game's own desktop render while VR runs: a customRender callback on the game camera makes HDRP
/// skip its whole pipeline for that camera (as for the HUD layer's helper camera), and the callback instead
/// shows the left eye in the window, with the game's UI drawn on top. The game's 2D layers that are placed
/// for its own camera's view (overhead bars and names, combat text) are left out there: they wouldn't line
/// up with the eye image, which already shows the 3D bars. The camera itself stays enabled and untouched
/// otherwise, so Camera.main, mouse aiming and the game's camera systems are unaffected. Detaching restores
/// the normal render immediately.
/// </summary>
internal sealed class DesktopMirror
{
    private const int HiddenDisplay = 7; // a display nothing renders

    private HDAdditionalCameraData _attached;
    private Camera _camera;
    // Kept in a field so the converted IL2CPP delegate isn't collected while HDRP holds it.
    private Il2CppSystem.Action<ScriptableRenderContext, HDCamera> _callback;
    private CommandBuffer _commands;
    private RenderTexture _source;
    private GameHud _hud;
    private bool _failed;
    private bool _loggedFirst;
    private bool _errorLogged;

    public bool Active => _attached != null;

    /// <summary>
    /// Call once per frame: mirror <paramref name="eye"/> instead of rendering <paramref name="game"/> when
    /// wanted. <paramref name="hud"/> gives the camera-placed 2D canvases to leave out.
    /// </summary>
    public void Tick(bool want, Camera game, RenderTexture eye, GameHud hud)
    {
        if (!want || _failed || game == null || eye == null)
        {
            Detach();
            return;
        }
        _source = eye;
        _hud = hud;
        if (_attached != null && _camera == game)
            return;

        Detach();
        try
        {
            HDAdditionalCameraData hd = game.GetComponent<HDAdditionalCameraData>();
            if (hd == null)
                return;
            _commands ??= new CommandBuffer { name = "VRisingVR desktop mirror" };
            _callback ??= (Action<ScriptableRenderContext, HDCamera>)OnCustomRender;
            hd.add_customRender(_callback);
            _attached = hd;
            _camera = game;
            Plugin.Logger.LogInfo("Desktop view: showing the left eye; the game's own desktop render is skipped while VR runs.");
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Logger.LogError($"Desktop mirror disabled (the desktop renders normally): {e.Message}");
        }
    }

    public void Detach()
    {
        if (_attached == null)
            return;
        try
        {
            // Unity's != compares the native objects: skip a camera that's already gone.
            if (_attached != null && _callback != null)
                _attached.remove_customRender(_callback);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Removing the desktop mirror failed: {e.Message}");
        }
        _attached = null;
        _camera = null;
        Plugin.Logger.LogInfo("Desktop view: the game renders it again.");
    }

    public void Destroy()
    {
        Detach();
        _commands?.Release();
        _commands = null;
        _callback = null;
        _source = null;
        _hud = null;
    }

    /// <summary>
    /// The game's UI, without the canvases placed for its own camera's view: they're moved to an unused display
    /// for this draw, as HudLayer does. The overlay draw runs when the context is submitted, which HDRP only
    /// does after this callback, so it's submitted here before they're moved back.
    /// </summary>
    private void DrawUI(ScriptableRenderContext context, Camera camera)
    {
        Canvas overhead = _hud != null && _hud.Refresh() ? _hud.OverheadCanvas : null;
        Canvas sct = _hud?.SctCanvas;
        bool movedOverhead = false, movedSct = false;
        try
        {
            if (overhead != null && overhead.targetDisplay == 0)
            {
                overhead.targetDisplay = HiddenDisplay;
                movedOverhead = true;
            }
            if (sct != null && sct.targetDisplay == 0)
            {
                sct.targetDisplay = HiddenDisplay;
                movedSct = true;
            }
            context.DrawUIOverlay(camera);
            if (movedOverhead || movedSct)
                context.Submit();
        }
        finally
        {
            if (movedOverhead)
                overhead.targetDisplay = 0;
            if (movedSct)
                sct.targetDisplay = 0;
        }
    }

    /// <summary>Called by HDRP on the main thread, in place of rendering the game camera.</summary>
    private void OnCustomRender(ScriptableRenderContext context, HDCamera hdCamera)
    {
        try
        {
            Camera camera = hdCamera.camera;
            context.SetupCameraProperties(camera);
            _commands.Clear();
            var screen = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            _commands.SetRenderTarget(screen);
            _commands.ClearRenderTarget(true, true, Color.black);
            if (_source != null && _source.IsCreated())
            {
                // The middle of the (square) eye image, filling the window's width.
                float aspect = (float)Mathf.Max(camera.pixelWidth, 1) / Mathf.Max(camera.pixelHeight, 1);
                float sourceAspect = (float)_source.width / Mathf.Max(_source.height, 1);
                float v = Mathf.Clamp01(sourceAspect / aspect);
                Vector2 scale = default, offset = default;
                scale.x = 1f;
                scale.y = v;
                offset.y = 0.5f * (1f - v);
                _commands.Blit(_source, screen, scale, offset);
            }
            context.ExecuteCommandBuffer(_commands);
            _commands.Clear();
            // HDRP normally draws the screen-space UI inside the camera's render; draw it ourselves.
            DrawUI(context, camera);
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Logger.LogInfo($"Desktop mirror drawing ({camera.pixelWidth}x{camera.pixelHeight}).");
            }
        }
        catch (Exception e)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                Plugin.Logger.LogError($"Desktop mirror failed; the desktop renders normally again: {e}");
            }
            // The next Tick detaches, so the desktop renders normally from the next frame on.
            _failed = true;
        }
    }
}
