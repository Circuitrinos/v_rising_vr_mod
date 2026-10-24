using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// A window onto your real desk, for the Steam Frame with the community chroma-key tool
/// (github.com/spyjacob/steam-frame-chroma-key): a flat rectangle lying over your keyboard and mouse, filled
/// with a pure key colour and drawn into both eye images last. The tool makes the headset show its own
/// passthrough wherever the streamed picture has that colour, so the rectangle becomes a hole through the game
/// onto your hands, keyboard and mouse.
///
/// The rectangle is fixed in the room (SteamVR's tracking space) and drawn from your real eye positions (not the
/// game's scaled ones) with its own near plane, so it lines up with the passthrough whatever WorldScale or
/// StereoStrength is set to. Its offsets are measured from an anchor: where your head was at the last recenter
/// until you move the window (MoveKey and the mouse), then that pose, frozen until VR restarts, so taking the
/// headset off and on (which recenters) doesn't move it off the keyboard. In the tabletop view the board lies in
/// about the same direction as the desk, so by default the window only shows while you hold a key.
/// </summary>
internal sealed class DeskWindow
{
    private const float NearClip = 0.02f; // real meters
    private const float FarClip = 10f;
    /// <summary>LookDown mode: once shown, the window stays until you look this many degrees back above LookDownAngle.</summary>
    private const float Hysteresis = 4f;
    /// <summary>A cursor jump bigger than this (pixels per frame) is the game moving the cursor, not the mouse; it doesn't move the window.</summary>
    private const int MaxCursorStep = 300;
    private const int MaxSamples = 4096;

    private Mesh _mesh;
    private Material _material;
    private Il2CppStructArray<Vector3> _vertices;
    private Matrix4x4 _identity;
    private CommandBuffer _commands;
    private ChromaKeyColor _appliedColor = (ChromaKeyColor)(-1);
    /// <summary>Flipped by each press of the key: shown (Toggle mode), or turned off (LookDown and Always). Reset when the mode changes.</summary>
    private bool _latched;
    private DeskWindowMode _latchMode;
    /// <summary>Set by moving the window: the pose its offsets are measured from, instead of the latest recenter.</summary>
    private bool _anchored;
    private HmdMatrix34_t _anchor;
    // Moving it: the offsets and turn so far and the cursor's last position. The settings are written once, when MoveKey is let go.
    private bool _moving;
    private float _moveForward, _moveRight, _moveYaw;
    private int _cursorX, _cursorY;
    private bool _lookingDown;
    private bool _visible;
    private bool _heldOpen;
    private bool _drawn;
    private bool _failed;

    // Diagnostics for the Perf line: where your head points while the window is hidden and while it shows, and
    // where the board is, to tell whether a look-down angle can separate looking at the board from looking at the desk.
    private readonly List<float> _pitchHidden = new();
    private readonly List<float> _pitchShown = new();
    private float _boardPitch = float.NaN;

    /// <summary>Whether the window is drawn this frame.</summary>
    public bool Visible => _visible;

    /// <summary>Whether it shows because a key is held (a Hold-mode peek, or moving it), so you're looking at the desk.</summary>
    public bool HeldOpen => _visible && _heldOpen;

    /// <summary>The desk window keys as they read in messages.</summary>
    public static string KeyName => Hotkeys.DisplayName(Settings.DeskKey.Value);
    public static string MoveKeyName => Hotkeys.DisplayName(Settings.DeskMoveKey.Value);

    public static string Describe() =>
        $"Desk window: {Settings.DeskMode.Value} mode ({KeyName}; {MoveKeyName} and the mouse move it), " +
        $"{Settings.DeskColor.Value}, {Settings.DeskWidth.Value:0.00} x {Settings.DeskDepth.Value:0.00} m, " +
        $"{Settings.DeskForward.Value:0.00} m ahead, {Settings.DeskRight.Value:0.00} m right and {Settings.DeskBelowEyes.Value:0.00} m " +
        $"below the recentered eyes, turned {Settings.DeskYaw.Value:0} degrees.";

    /// <summary>
    /// Decide whether the window shows this frame and lay it out. <paramref name="head"/> is the current head pose,
    /// <paramref name="recenter"/> the head pose at the last recenter, both in tracking space. While
    /// <paramref name="moveHeld"/>, the window shows and follows the mouse.
    /// </summary>
    public void Update(bool havePose, HmdMatrix34_t head, HmdMatrix34_t recenter, bool keyHeld, bool moveHeld, bool panelOpen, bool inWorld)
    {
        _visible = false;
        _heldOpen = false;
        if (_failed || !havePose)
            return;
        try
        {
            DeskWindowMode mode = Settings.DeskMode.Value;
            if (mode != _latchMode)
            {
                // The latch means "shown" in Toggle mode but "turned off" in the others, so start each mode fresh.
                _latched = false;
                _latchMode = mode;
            }
            if (mode == DeskWindowMode.Off)
                return;
            float pitch = PitchDown(head);
            float angle = Settings.DeskLookDownAngle.Value;
            _lookingDown = pitch >= (_lookingDown ? angle - Hysteresis : angle);
            bool show = mode switch
            {
                DeskWindowMode.Hold => keyHeld,
                DeskWindowMode.Toggle => _latched,
                DeskWindowMode.LookDown => !_latched && _lookingDown,
                DeskWindowMode.Always => !_latched,
                _ => false,
            };
            _heldOpen = moveHeld || (mode == DeskWindowMode.Hold && keyHeld);
            // Also while moving it, and while the settings panel is open, so its sliders can line it up with the desk.
            show |= moveHeld || panelOpen;
            Move(moveHeld, recenter);

            if (inWorld)
                Sample(show ? _pitchShown : _pitchHidden, pitch);
            if (!show)
                return;

            EnsureResources();
            UpdateColor();
            UpdateCorners(_anchored ? _anchor : recenter,
                _moving ? _moveForward : Settings.DeskForward.Value, _moving ? _moveRight : Settings.DeskRight.Value,
                _moving ? _moveYaw : Settings.DeskYaw.Value);
            _visible = true;
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// The desk window key. In Hold mode holding it is what shows the window; in the other modes each press turns
    /// the window on or off. Returns a message to show, or null.
    /// </summary>
    public string OnKeyPressed()
    {
        DeskWindowMode mode = Settings.DeskMode.Value;
        switch (mode)
        {
            case DeskWindowMode.Hold:
                return null;
            case DeskWindowMode.Off:
                return "Desk window is off (DeskWindow.Mode in the settings)";
            default:
                _latched = !_latched;
                bool on = mode == DeskWindowMode.Toggle ? _latched : !_latched;
                if (!on)
                    return "Desk window off";
                return mode == DeskWindowMode.LookDown ? "Desk window on (look down)" : "Desk window on";
        }
    }

    /// <summary>
    /// While MoveKey is held, the mouse slides the window over the desk: right moves it right, forward (the cursor
    /// going up) moves it away, MoveSpeed millimeters per pixel, in your view's directions whatever its turn. The
    /// scroll wheel turns it about its center, RotateStep degrees per notch. Moving it anchors it for the rest of
    /// the VR session. Letting go saves where it is (one settings write, not one per frame).
    /// </summary>
    private void Move(bool moveHeld, HmdMatrix34_t recenter)
    {
        if (!moveHeld)
        {
            if (_moving)
            {
                _moving = false;
                Settings.DeskForward.Value = _moveForward;
                Settings.DeskRight.Value = _moveRight;
                Settings.DeskYaw.Value = _moveYaw;
                Plugin.Logger.LogInfo($"Desk window moved to {Settings.DeskForward.Value:0.00} m ahead, {Settings.DeskRight.Value:0.00} m right, " +
                                      $"turned {Settings.DeskYaw.Value:0} degrees.");
            }
            return;
        }
        if (!Hotkeys.TryGetCursor(out int x, out int y))
            return;
        if (!_moving)
        {
            if (!_anchored)
            {
                _anchor = recenter;
                _anchored = true;
            }
            _moveForward = Settings.DeskForward.Value;
            _moveRight = Settings.DeskRight.Value;
            _moveYaw = Settings.DeskYaw.Value;
            _cursorX = x;
            _cursorY = y;
            _moving = true;
        }
        int dx = x - _cursorX, dy = y - _cursorY;
        _cursorX = x;
        _cursorY = y;
        if (Math.Abs(dx) <= MaxCursorStep && Math.Abs(dy) <= MaxCursorStep)
        {
            float metersPerPixel = Settings.DeskMoveSpeed.Value / 1000f;
            _moveRight = Clamp(Settings.DeskRight, _moveRight + dx * metersPerPixel);
            _moveForward = Clamp(Settings.DeskForward, _moveForward - dy * metersPerPixel);
        }
        float notches = Hotkeys.WheelNotches();
        if (notches != 0f)
        {
            // Wrapped rather than clamped, so it keeps turning past half a turn.
            float yaw = _moveYaw + notches * Settings.DeskRotateStep.Value;
            _moveYaw = yaw - 360f * MathF.Floor((yaw + 180f) / 360f);
        }
    }

    /// <summary>A value kept inside the setting's own range.</summary>
    private static float Clamp(BepInEx.Configuration.ConfigEntry<float> entry, float value) =>
        entry.Description.AcceptableValues is { } range ? (float)range.Clamp(value) : value;

    /// <summary>The room's tracking space changed (SteamVR reset it), so a placed window's anchor no longer means anything.</summary>
    public void ForgetPlacement() => _anchored = false;

    /// <summary>
    /// Draw the window into both eye images, after everything else and before the submit, from your real eye
    /// positions at <paramref name="head"/>: the pose the frame was rendered with and is submitted with, so the
    /// headset's reprojection keeps the window fixed in the room. Within an SRP callback the commands go through
    /// its context (submitted right away); otherwise they run immediately.
    /// </summary>
    public void Render(ScriptableRenderContext context, bool haveContext, RenderTexture[] eyes, CVRSystem hmd, HmdMatrix34_t head)
    {
        if (!_visible || _failed || hmd == null || eyes[0] == null || eyes[1] == null)
            return;
        try
        {
            _commands ??= new CommandBuffer { name = "VRisingVR desk window" };
            _commands.Clear();
            for (int e = 0; e < 2; e++)
            {
                EVREye eye = e == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                // Binding a render texture turns on Unity's render-texture Y flip, which SetViewProjectionMatrices
                // applies to this camera-style projection: the orientation HDRP rendered the eye in.
                _commands.SetRenderTarget(new RenderTargetIdentifier(eyes[e]));
                _commands.SetViewProjectionMatrices(VRMath.TrackingView(head, hmd.GetEyeToHeadTransform(eye)),
                    VRMath.Projection(hmd, eye, NearClip, FarClip));
                _commands.DrawMesh(_mesh, _identity, _material, 0, 0, null);
            }
            if (haveContext)
            {
                context.ExecuteCommandBuffer(_commands);
                context.Submit();
            }
            else
            {
                Graphics.ExecuteCommandBuffer(_commands);
            }
            _commands.Clear();
            _drawn = true;
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>True once after a frame the window was drawn in (the menus' black eye images then need clearing).</summary>
    public bool TakeDrawn()
    {
        bool drawn = _drawn;
        _drawn = false;
        return drawn;
    }

    /// <summary>
    /// Diagnostics: how far below the horizon your character's feet are, seen from your recentered eyes. The HUD's
    /// anchor points there in Feet mode, tilted by HUD.PitchOffset (positive = up), which is taken back out.
    /// </summary>
    public void NoteBoard(HudAnchor anchor)
    {
        if (anchor.Valid && Settings.HudAnchorMode.Value == HudAnchorMode.Feet)
            _boardPitch = MathF.Asin(Math.Clamp(-anchor.Direction.y, -1f, 1f)) * Mathf.Rad2Deg + Settings.HudPitchOffset.Value;
    }

    /// <summary>For F5's _info.txt: the window's state and, if it has been laid out, its corners in tracking space.</summary>
    public string CaptureInfo()
    {
        string info = $"desk window: visible {_visible}, anchored {_anchored} (moved with MoveKey), moving {_moving}, failed {_failed}. {Describe()}";
        if (_vertices != null)
        {
            Span<Vector3> v = _vertices.AsSpan();
            info += "\ndesk window corners (tracking m, near left / far left / far right / near right):";
            for (int i = 0; i < 4; i++)
                info += $" ({v[i].x:F3}, {v[i].y:F3}, {v[i].z:F3})";
        }
        return info;
    }

    /// <summary>For the Perf line: how often the window showed and where your head pointed meanwhile (null if nothing to report).</summary>
    public string TakeStats()
    {
        int total = _pitchHidden.Count + _pitchShown.Count;
        if (total == 0)
            return null;
        string stats = $"desk window shown {100f * _pitchShown.Count / total:F0}%, head pitch hidden {Percentiles(_pitchHidden)}";
        if (_pitchShown.Count > 0)
            stats += $", shown {Percentiles(_pitchShown)}";
        if (!float.IsNaN(_boardPitch))
            stats += $", board {_boardPitch:F0} deg down";
        _pitchHidden.Clear();
        _pitchShown.Clear();
        return stats;
    }

    public void Destroy()
    {
        if (_mesh != null)
            UnityEngine.Object.Destroy(_mesh);
        if (_material != null)
            UnityEngine.Object.Destroy(_material);
        _commands?.Release();
        _mesh = null;
        _material = null;
        _commands = null;
        _vertices = null;
        _appliedColor = (ChromaKeyColor)(-1);
        // A VR restart starts fresh: unlatched, following the recenter until placed again, and retrying after a failure.
        _latched = false;
        _anchored = false;
        _failed = false;
        _moving = false; // a move cut off by stopping VR isn't saved
        _lookingDown = false;
        _visible = false;
        _drawn = false;
        _pitchHidden.Clear();
        _pitchShown.Clear();
        _boardPitch = float.NaN;
    }

    private void Fail(Exception e)
    {
        _failed = true;
        _visible = false;
        Plugin.Logger.LogError($"Desk window disabled after an error: {e}");
    }

    /// <summary>Degrees the head points below the horizon (negative = above it). OpenVR's forward is -Z.</summary>
    private static float PitchDown(HmdMatrix34_t pose) => MathF.Asin(Math.Clamp(pose.m6, -1f, 1f)) * Mathf.Rad2Deg;

    /// <summary>The pose's heading along the floor, as a unit vector (x, z) in tracking space.</summary>
    private static void Heading(HmdMatrix34_t pose, out float fx, out float fz)
    {
        fx = -pose.m2;
        fz = -pose.m10;
        float length = MathF.Sqrt(fx * fx + fz * fz);
        if (length < 1e-3f)
        {
            fx = 0f;
            fz = -1f;
        }
        else
        {
            fx /= length;
            fz /= length;
        }
    }

    /// <summary>
    /// Lay the rectangle flat at desk height, <paramref name="forward"/> ahead of and <paramref name="right"/> right
    /// of the anchor along its heading, turned <paramref name="yawDegrees"/> clockwise (seen from above) about its
    /// center, in tracking space (meters).
    /// </summary>
    private void UpdateCorners(HmdMatrix34_t anchor, float forward, float right, float yawDegrees)
    {
        Heading(anchor, out float fx, out float fz);
        // Right is (-fz, 0, fx): looking along -Z (OpenVR's forward), right is +X.
        float rx = -fz, rz = fx;
        float cx = anchor.m3 + fx * forward + rx * right;
        float cy = anchor.m7 - Settings.DeskBelowEyes.Value;
        float cz = anchor.m11 + fz * forward + rz * right;
        // The rectangle's own axes: the heading turned clockwise, so its forward edge swings toward the right.
        float yaw = yawDegrees * Mathf.Deg2Rad, cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
        float tfx = fx * cos + rx * sin, tfz = fz * cos + rz * sin;
        fx = tfx;
        fz = tfz;
        rx = -fz;
        rz = fx;
        float hw = 0.5f * Settings.DeskWidth.Value, hd = 0.5f * Settings.DeskDepth.Value;

        Span<Vector3> v = _vertices.AsSpan();
        SetPoint(ref v[0], cx - rx * hw - fx * hd, cy, cz - rz * hw - fz * hd); // near left
        SetPoint(ref v[1], cx - rx * hw + fx * hd, cy, cz - rz * hw + fz * hd); // far left
        SetPoint(ref v[2], cx + rx * hw + fx * hd, cy, cz + rz * hw + fz * hd); // far right
        SetPoint(ref v[3], cx + rx * hw - fx * hd, cy, cz + rz * hw - fz * hd); // near right
        _mesh.vertices = _vertices;
    }

    private static void SetPoint(ref Vector3 v, float x, float y, float z)
    {
        v.x = x;
        v.y = y;
        v.z = z;
    }

    /// <summary>The key colour, exactly: pure primaries and secondaries are the same in sRGB and linear terms.</summary>
    private void UpdateColor()
    {
        ChromaKeyColor key = Settings.DeskColor.Value;
        if (key == _appliedColor)
            return;
        Color color = default;
        color.r = key is ChromaKeyColor.Red or ChromaKeyColor.Magenta ? 1f : 0f;
        color.g = key is ChromaKeyColor.Green or ChromaKeyColor.Cyan ? 1f : 0f;
        color.b = key is ChromaKeyColor.Blue or ChromaKeyColor.Magenta or ChromaKeyColor.Cyan ? 1f : 0f;
        color.a = 1f;
        _material.color = color; // UI/Default multiplies the white texture and vertex colour by it
        _appliedColor = key;
    }

    private static void Sample(List<float> samples, float value)
    {
        if (samples.Count < MaxSamples)
            samples.Add(value);
    }

    private static string Percentiles(List<float> samples)
    {
        if (samples.Count == 0)
            return "-";
        samples.Sort();
        float At(float p) => samples[(int)MathF.Round(p * (samples.Count - 1))];
        return $"p10 {At(0.1f):F0} p50 {At(0.5f):F0} p90 {At(0.9f):F0} deg";
    }

    private void EnsureResources()
    {
        if (_mesh != null)
            return;
        _identity = Matrix4x4.identity;
        _material = new Material(Canvas.GetDefaultCanvasMaterial()) { name = "VRisingVR_DeskWindow", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _material.mainTexture = Texture2D.whiteTexture;
        _material.SetColor("_TextureSampleAdd", default);
        _material.SetFloat("_UIVertexColorAlwaysGammaSpace", 0f);
        _material.SetFloat("unity_GUIZTestMode", 8f); // Always: over everything, like a hole cut in the picture
        _appliedColor = (ChromaKeyColor)(-1);

        _vertices = new Il2CppStructArray<Vector3>(4);
        var uv = new Il2CppStructArray<Vector2>(4);
        Span<Vector2> u = uv.AsSpan();
        u[1].y = 1f;
        u[2].x = 1f;
        u[2].y = 1f;
        u[3].x = 1f;
        var colors = new Il2CppStructArray<Color32>(4);
        Span<Color32> c = colors.AsSpan();
        for (int i = 0; i < 4; i++)
        {
            c[i].r = 255;
            c[i].g = 255;
            c[i].b = 255;
            c[i].a = 255;
        }
        var triangles = new Il2CppStructArray<int>(6);
        Span<int> t = triangles.AsSpan();
        t[0] = 0;
        t[1] = 1;
        t[2] = 2;
        t[3] = 0;
        t[4] = 2;
        t[5] = 3;
        _mesh = new Mesh { name = "VRisingVR_DeskWindow", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _mesh.MarkDynamic();
        _mesh.vertices = _vertices;
        _mesh.uv = uv;
        _mesh.colors32 = colors;
        _mesh.triangles = triangles;
    }
}
