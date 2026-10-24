using System;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// Renders the game's own top-down camera view into a SteamVR headset.
///
/// The game camera is left untouched (it still drives gameplay, mouse aiming and the
/// desktop window). Two extra eye cameras are placed at the game camera every frame and
/// offset by the tracked head pose, scaled by WorldScale so the world reads as a tabletop.
/// </summary>
public class VRRig : MonoBehaviour
{
    public VRRig(IntPtr ptr) : base(ptr) { }

    private enum State { Off, Running, Failed }

    private State _state = State.Off;
    private CVRSystem _hmd;

    private readonly Camera[] _eyes = new Camera[2];
    private readonly float[] _eyeFov = { 100f, 100f };
    private readonly float[] _eyeAspect = { 1f, 1f };
    private int _eyeWidth, _eyeHeight;
    private readonly RenderTexture[] _textures = new RenderTexture[2];
    private readonly IntPtr[] _nativeTextures = new IntPtr[2];
    private Camera _sourceCamera;

    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private HmdMatrix34_t _renderedPose;
    private bool _submitThisFrame;
    private HmdMatrix34_t _submitPose;
    private readonly TrackedDevicePose_t[] _noGamePoses = new TrackedDevicePose_t[0];
    private bool _poseFromCompositor;
    private bool _captureRequested;
    private readonly VrSettingsPanel _settingsPanel = new();

    private bool _needsRecenter = true;
    private Vector3 _recenterPos;
    private Quaternion _recenterYawInv = Quaternion.identity;
    private HmdMatrix34_t _recenterPose;
    private bool _haveRecenterPose;

    // Main menu is the "UIEntryPoint" scene; gameplay happens in this one.
    private const string WorldSceneName = "VRisingWorld";
    private bool _worldLoaded;
    private float _sceneCheckTimer;
    private string _sceneNames;
    private bool _wasInWorld;

    private readonly VirtualScreen _screen = new();
    private readonly HudLayer _hud = new();
    private readonly VrCursor _cursor = new();
    private readonly VrToast _toast = new();
    private readonly GroundReticle _reticle = new();
    private readonly GameHud _gameHud = new();
    private readonly WorldBars _bars = new();
    private readonly DesktopMirror _mirror = new();
    // The key-coloured window onto your desk, drawn into the eye images last (after the bars and the cursor).
    private readonly DeskWindow _desk = new();
    private bool _deskKeyHeld;
    // The health bars and the ground cursor, drawn into the eye images after they render (DrawEyeOverlays).
    private bool _barsWanted;
    private bool _reticleWanted;
    private bool _overlaysFailed;
    private CommandBuffer _overlayCommands;
    private const int CaptureDelayFrames = 4;
    private int _captureDelay;

    // The game's camera systems run in PresentationSystemGroup, after LateUpdate, so the eyes placed in
    // LateUpdate trail the game camera by a frame. HDRP's beginContextRendering runs after that and
    // before culling, so the eyes are placed again there from the camera's final pose for the frame.
    private Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>> _beginContext;
    private bool _eyesActive;
    private Camera _frameGame;
    private Vector3 _frameHeadPos;
    private Quaternion _frameHeadRot;
    private int _framePlaced = -1;
    private int _contextPlaced = -1;
    private bool _loggedContextPlacement;
    private bool _contextPlacementFailed;
    private int _poseFlushes;


    // Submit the eyes right after they render (see OnEndCameraRendering).
    private Il2CppSystem.Action<ScriptableRenderContext, Camera> _endCamera;
    private int _eyesDoneFrame = -1;
    private int _eyesDoneMask;
    private bool _loggedEarlySubmit;
    private bool _earlySubmitFailed;

    private Matrix4x4 _lastPlacedView;
    private Matrix4x4 _previousPlacedView;
    private Vector4 _freshScreenSizeOverride;
    private Vector4 _freshScreenCoordScaleBias;
    private float _freshMaterialMipBias;
    // Set by the screen toggle key; cleared (back to automatic) when switching between menu and world.
    private bool? _screenOverride;
    private bool _eyesCleared;

    private float _pacingTimer;
    private bool _pacingApplied;
    private int _savedTargetFrameRate = -1;

    private const float ResyncInterval = 2f;
    private float _resyncTimer;
    private float _projectionCheckTimer = -1f;

    private const float StatsInterval = 5f;
    private float _statsTimer;
    private int _statsFrames;
    private Compositor_CumulativeStats _lastStats;

    private void Start()
    {
        Plugin.Logger.LogInfo($"VR rig active (AutoStart={Settings.AutoStart.Value}, toggle={Settings.ToggleKey.Value}).");
        this.StartCoroutine(EndOfFrame.Loop(() =>
        {
            if (_state == State.Running)
                OnEndOfFrame();
        }));
        if (Settings.AutoStart.Value)
            StartVR();
    }

    /// <summary>HDRP, before culling any camera this frame (main thread).</summary>
    private void OnBeginContextRendering(ScriptableRenderContext context, Il2CppSystem.Collections.Generic.List<Camera> cameras)
    {
        if (_contextPlacementFailed)
            return;
        try
        {
            int frame = Time.frameCount;
            if (_state != State.Running || !_eyesActive || _frameGame == null || _framePlaced != frame || _contextPlaced == frame)
                return;
            _contextPlaced = frame;

            // Unity has normally waited for the render thread by now, so last frame's WaitGetPoses has
            // returned and GetLastPoses gives the pose SteamVR pairs with this frame's submit. If it hasn't
            // returned yet, force the sync (Valve's legacy plugin does the same with GetNativeTexturePtr),
            // or this frame would be rendered with the previous frame's pose.
            if (SubmitBridge.PosesPending && _textures[0] != null)
            {
                _textures[0].GetNativeTexturePtr();
                SubmitBridge.MarkPosesReturned();
                _poseFlushes++;
            }
            if (UpdateHeadPose(out Vector3 headPos, out Quaternion headRot))
            {
                _frameHeadPos = headPos;
                _frameHeadRot = headRot;
                _submitPose = _renderedPose;
            }
            PlaceEyes(_frameGame, _frameHeadPos, _frameHeadRot);
            if (!_loggedContextPlacement)
            {
                _loggedContextPlacement = true;
                Plugin.Logger.LogInfo("Eyes now follow the game camera's final pose each frame (placed in beginContextRendering).");
            }
        }
        catch (Exception e)
        {
            _contextPlacementFailed = true;
            Plugin.Logger.LogError($"Late eye placement disabled after an error (eyes stay a frame behind the camera): {e}");
        }
    }

    /// <summary>HDRP, after each camera has rendered and its work was submitted (main thread).</summary>
    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (_earlySubmitFailed)
            return;
        try
        {
            if (_state != State.Running || !_submitThisFrame || !_eyesActive)
                return;
            int frame = Time.frameCount;
            if (_eyesDoneFrame != frame)
            {
                _eyesDoneFrame = frame;
                _eyesDoneMask = 0;
            }
            if (camera == _eyes[0]) _eyesDoneMask |= 1;
            else if (camera == _eyes[1]) _eyesDoneMask |= 2;
            else return;

            if (_eyesDoneMask == 3)
            {
                // HDRP has submitted both eyes' work (it submits the context before this callback), so the
                // ground cursor and the health bars go into the finished images before they're handed over,
                // then the desk window on top of everything.
                DrawEyeOverlays(context, haveContext: true);
                _desk.Render(context, haveContext: true, _textures, _hmd, _submitPose);
                // Both eyes are drawn: hand them to SteamVR now instead of at the end of the frame, which would
                // add the desktop camera's and UI's GPU time to every VR frame's latency.
                QueueEyeSubmit();
                if (!_loggedEarlySubmit)
                {
                    _loggedEarlySubmit = true;
                    Plugin.Logger.LogInfo("Eyes are submitted as soon as they're rendered (endCameraRendering).");
                }
            }
        }
        catch (Exception e)
        {
            _earlySubmitFailed = true;
            Plugin.Logger.LogError($"Early eye submit disabled after an error (eyes are submitted at the end of the frame): {e}");
        }
    }

    private void QueueEyeSubmit()
    {
        SubmitBridge.QueueSubmit(_nativeTextures[0], _nativeTextures[1], _submitPose);
        _submitThisFrame = false;
    }

    /// <summary>
    /// Draw the ground cursor and the game's overhead health bars into both eye images, after they've rendered
    /// and before the submit, each with that eye's own view and projection. The eye textures hold HDRP's depth
    /// (it copies it into a target texture that has a depth buffer), so the cursor is hidden by whatever is in
    /// front of it; the bars aren't depth-tested. Within an SRP callback the commands go through its context
    /// (submitted right away, so they run after the eyes and before the submit event); otherwise they run
    /// immediately.
    /// </summary>
    private void DrawEyeOverlays(ScriptableRenderContext context, bool haveContext)
    {
        if (_overlaysFailed || (!_barsWanted && !_reticleWanted) || _eyes[0] == null || _eyes[1] == null)
            return;
        try
        {
            Vector3 l = _eyes[0].transform.position, r = _eyes[1].transform.position;
            Vector3 head = default;
            head.x = 0.5f * (l.x + r.x);
            head.y = 0.5f * (l.y + r.y);
            head.z = 0.5f * (l.z + r.z);

            bool bars = _barsWanted && _gameHud.Refresh() && _bars.Prepare(_gameHud, head, r);
            bool cursor = _reticleWanted && GameCamera.TryReadAim(out Vector3 aim) && _reticle.Prepare(true, aim, head);
            if (!cursor)
                _reticle.Prepare(false, default, default);
            if (!bars && !cursor)
                return;

            _overlayCommands ??= new CommandBuffer { name = "VRisingVR eye overlays" };
            _overlayCommands.Clear();
            for (int e = 0; e < 2; e++)
            {
                // Binds the eye's colour and depth. Binding a render texture also turns on Unity's
                // render-texture Y flip, which SetViewProjectionMatrices applies to this camera-style
                // projection: the same orientation HDRP rendered the eye in.
                _overlayCommands.SetRenderTarget(new RenderTargetIdentifier(_textures[e]));
                _overlayCommands.SetViewProjectionMatrices(_eyes[e].worldToCameraMatrix, _eyes[e].projectionMatrix);
                // The materials set their own depth test (the shader reads the material's value first).
                if (cursor)
                    _reticle.Draw(_overlayCommands);
                if (bars)
                    _bars.Draw(_overlayCommands);
            }
            if (haveContext)
            {
                context.ExecuteCommandBuffer(_overlayCommands);
                context.Submit();
            }
            else
            {
                Graphics.ExecuteCommandBuffer(_overlayCommands);
            }
            _overlayCommands.Clear();
        }
        catch (Exception e)
        {
            _overlaysFailed = true;
            _barsWanted = false;
            _reticleWanted = false;
            Plugin.Logger.LogError($"3D health bars and ground cursor disabled after an error: {e}");
        }
    }

    /// <summary>
    /// After WaitForEndOfFrame: this frame's cameras and UI have been drawn. Submitting here puts the
    /// event after the eyes' draw commands on the render thread, so the textures are complete and are
    /// paired with the pose they were rendered with (and a frame sooner than submitting next frame).
    /// The render thread then calls WaitGetPoses, whose poses the next frame renders with.
    /// </summary>
    private void OnEndOfFrame()
    {
        // Fallback (menus' black frames, or if the early submit isn't available).
        if (_submitThisFrame)
        {
            if (_eyesActive)
                DrawEyeOverlays(default, haveContext: false);
            // Also into the menus' black frames, so you can see your keyboard there too.
            _desk.Render(default, haveContext: false, _textures, _hmd, _submitPose);
            QueueEyeSubmit();
        }

        _screen.CaptureAtEndOfFrame();

        if (_captureRequested)
        {
            _captureRequested = false;
            try
            {
                string stem = CaptureTool.Capture(_textures[0], _textures[1], _hud.Visible ? _hud.Target : null, BuildCaptureInfo());
                Plugin.Logger.LogInfo($"3D capture saved: {stem}_left/_right/_hud/_desktop.png + _info.txt");
                _toast.Show("3D capture saved");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"3D capture failed: {e}");
            }
        }
    }

    private string BuildCaptureInfo()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"time {DateTime.Now:O}");
        sb.AppendLine($"settings: WorldScale {Settings.WorldScale.Value}, HeightOffset {Settings.HeightOffset.Value}, BackOffset {Settings.BackOffset.Value}, " +
                      $"FollowCameraPitch {Settings.FollowCameraPitch.Value}, RenderScale {Settings.RenderScale.Value}, AA {Settings.EyeAA.Value}, " +
                      $"PerformanceMode {Settings.EyePerformanceMode.Value}, EyeIsolation {Settings.EyeIsolation.Value}, PoseSource {Settings.HeadPoseSource.Value} " +
                      $"(this frame from compositor: {_poseFromCompositor})");
        sb.AppendLine($"eye textures {_eyeWidth}x{_eyeHeight}; eye fov {_eyeFov[0]:F2}/{_eyeFov[1]:F2}, aspect {_eyeAspect[0]:F4}/{_eyeAspect[1]:F4}");
        for (int i = 0; i < 2; i++)
        {
            EVREye eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
            float l = 0, r = 0, t = 0, b = 0;
            _hmd.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
            HmdMatrix34_t e2h = _hmd.GetEyeToHeadTransform(eye);
            sb.AppendLine($"{eye}: raw tan L {l:F4} R {r:F4} T {t:F4} B {b:F4}; eyeToHead t=({e2h.m3:F4},{e2h.m7:F4},{e2h.m11:F4})");
        }
        HmdMatrix34_t p = _renderedPose;
        sb.AppendLine($"HMD pose (rendered): [{p.m0:F4} {p.m1:F4} {p.m2:F4} {p.m3:F4}; {p.m4:F4} {p.m5:F4} {p.m6:F4} {p.m7:F4}; {p.m8:F4} {p.m9:F4} {p.m10:F4} {p.m11:F4}]");
        sb.AppendLine($"recenter pos {_recenterPos}, recenter yaw inverse {_recenterYawInv.eulerAngles}");
        Camera game = Camera.main;
        if (game != null)
        {
            Transform gt = game.transform;
            sb.AppendLine($"game camera pos {gt.position}, euler {gt.eulerAngles}, fov {game.fieldOfView:F2}, near {game.nearClipPlane:F3}, far {game.farClipPlane:F1}, " +
                          $"pixels {game.pixelWidth}x{game.pixelHeight}");
        }
        for (int i = 0; i < 2; i++)
        {
            if (_eyes[i] == null)
                continue;
            Transform et = _eyes[i].transform;
            Matrix4x4 m = _eyes[i].projectionMatrix;
            sb.AppendLine($"eye {i} camera pos {et.position}, euler {et.eulerAngles}, fov {_eyes[i].fieldOfView:F2}, aspect {_eyes[i].aspect:F4}, " +
                          $"proj m00 {m.m00:F4} m02 {m.m02:F4} m11 {m.m11:F4} m12 {m.m12:F4} m22 {m.m22:F4} m23 {m.m23:F4} m32 {m.m32:F4}");
        }
        sb.AppendLine(_desk.CaptureInfo());
        sb.AppendLine($"desktop png: the game camera's own render (desktop mirror {(Settings.DesktopMirror.Value ? "paused for the capture" : "off")})");
        sb.AppendLine("note: all PNGs are upright, as the headset shows them (before SteamVR's lens warp and reprojection; the HUD PNG is a separate overlay). " +
                      "Each eye's straight-ahead direction is at column (1 - m02)/2*width from the left and row (1 + m12)/2*height from the top.");
        return sb.ToString();
    }

    private void Update()
    {
        if (Hotkeys.Pressed(Settings.SettingsKey.Value))
            _settingsPanel.Toggle();
        _settingsPanel.Tick();
        _toast.Tick();
        // The panel's "place where you face" countdown ended: the desk window goes where the head points now.
        if (_settingsPanel.TakePlaceRequest())
        {
            string placed = _state == State.Running && _haveRecenterPose
                ? _desk.PlaceAtGaze(_renderedPose, _recenterPose)
                : $"Start VR ({Settings.ToggleKey.Value}) before placing the desk window";
            Plugin.Logger.LogInfo(placed);
            _settingsPanel.ShowPlaceResult(placed);
        }

        // Poll every key each frame so edge detection stays current.
        bool toggle = Hotkeys.Pressed(Settings.ToggleKey.Value);
        bool recenter = Hotkeys.Pressed(Settings.RecenterKey.Value);
        bool scaleDown = Hotkeys.Pressed(Settings.ScaleDownKey.Value);
        bool scaleUp = Hotkeys.Pressed(Settings.ScaleUpKey.Value);
        bool screenToggle = Hotkeys.Pressed(Settings.ScreenToggleKey.Value);
        bool isolate = Hotkeys.Pressed(Settings.IsolationKey.Value);
        bool capture = Hotkeys.Pressed(Settings.CaptureKey.Value);
        bool deskKey = Hotkeys.Pressed(Settings.DeskKey.Value);
        _deskKeyHeld = Hotkeys.IsDown(Settings.DeskKey.Value);

        if (toggle)
        {
            Plugin.Logger.LogInfo($"{Settings.ToggleKey.Value} pressed: {(_state == State.Running ? "stopping" : "starting")} VR.");
            if (_state == State.Running) StopVR();
            else StartVR();
        }

        if (_state != State.Running)
            return;

        PollEvents();
        if (_state != State.Running)
            return;

        if (recenter)
        {
            Recenter("recenter key");
            _toast.Show("Recentered");
        }

        if (deskKey && _desk.OnKeyPressed() is string deskMessage)
        {
            Plugin.Logger.LogInfo(deskMessage);
            _toast.Show(deskMessage);
        }

        if (capture)
        {
            // A few frames later, with the desktop mirror off meanwhile, so _desktop.png is the game's own view
            // (and its TAA history has caught up).
            _captureDelay = CaptureDelayFrames;
            Plugin.Logger.LogInfo($"3D capture requested; saving to {CaptureTool.Directory}");
        }

        if (screenToggle)
        {
            _screenOverride = !ScreenWanted(_wasInWorld);
            if (_screenOverride == true)
                _screen.RequestPlacement();
            Plugin.Logger.LogInfo($"Virtual screen {(_screenOverride == true ? "on" : "off")}.");
            _toast.Show(_screenOverride == true ? "Virtual screen on" : "Virtual screen off");
        }

        if (scaleDown || scaleUp)
        {
            Settings.WorldScale.Value += scaleUp ? Settings.ScaleStep.Value : -Settings.ScaleStep.Value;
            Plugin.Logger.LogInfo($"WorldScale = {Settings.WorldScale.Value}");
            _toast.Show($"World scale {Settings.WorldScale.Value:0.0}");
        }

        if (isolate)
        {
            // Diagnostic A/B: each level strips more of the game's rendering from the eye views, to find
            // what distorts them. Fresh cameras so nothing copied at a lower level lingers.
            Settings.EyeIsolation.Value = (Settings.EyeIsolation.Value + 1) % (MaxIsolationLevel + 1);
            Plugin.Logger.LogInfo($"Eye isolation level {Settings.EyeIsolation.Value}: {IsolationDescription(Settings.EyeIsolation.Value)}.");
            _toast.Show($"Isolation level {Settings.EyeIsolation.Value}: {IsolationDescription(Settings.EyeIsolation.Value)}", 5f);
            RecreateEyeCameras();
            _projectionCheckTimer = 3f;
        }
    }

    private void LateUpdate()
    {
        if (_state != State.Running)
            return;

        LogPerfStats();
        bool holdMirror = _captureDelay > 0;
        if (holdMirror && --_captureDelay == 0)
            _captureRequested = true;
        _previousPlacedView = _lastPlacedView;
        // Set below once this frame's eyes are ready; submitted once they've rendered.
        _submitThisFrame = false;
        _eyesActive = false;
        _barsWanted = false;
        _hud.SetOverhead(OverheadHudMode.Keep, null);
        try { _gameHud.EnsureOverheadOnDesktop(); } catch { }
        EnsureEyeTextures();

        Camera game = Camera.main;
        bool inWorld = game != null && IsWorldLoaded();
        if (inWorld != _wasInWorld)
        {
            _wasInWorld = inWorld;
            _screenOverride = null;
            _resyncTimer = 0f;
        }

        bool havePose = UpdateHeadPose(out Vector3 headPos, out Quaternion headRot);
        EnforceFramePacing(havePose);
        // The desk window was drawn into the eye images, so the menus' black frames need clearing again, even if
        // this frame returns early (no pose) and they're only submitted later.
        if (_desk.TakeDrawn())
            _eyesCleared = false;
        _desk.Update(havePose && _haveRecenterPose, _renderedPose, _recenterPose, _deskKeyHeld, _settingsPanel.Visible, inWorld);

        if (SubmitBridge.TakePendingError() is { } renderThreadError)
            Plugin.Logger.LogError(renderThreadError);

        // Menus are flat UI the eye cameras can't see, so the virtual screen shows them. In menus it
        // follows your gaze; in the world it stays put (like a TV behind the table) until re-centered.
        bool screenWanted = ScreenWanted(inWorld);
        _screen.Tick(screenWanted, follow: !inWorld, havePose, _renderedPose);
        // In the world, the UI-only layer is the normal way to see the HUD and menus; the full mirror
        // (F7) replaces it rather than stacking on top of it.
        // While you hold the key to peek at your desk, the HUD would cover the window, so it steps aside (not while
        // the settings panel is open: the panel is on the HUD).
        bool deskPeek = _desk.Visible && Settings.DeskMode.Value == DeskWindowMode.Hold && !_settingsPanel.Visible;
        HudAnchor anchor = inWorld ? ComputeHudAnchor(game) : default;
        _hud.Tick(inWorld && Settings.HudEnabled.Value && !screenWanted && !deskPeek, anchor);
        _desk.NoteBoard(anchor);
        UpdateCursors(inWorld);

        if (!havePose)
        {
            SetEyesEnabled(false);
            _mirror.Tick(false, null, null, null);
            return;
        }

        if (!inWorld)
        {
            _mirror.Tick(false, null, null, null);
            // Nothing 3D to show: skip the two expensive HDRP eye renders, but keep SteamVR fed with
            // black frames so it keeps pacing us and doesn't time out (cleared again after the desk window was drawn).
            SetEyesEnabled(false);
            ClearEyesToBlack();
            _submitPose = _renderedPose;
            _submitThisFrame = true;
            return;
        }
        _eyesCleared = false;

        // The game changes its camera at runtime (e.g. the connecting view swaps the culling and volume
        // masks while its video plays), so re-sync the eyes periodically, not only when the camera changes.
        if (game != _sourceCamera)
        {
            ConfigureEyesFrom(game, log: true);
            _resyncTimer = ResyncInterval;
            _projectionCheckTimer = 3f;
        }
        else if ((_resyncTimer -= Time.unscaledDeltaTime) <= 0f)
        {
            ConfigureEyesFrom(game, log: false);
            _resyncTimer = ResyncInterval;
        }

        PlaceEyes(game, headPos, headRot);
        SetEyesEnabled(true);
        _submitPose = _renderedPose;
        _submitThisFrame = true;
        _frameGame = game;
        _frameHeadPos = headPos;
        _frameHeadRot = headRot;
        _framePlaced = Time.frameCount;
        _eyesActive = true;
        _barsWanted = Settings.BarsEnabled.Value && !_overlaysFailed;
        // The 2D originals stay on the desktop; on the VR HUD they'd show the bars twice.
        if (_barsWanted)
            _hud.SetOverhead(Settings.BarsHudPanel.Value, _gameHud);
        // Skip the game's own desktop render (the window mirrors the left eye) unless the virtual screen,
        // which shows the desktop image, is up.
        _mirror.Tick(Settings.DesktopMirror.Value && !screenWanted && !holdMirror, game, _textures[0], _gameHud);

        if (_projectionCheckTimer > 0f && (_projectionCheckTimer -= Time.unscaledDeltaTime) <= 0f)
        {
            LogEyeState(game);
            _projectionCheckTimer = 10f;
        }
    }

    /// <summary>
    /// Diagnostic: compare what HDRP actually rendered the left eye with against what we set: its
    /// internal resolution (an upscaler or screen override would change it), its view rotation (an
    /// inherited view matrix would ignore head tracking) and its projection. HDRP's projection is the
    /// GPU version (Y flipped for render textures), so the Y terms are compared by magnitude.
    /// </summary>
    private void LogEyeState(Camera game)
    {
        try
        {
            HDCamera hdCamera = HDCamera.GetOrCreate(_eyes[0], 0);
            // HDRP's matrices are from last frame's render; compare them with last frame's final placement.
            Matrix4x4 hv = hdCamera.mainViewConstants.viewMatrix;
            Matrix4x4 ev = _previousPlacedView;
            float viewErr = Mathf.Max(
                Mathf.Max(Mathf.Abs(hv.m00 - ev.m00), Mathf.Abs(hv.m01 - ev.m01), Mathf.Abs(hv.m02 - ev.m02)),
                Mathf.Max(Mathf.Abs(hv.m10 - ev.m10), Mathf.Abs(hv.m11 - ev.m11), Mathf.Abs(hv.m12 - ev.m12)),
                Mathf.Max(Mathf.Abs(hv.m20 - ev.m20), Mathf.Abs(hv.m21 - ev.m21), Mathf.Abs(hv.m22 - ev.m22)));
            Matrix4x4 hp = hdCamera.mainViewConstants.projMatrix;
            Matrix4x4 ep = _eyes[0].projectionMatrix;
            float projErr = Mathf.Max(Mathf.Abs(hp.m00 - ep.m00), Mathf.Abs(hp.m02 - ep.m02),
                Mathf.Max(Mathf.Abs(Mathf.Abs(hp.m11) - Mathf.Abs(ep.m11)), Mathf.Abs(Mathf.Abs(hp.m12) - Mathf.Abs(ep.m12))));

            HDAdditionalCameraData srcHd = game.GetComponent<HDAdditionalCameraData>();
            Plugin.Logger.LogInfo(
                $"Eye state (isolation {Settings.EyeIsolation.Value}): HDRP renders {hdCamera.actualWidth}x{hdCamera.actualHeight} " +
                $"into {_eyeWidth}x{_eyeHeight}, view rotation error {viewErr:F4}, projection error {projErr:F4} | " +
                $"game camera {game.pixelWidth}x{game.pixelHeight} rect {game.rect} fov {game.fieldOfView:F1}" +
                (srcHd != null
                    ? $", AA {srcHd.antialiasing}, dynres {srcHd.allowDynamicResolution}, screenSizeOverride {srcHd.screenSizeOverride}, " +
                      $"scaleBias {srcHd.screenCoordScaleBias}, mipBias {srcHd.materialMipBias:F2}"
                    : ""));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Eye state check failed: {e.Message}");
        }
    }

    /// <summary>
    /// Pitch for the HUD layer: down to where the game camera looks (the character), since the VR view
    /// keeps the horizon level, plus the configured offset. With FollowCameraPitch the view already
    /// looks along the camera, so the HUD stays straight ahead.
    /// </summary>
    private static float HudPitch(Camera game)
    {
        float pitch = 0f;
        if (Settings.HudFollowCameraPitch.Value && !Settings.FollowCameraPitch.Value)
        {
            float cameraPitch = game.transform.eulerAngles.x; // degrees, positive = looking down
            if (cameraPitch > 180f)
                cameraPitch -= 360f;
            pitch = -cameraPitch;
        }
        return Mathf.Clamp(pitch + Settings.HudPitchOffset.Value, -80f, 80f);
    }

    private bool ScreenWanted(bool inWorld) =>
        _screenOverride ?? (inWorld ? Settings.ScreenInWorld.Value : Settings.ScreenInMenus.Value);

    private void LogHeadsetGeometry()
    {
        for (int i = 0; i < 2; i++)
        {
            EVREye eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
            float l = 0, r = 0, t = 0, b = 0;
            _hmd.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
            // Raw b is the upward tangent and t the (negative) downward one; see VRMath.Projection.
            _eyeFov[i] = (Mathf.Atan(b) + Mathf.Atan(-t)) * Mathf.Rad2Deg;
            _eyeAspect[i] = (r - l) / (b - t);
            VRMath.ToUnity(_hmd.GetEyeToHeadTransform(eye), out Vector3 pos, out Quaternion rot);
            Plugin.Logger.LogInfo($"{eye}: tan L={l:F3} R={r:F3} T={t:F3} B={b:F3}, offset {pos.x:F4},{pos.y:F4},{pos.z:F4}, rot {rot.eulerAngles}");
        }
    }

    private void ClearEyesToBlack()
    {
        if (_eyesCleared)
            return;
        RenderTexture previous = RenderTexture.active;
        for (int i = 0; i < 2; i++)
        {
            RenderTexture.active = _textures[i];
            GL.Clear(true, true, Color.black);
        }
        RenderTexture.active = previous;
        _eyesCleared = true;
    }

    /// <summary>
    /// The headset, not the monitor, must set the frame rate: with vsync on, the game is held to
    /// the monitor's 60 Hz while SteamVR waits on a 90 Hz clock, and SteamVR times out on us.
    /// WaitGetPoses on the render thread does the pacing instead. Re-applied periodically in case
    /// the game re-applies its own graphics settings.
    /// </summary>
    private void EnforceFramePacing(bool paced)
    {
        // With a valid head pose every frame is submitted and WaitGetPoses paces the game. Without one
        // (headset off or not tracking) nothing does, so cap it rather than let it spin at hundreds of fps.
        int target = paced ? -1 : 90;
        _pacingTimer -= Time.unscaledDeltaTime;
        if (_pacingTimer > 0f && Application.targetFrameRate == target)
            return;
        _pacingTimer = 1f;

        QualitySettings.vSyncCount = 0;
        int current = Application.targetFrameRate;
        if (current != target)
        {
            if (!_pacingApplied)
                _savedTargetFrameRate = current;
            if (current != -1 && current != 90)
                Plugin.Logger.LogInfo($"Game frame cap {current} overridden while VR runs (SteamVR paces the game).");
            Application.targetFrameRate = target;
        }
        if (!_pacingApplied)
        {
            _pacingApplied = true;
            Plugin.Logger.LogInfo("VSync off while VR runs (SteamVR paces the game).");
        }
    }

    private void RestoreFramePacing()
    {
        if (!_pacingApplied)
            return;
        _pacingApplied = false;

        // Let the game re-apply its own limit for the current view (vsync, menu cap, FPS limit) rather
        // than guessing: the values captured at StartVR are usually just the main menu's cap.
        try
        {
            ProjectM.GraphicsSettings graphics = ProjectM.SettingsManager._ClientSettings?.GraphicSettings;
            if (graphics != null)
            {
                graphics.SetFPSLimit(_wasInWorld);
                Plugin.Logger.LogInfo("Frame pacing handed back to the game's settings.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not re-apply the game's FPS limit: {e.Message}");
        }

        QualitySettings.vSyncCount = 1;
        Application.targetFrameRate = _savedTargetFrameRate;
        Plugin.Logger.LogInfo($"VSync restored (frame cap {_savedTargetFrameRate}).");
    }

    private bool IsWorldLoaded()
    {
        _sceneCheckTimer -= Time.unscaledDeltaTime;
        if (_sceneCheckTimer > 0f)
            return _worldLoaded;
        _sceneCheckTimer = 0.5f;

        bool loaded = false;
        string names = "";
        int count = SceneManager.sceneCount;
        for (int i = 0; i < count; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            string name = scene.name;
            names += (i > 0 ? ", " : "") + name + (scene.isLoaded ? "" : " (loading)");
            if (scene.isLoaded && name == WorldSceneName)
                loaded = true;
        }

        if (names != _sceneNames)
        {
            _sceneNames = names;
            Plugin.Logger.LogInfo($"Scenes: [{names}] -> {(loaded ? "world" : "menu")} mode.");
        }
        _worldLoaded = loaded;
        return loaded;
    }

    private void Recenter(string reason)
    {
        _needsRecenter = true;
        _screen.RequestPlacement();
        _hud.RequestPlacement();
        Plugin.Logger.LogInfo($"Recentered ({reason}).");
    }

    private void PollEvents()
    {
        var ev = new VREvent_t();
        uint size = (uint)Marshal.SizeOf<VREvent_t>();
        while (_hmd != null && _hmd.PollNextEvent(ref ev, size))
        {
            switch ((EVREventType)ev.eventType)
            {
                case EVREventType.VREvent_TrackedDeviceUserInteractionStarted:
                    if (ev.trackedDeviceIndex == OpenVR.k_unTrackedDeviceIndex_Hmd)
                        Recenter("headset put on");
                    break;
                case EVREventType.VREvent_ChaperoneUniverseHasChanged:
                case EVREventType.VREvent_StandingZeroPoseReset:
                    // The room's tracking space moved, so a placed desk window would no longer be on the desk.
                    _desk.ForgetPlacement();
                    Recenter("SteamVR recentered");
                    break;
                case EVREventType.VREvent_SeatedZeroPoseReset:
                    Recenter("SteamVR recentered");
                    break;
                case EVREventType.VREvent_Quit:
                    // Just disconnect. AcknowledgeQuit_Exiting would tell SteamVR the whole process is
                    // exiting, but the game keeps running without VR (F8 restarts it).
                    Plugin.Logger.LogInfo("SteamVR is quitting; stopping VR.");
                    StopVR();
                    return;
            }
        }
    }

    private void OnApplicationQuit() => _settingsPanel.SaveIfOpen();

    private void OnDestroy()
    {
        StopVR();
    }

    private void StartVR()
    {
        if (_state == State.Running)
            return;

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            Plugin.Logger.LogError($"VR needs Direct3D 11 but the game is using {SystemInfo.graphicsDeviceType}. " +
                                   "Add -force-d3d11 to the game's Steam launch options.");
            _state = State.Failed;
            return;
        }

        Plugin.Logger.LogInfo("Starting VR (connecting to SteamVR)...");
        EVRInitError err = EVRInitError.None;
        _hmd = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
        if (err != EVRInitError.None || _hmd == null)
        {
            Plugin.Logger.LogError($"OpenVR init failed: {OpenVR.GetStringForHmdError(err)}. Is SteamVR installed and a headset connected?");
            _hmd = null;
            _state = State.Failed;
            return;
        }

        uint w = 0, h = 0;
        _hmd.GetRecommendedRenderTargetSize(ref w, ref h);
        _eyeWidth = Mathf.Max(256, (int)(w * Settings.RenderScale.Value));
        _eyeHeight = Mathf.Max(256, (int)(h * Settings.RenderScale.Value));
        int width = _eyeWidth;
        int height = _eyeHeight;
        CreateEyeTextures();
        CreateEyeCameras();

        try
        {
            _beginContext = (System.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>)OnBeginContextRendering;
            RenderPipelineManager.add_beginContextRendering(_beginContext);
        }
        catch (Exception e)
        {
            _beginContext = null;
            Plugin.Logger.LogWarning($"Could not hook beginContextRendering (eyes stay a frame behind the camera): {e.Message}");
        }

        try
        {
            _endCamera = (System.Action<ScriptableRenderContext, Camera>)OnEndCameraRendering;
            // add_endCameraRendering is an unstripping stub in this build, so combine into the backing field.
            Il2CppSystem.Action<ScriptableRenderContext, Camera> current = RenderPipelineManager.endCameraRendering;
            RenderPipelineManager.endCameraRendering = current == null
                ? _endCamera
                : Il2CppSystem.Delegate.Combine(current, _endCamera).Cast<Il2CppSystem.Action<ScriptableRenderContext, Camera>>();
        }
        catch (Exception e)
        {
            _endCamera = null;
            Plugin.Logger.LogWarning($"Could not hook endCameraRendering (eyes are submitted at the end of the frame): {e.Message}");
        }

        _sourceCamera = null;
        _needsRecenter = true;
        _submitThisFrame = false;
        _worldLoaded = false;
        _wasInWorld = false;
        _sceneCheckTimer = 0f;
        _sceneNames = null;
        _screenOverride = null;
        _eyesCleared = false;
        _pacingTimer = 0f;
        _statsTimer = 0f;
        _statsFrames = 0;
        _lastStats = ReadCumulativeStats();
        SubmitBridge.ResetErrorState();
        _state = State.Running;
        Plugin.Logger.LogInfo($"VR started: {width}x{height} per eye (RenderScale {Settings.RenderScale.Value}).");
        LogHeadsetGeometry();
        Plugin.Logger.LogInfo(DeskWindow.Describe());
    }

    private void CreateEyeTextures()
    {
        for (int i = 0; i < 2; i++)
        {
            // With a depth buffer, HDRP copies the eye's depth into it at the end of the render (SetFinalTarget),
            // so the ground cursor drawn afterwards is hidden by whatever is in front of it.
            var rt = new RenderTexture(_eyeWidth, _eyeHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = $"VRisingVR_Eye{i}",
                antiAliasing = 1,
                // Nothing the engine tracks references these while the eyes are idle in menus, so without
                // this the scene load's UnloadUnusedAssets destroys them (seen as InvalidTexture on submit).
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            rt.Create();
            _textures[i] = rt;
            _nativeTextures[i] = rt.GetNativeTexturePtr();
        }
        _eyesCleared = false;
    }

    private void CreateEyeCameras()
    {
        for (int i = 0; i < 2; i++)
        {
            var go = new GameObject($"VRisingVR_EyeCamera{i}");
            DontDestroyOnLoad(go);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            HDAdditionalCameraData hd = go.GetComponent<HDAdditionalCameraData>();
            if (hd == null)
                hd = go.AddComponent<HDAdditionalCameraData>();
            if (i == 0)
            {
                // HDRP's own defaults for these, restored after copying from the game camera so an
                // upscaler or screen-size override on the game camera can never resize the eye renders.
                _freshScreenSizeOverride = hd.screenSizeOverride;
                _freshScreenCoordScaleBias = hd.screenCoordScaleBias;
                _freshMaterialMipBias = hd.materialMipBias;
            }
            _eyes[i] = cam;
        }
    }

    private void DestroyEyeCameras()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_eyes[i] != null)
                Destroy(_eyes[i].gameObject);
            _eyes[i] = null;
        }
    }

    /// <summary>Fresh cameras, so a lower isolation level can't leave settings copied by a higher one.</summary>
    private void RecreateEyeCameras()
    {
        DestroyEyeCameras();
        CreateEyeCameras();
        _sourceCamera = null;
        _submitThisFrame = false;
    }

    /// <summary>Recreate the eye textures if the engine destroyed or released them anyway.</summary>
    private bool EnsureEyeTextures()
    {
        bool lost = false;
        for (int i = 0; i < 2; i++)
            lost |= _textures[i] == null || !_textures[i].IsCreated();
        if (!lost)
            return true;

        Plugin.Logger.LogWarning("Eye render textures were lost; recreating them.");
        for (int i = 0; i < 2; i++)
        {
            if (_textures[i] != null)
                Destroy(_textures[i]);
        }
        CreateEyeTextures();
        _sourceCamera = null; // re-point the eye cameras at the new textures
        _submitThisFrame = false;
        return false;
    }

    private void StopVR()
    {
        if (_state != State.Running)
        {
            _state = State.Off;
            return;
        }

        if (_beginContext != null)
        {
            try { RenderPipelineManager.remove_beginContextRendering(_beginContext); } catch { }
            _beginContext = null;
        }
        if (_endCamera != null)
        {
            try
            {
                Il2CppSystem.Action<ScriptableRenderContext, Camera> current = RenderPipelineManager.endCameraRendering;
                if (current != null)
                {
                    Il2CppSystem.Delegate rest = Il2CppSystem.Delegate.Remove(current, _endCamera);
                    RenderPipelineManager.endCameraRendering = rest?.TryCast<Il2CppSystem.Action<ScriptableRenderContext, Camera>>();
                }
            }
            catch
            {
                // nothing to undo
            }
            _endCamera = null;
        }
        _eyesActive = false;
        DestroyEyeCameras();

        _screen.Destroy();
        _hud.Destroy();
        _cursor.Destroy();
        _reticle.Destroy();
        _bars.Destroy();
        _gameHud.Reset();
        _mirror.Destroy();
        _desk.Destroy();
        _overlayCommands?.Release();
        _overlayCommands = null;
        RestoreFramePacing();
        SubmitBridge.QueueShutdown();

        // Release after the render thread has processed the shutdown event.
        for (int i = 0; i < 2; i++)
        {
            if (_textures[i] != null)
            {
                _textures[i].Release();
                Destroy(_textures[i]);
            }
            _textures[i] = null;
            _nativeTextures[i] = IntPtr.Zero;
        }

        _hmd = null;
        _sourceCamera = null;
        _submitThisFrame = false;
        _state = State.Off;
        Plugin.Logger.LogInfo("VR stopped.");
    }

    private const int MaxIsolationLevel = 4;

    private static string IsolationDescription(int level) => level switch
    {
        0 => "normal (eyes copy the game camera's settings)",
        1 => "eyes don't copy the game camera's HDRP settings",
        2 => "level 1 + no custom post-processing or custom passes",
        3 => "level 2 + no distortion/refraction, SSAO or screen-space shadows",
        _ => "level 3 + no post-processing at all",
    };

    private void ConfigureEyesFrom(Camera game, bool log)
    {
        _sourceCamera = game;
        HDAdditionalCameraData srcHd = game.GetComponent<HDAdditionalCameraData>();
        int level = Mathf.Clamp(Settings.EyeIsolation.Value, 0, MaxIsolationLevel);

        for (int i = 0; i < 2; i++)
        {
            Camera eye = _eyes[i];
            if (level == 0)
            {
                eye.CopyFrom(game);
            }
            else
            {
                // Only what decides *what* is drawn, nothing about *how* the game camera renders it.
                eye.cullingMask = game.cullingMask;
                eye.clearFlags = game.clearFlags;
                eye.backgroundColor = game.backgroundColor;
                eye.useOcclusionCulling = game.useOcclusionCulling;
            }
            // Never inherit custom matrices or a viewport from the game camera: an eye's view comes from
            // its transform, and its projection is set every frame.
            eye.ResetWorldToCameraMatrix();
            eye.ResetCullingMatrix();
            eye.rect = new Rect(0f, 0f, 1f, 1f);
            eye.targetTexture = _textures[i];
            eye.stereoTargetEye = StereoTargetEyeMask.None;
            eye.allowDynamicResolution = false;
            eye.depth = game.depth - 1; // render before the game camera; order is otherwise irrelevant

            HDAdditionalCameraData hd = eye.GetComponent<HDAdditionalCameraData>();
            if (hd == null)
                hd = eye.gameObject.AddComponent<HDAdditionalCameraData>();
            ConfigureHdCamera(hd, srcHd, level);
        }

        if (log)
        {
            Plugin.Logger.LogInfo($"Eye cameras configured from '{game.name}' (isolation level {level}, AA {Settings.EyeAA.Value}, " +
                                  $"performance mode {Settings.EyePerformanceMode.Value}).");
        }
    }

    private static readonly FrameSettingsField[] ComfortOff =
    {
        // Blur from head motion, and lens effects that only make sense for a flat screen.
        FrameSettingsField.MotionBlur, FrameSettingsField.DepthOfField, FrameSettingsField.LensDistortion,
        FrameSettingsField.ChromaticAberration, FrameSettingsField.PaniniProjection,
        // Centered on each eye's off-axis texture rather than the lens, so on this asymmetric headset it
        // darkens a different, head-locked band in each eye.
        FrameSettingsField.Vignette,
        // Per-eye noise that doesn't match between the eyes and shimmers.
        FrameSettingsField.FilmGrain,
    };

    private static readonly FrameSettingsField[] PerformanceOff =
    {
        FrameSettingsField.Volumetrics, FrameSettingsField.ReprojectionForVolumetrics, FrameSettingsField.VolumetricClouds,
        FrameSettingsField.SSR, FrameSettingsField.TransparentSSR, FrameSettingsField.SSGI, FrameSettingsField.ContactShadows,
        // Follows the game preset (full-res 16-step GTAO at High) and HDRP 14's GTAO assumes a centered
        // projection, so on the off-axis eyes it costs a lot and its halos sit slightly wrong.
        FrameSettingsField.SSAO,
    };

    /// <summary>
    /// Match the game camera's look, then override what doesn't work in VR. Interop note: never use
    /// HDRP's ref-returning renderingPathCustomFrameSettings property from here; Il2CppInterop returns a
    /// temporary boxed copy, so writes through it are silently lost. Use the m_ backing field.
    /// </summary>
    private void ConfigureHdCamera(HDAdditionalCameraData hd, HDAdditionalCameraData src, int level)
    {
        if (src != null)
        {
            if (level == 0)
            {
                // CopyTo (present in this build) copies every camera-data field: volume masks, clear color,
                // frame settings and mask, and the game's TAA tuning. The VR overrides are applied on top.
                src.CopyTo(hd);
            }
            else
            {
                hd.volumeLayerMask = src.volumeLayerMask;
                hd.volumeAnchorOverride = src.volumeAnchorOverride;
                hd.clearColorMode = src.clearColorMode;
                hd.backgroundColorHDR = src.backgroundColorHDR;
            }
        }

        // Undo anything that would resize or offset the eye's render (upscalers, screen overrides).
        hd.screenSizeOverride = _freshScreenSizeOverride;
        hd.screenCoordScaleBias = _freshScreenCoordScaleBias;
        hd.materialMipBias = _freshMaterialMipBias;

        FrameSettings fs = hd.m_RenderingPathCustomFrameSettings;
        FrameSettingsOverrideMask mask = hd.customRenderingSettings ? hd.renderingPathCustomFrameSettingsOverrideMask : default;

        foreach (FrameSettingsField field in ComfortOff)
            SetFrameSetting(ref fs, ref mask, field, false);
        if (Settings.EyePerformanceMode.Value)
        {
            foreach (FrameSettingsField field in PerformanceOff)
                SetFrameSetting(ref fs, ref mask, field, false);
        }
        if (level >= 2)
        {
            SetFrameSetting(ref fs, ref mask, FrameSettingsField.CustomPostProcess, false);
            SetFrameSetting(ref fs, ref mask, FrameSettingsField.CustomPass, false);
        }
        if (level >= 3)
        {
            foreach (FrameSettingsField field in new[]
                     {
                         FrameSettingsField.Distortion, FrameSettingsField.RoughDistortion, FrameSettingsField.Refraction,
                         FrameSettingsField.SSAO, FrameSettingsField.ScreenSpaceShadows,
                     })
                SetFrameSetting(ref fs, ref mask, field, false);
        }
        if (level >= 4)
            SetFrameSetting(ref fs, ref mask, FrameSettingsField.Postprocess, false);
        // Each eye uses an off-axis frustum; HDRP assumes a symmetric one unless told otherwise.
        SetFrameSetting(ref fs, ref mask, FrameSettingsField.AsymmetricProjection, true);

        hd.m_RenderingPathCustomFrameSettings = fs;
        hd.renderingPathCustomFrameSettingsOverrideMask = mask;
        hd.customRenderingSettings = true;

        hd.antialiasing = Settings.EyeAA.Value switch
        {
            EyeAntialiasing.None => HDAdditionalCameraData.AntialiasingMode.None,
            EyeAntialiasing.FXAA => HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing,
            EyeAntialiasing.TAA => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
            EyeAntialiasing.SMAA => HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
            _ => src != null ? src.antialiasing : HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
        };
        hd.allowDynamicResolution = false;
        hd.allowDeepLearningSuperSampling = false;
        // The submit bounds already flip V (SubmitBridge); a forced HDRP flip would undo it.
        hd.flipYMode = HDAdditionalCameraData.FlipYMode.Automatic;
        // Keep HDRP's per-camera history while the eyes are disabled in menus (avoids a realloc hitch).
        hd.hasPersistentHistory = true;
    }

    internal static void SetFrameSetting(ref FrameSettings fs, ref FrameSettingsOverrideMask mask, FrameSettingsField field, bool enabled)
    {
        int bit = (int)field;
        ulong flag = 1UL << (bit & 63);
        if (bit < 64)
        {
            fs.bitDatas.data1 = enabled ? fs.bitDatas.data1 | flag : fs.bitDatas.data1 & ~flag;
            mask.mask.data1 |= flag;
        }
        else
        {
            fs.bitDatas.data2 = enabled ? fs.bitDatas.data2 | flag : fs.bitDatas.data2 & ~flag;
            mask.mask.data2 |= flag;
        }
    }

    private void SetEyesEnabled(bool enabled)
    {
        for (int i = 0; i < 2; i++)
        {
            if (_eyes[i] != null && _eyes[i].enabled != enabled)
                _eyes[i].enabled = enabled;
        }
    }

    /// <summary>
    /// Logs game FPS and SteamVR's view of our frames every few seconds. "timedOut" counts the
    /// times the compositor gave up waiting for us and showed its own environment instead.
    /// </summary>
    private void LogPerfStats()
    {
        _statsFrames++;
        _statsTimer += Time.unscaledDeltaTime;
        if (_statsTimer < StatsInterval)
            return;

        Compositor_CumulativeStats now = ReadCumulativeStats();
        Compositor_CumulativeStats last = now.m_nNumFramePresents >= _lastStats.m_nNumFramePresents ? _lastStats : default;

        var timing = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf<Compositor_FrameTiming>() };
        bool haveTiming = OpenVR.Compositor != null && OpenVR.Compositor.GetFrameTiming(ref timing, 0);
        // Measured on the render thread for every submit: rendered with the latest WaitGetPoses pose
        // (paired), the one before (stale by a frame) or neither (other, e.g. predicted poses).
        var pairing = SubmitBridge.TakePairingStats();

        Plugin.Logger.LogInfo(
            $"Perf: game {_statsFrames / _statsTimer:F0} fps" +
            $" | SteamVR presents={now.m_nNumFramePresents - last.m_nNumFramePresents}" +
            $" dropped={now.m_nNumDroppedFrames - last.m_nNumDroppedFrames}" +
            $" reprojected={now.m_nNumReprojectedFrames - last.m_nNumReprojectedFrames}" +
            $" timedOut={now.m_nNumTimedOut - last.m_nNumTimedOut}" +
            (SubmitBridge.TakeSubmitErrorCount() is var errors && errors > 0 ? $" SUBMIT ERRORS={errors}" : "") +
            (haveTiming ? $" | last frame: interval={timing.m_flClientFrameIntervalMs:F1}ms gpu={timing.m_flTotalRenderGpuMs:F1}ms" : "") +
            $" | poses paired={pairing.paired} stale={pairing.stale} other={pairing.other} syncs={_poseFlushes}" +
            $" ({(_poseFromCompositor ? "compositor" : "predicted")})" +
            (_gameHud.TakeStats() is string hudStats ? $" | {hudStats}" : "") +
            (_desk.TakeStats() is string deskStats ? $" | {deskStats}" : ""));
        _poseFlushes = 0;

        _lastStats = now;
        _statsFrames = 0;
        _statsTimer = 0f;
    }

    private static Compositor_CumulativeStats ReadCumulativeStats()
    {
        var stats = new Compositor_CumulativeStats();
        OpenVR.Compositor?.GetCumulativeStats(ref stats, (uint)Marshal.SizeOf<Compositor_CumulativeStats>());
        return stats;
    }

    /// <summary>Fetch the HMD pose for the frame we are about to render, relative to the recenter point.</summary>
    private bool UpdateHeadPose(out Vector3 headPos, out Quaternion headRot)
    {
        headPos = Vector3.zero;
        headRot = Quaternion.identity;

        TrackedDevicePose_t hmdPose = default;
        bool valid = false;
        _poseFromCompositor = false;

        // Valve's own scheme: render with the poses from the compositor's latest WaitGetPoses (called on
        // the render thread right after the previous submit). That is the pose SteamVR pairs with the next
        // frame we submit, so whatever reprojects downstream starts from the pose we actually rendered.
        CVRCompositor compositor = OpenVR.Compositor;
        if (Settings.HeadPoseSource.Value == PoseSource.Compositor && compositor != null
            && compositor.GetLastPoses(_poses, _noGamePoses) == EVRCompositorError.None)
        {
            hmdPose = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            valid = hmdPose.bPoseIsValid;
            _poseFromCompositor = valid;
        }

        if (!valid)
        {
            float sinceVsync = 0;
            ulong frameCounter = 0;
            _hmd.GetTimeSinceLastVsync(ref sinceVsync, ref frameCounter);

            ETrackedPropertyError propErr = ETrackedPropertyError.TrackedProp_Success;
            float hz = _hmd.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd,
                ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref propErr);
            float vsyncToPhotons = _hmd.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd,
                ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref propErr);
            float frame = hz > 0 ? 1f / hz : 1f / 90f;

            // Submitted at the end of this frame, so predict to the next vsync plus scan-out.
            float predict = frame - sinceVsync + vsyncToPhotons;
            _hmd.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, predict, _poses);
            hmdPose = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            valid = hmdPose.bPoseIsValid;
        }

        if (!valid)
            return false;

        _renderedPose = hmdPose.mDeviceToAbsoluteTracking;
        VRMath.ToUnity(_renderedPose, out Vector3 rawPos, out Quaternion rawRot);

        if (_needsRecenter)
        {
            _needsRecenter = false;
            _recenterPos = rawPos;
            _recenterYawInv = Quaternion.Inverse(Quaternion.Euler(0f, rawRot.eulerAngles.y, 0f));
            // Panels anchored to the VR view (the HUD) are placed from this pose, not from wherever the
            // head happens to point when they first appear.
            _recenterPose = _renderedPose;
            _haveRecenterPose = true;
        }

        headPos = _recenterYawInv * (rawPos - _recenterPos);
        headRot = _recenterYawInv * rawRot;
        return true;
    }

    /// <summary>
    /// The mapping from recentered head space (real meters) to the game world: world = origin +
    /// rigRot * (local * scale). The origin follows the game camera; its yaw turns with it.
    /// </summary>
    private static void GetRig(Camera game, out Vector3 origin, out Quaternion rigRot, out float scale)
    {
        Transform gameT = game.transform;
        GetRig(gameT.position, gameT.rotation, out origin, out rigRot, out scale);
    }

    private static void GetRig(Vector3 cameraPos, Quaternion cameraRot, out Vector3 origin, out Quaternion rigRot, out float scale)
    {
        scale = Settings.WorldScale.Value;
        Quaternion yaw = Quaternion.Euler(0f, cameraRot.eulerAngles.y, 0f);
        rigRot = Settings.FollowCameraPitch.Value ? cameraRot : yaw;
        origin = cameraPos
                 + yaw * new Vector3(0f, 0f, -Settings.BackOffset.Value)
                 + Vector3.up * Settings.HeightOffset.Value;
    }

    /// <summary>
    /// Where the HUD layer goes, seen from where the head was at the last recenter. Normally its center
    /// points at the character's feet. The desktop's center is the camera's look-at point, LookAtHeight
    /// above the feet, so a cursor drawn on the HUD sits a few degrees above the point the game aims at;
    /// the ground cursor shows the exact aim. The distance is capped so the HUD's lowest edge stays above
    /// the ground (see GroundSafeDistance).
    /// </summary>
    private HudAnchor ComputeHudAnchor(Camera game)
    {
        var anchor = new HudAnchor();
        if (!_haveRecenterPose)
            return anchor;

        // The recentered head position, in OpenVR tracking space (Unity z is OpenVR -z).
        Vector3 eye = new Vector3(_recenterPos.x, _recenterPos.y, -_recenterPos.z);
        anchor.Valid = true;
        anchor.Origin = eye;

        bool haveCamera = GameCamera.TryRead(out GameCameraState camera);
        // Lay the HUD out from the camera without the game's screen shake, so it holds still while the
        // world shakes, like the desktop HUD. (The shake only moves the camera's position.)
        Transform gameT = game.transform;
        Vector3 cameraPos = gameT.position;
        Quaternion cameraRot = gameT.rotation;
        if (haveCamera && camera.HaveCameraPos)
        {
            float shake = (gameT.position - camera.CameraPos).magnitude;
            if (shake < MaxScreenShake)
                cameraPos = camera.CameraPos;
            LogScreenShake(shake);
        }

        if (Settings.HudAnchorMode.Value == HudAnchorMode.Feet && haveCamera)
        {
            Vector3 toFeet = WorldToTracking(cameraPos, cameraRot, camera.Feet) - eye;
            float distanceToFeet = toFeet.magnitude;
            if (distanceToFeet > 0.05f)
            {
                anchor.Direction = WithPitchOffset(toFeet / distanceToFeet, Settings.HudPitchOffset.Value);
                anchor.Distance = GroundSafeDistance(cameraPos, cameraRot, camera.Feet, anchor.Direction);
                return anchor;
            }
        }

        // Fallback: the recentered heading, tilted to the game camera's pitch.
        float fx = -_recenterPose.m2, fz = -_recenterPose.m10;
        float len = MathF.Sqrt(fx * fx + fz * fz);
        if (len < 1e-3f) { fx = 0f; fz = -1f; } else { fx /= len; fz /= len; }
        float pitch = HudPitch(game) * Mathf.Deg2Rad;
        anchor.Direction = new Vector3(fx * MathF.Cos(pitch), MathF.Sin(pitch), fz * MathF.Cos(pitch));
        // Without the camera state (briefly after loading into the world) the ground isn't known.
        anchor.Distance = haveCamera
            ? GroundSafeDistance(cameraPos, cameraRot, camera.Feet, anchor.Direction)
            : Settings.HudDistance.Value;
        return anchor;
    }

    /// <summary>
    /// The HUD's distance along <paramref name="dirTracking"/>, capped so its lowest edge stays above
    /// the ground under the character's feet (taken as flat at the feet's height): a point above that
    /// plane is in front of the ground from anywhere above it, so the HUD never looks sunk in. GroundMargin
    /// is how far the lowest edge may go down toward that plane, as a fraction of the head's height above it.
    /// HudLayer scales the width with the distance, so the HUD's half-height per meter of distance is fixed.
    /// </summary>
    private float GroundSafeDistance(Vector3 cameraPos, Quaternion cameraRot, Vector3 feetWorld, Vector3 dirTracking)
    {
        float distance = Settings.HudDistance.Value;
        GetRig(cameraPos, cameraRot, out Vector3 origin, out Quaternion rigRot, out float scale);
        // The direction in world terms (OpenVR to Unity z, undo the recenter yaw, then the rig), which
        // covers FollowCameraPitch and the pitch offset.
        Vector3 dirWorld = rigRot * (_recenterYawInv * new Vector3(dirTracking.x, dirTracking.y, -dirTracking.z));
        float sinDown = -dirWorld.y;
        float cosDown = MathF.Sqrt(MathF.Max(0f, 1f - sinDown * sinDown));
        // The recentered head is at the rig origin; its height above the feet, in real meters.
        float eyeHeight = (origin.y - feetWorld.y) / scale;
        float halfTan = 0.5f * Settings.HudWidth.Value * Screen.height / Screen.width / MathF.Max(distance, 0.05f);
        // How far the lowest edge sits below the eyes per meter of distance. StereoStrength above 1 makes
        // the world read nearer (its disparity is scaled up, while the compositor draws the HUD with your
        // real eye separation), so the HUD comes in by the same factor.
        float drop = MathF.Max(1f, Settings.StereoStrength.Value) * (sinDown + halfTan * cosDown);
        if (eyeHeight > 0f && drop > 1e-4f)
            distance = MathF.Min(distance, Settings.HudGroundMargin.Value * eyeHeight / drop);
        return Mathf.Clamp(distance, 0.2f, 10f);
    }

    /// <summary>Beyond this (game meters) the camera entity and the Unity camera aren't the same camera.</summary>
    private const float MaxScreenShake = 3f;
    private bool _loggedShakeOffset;
    private float _loggedShake;

    /// <summary>Diagnostic: how far the game's screen shake moves the camera (0 when nothing shakes).</summary>
    private void LogScreenShake(float shake)
    {
        if (!_loggedShakeOffset)
        {
            _loggedShakeOffset = true;
            _loggedShake = shake;
            Plugin.Logger.LogInfo($"Unity camera vs camera entity: {shake:F3} game m apart (screen shake; the HUD ignores it).");
        }
        else if (shake > MathF.Max(0.02f, 2f * _loggedShake))
        {
            _loggedShake = shake;
            Plugin.Logger.LogInfo($"Screen shake: up to {shake:F3} game m{(shake >= MaxScreenShake ? " (too far apart to be shake; the HUD follows the Unity camera)" : "")}.");
        }
    }

    /// <summary>A game-world point in OpenVR tracking space, for the rig at the given camera pose (needs a recenter pose).</summary>
    private Vector3 WorldToTracking(Vector3 cameraPos, Quaternion cameraRot, Vector3 world)
    {
        GetRig(cameraPos, cameraRot, out Vector3 origin, out Quaternion rigRot, out float scale);
        Vector3 local = Quaternion.Inverse(rigRot) * (world - origin) / scale;
        Vector3 t = _recenterPos + Quaternion.Inverse(_recenterYawInv) * local;
        return new Vector3(t.x, t.y, -t.z);
    }

    /// <summary>
    /// Cursor placement: while a menu is open (the game shows its menu cursor) the cursor is drawn on the
    /// HUD; in gameplay it lies on the ground at the game's own aim point instead (drawn into the eye images
    /// by DrawEyeOverlays), so you can see where you're aiming in 3D.
    /// </summary>
    private void UpdateCursors(bool inWorld)
    {
        ProjectM.CursorType type = ProjectM.CursorType.Menu_Normal;
        try { type = ProjectM.CursorController._CursorType; } catch { }
        bool gameplayCursor = type == ProjectM.CursorType.Game_Normal;
        _reticleWanted = Settings.ReticleEnabled.Value && inWorld && gameplayCursor && !_screen.Visible && !_overlaysFailed
                         && GameCamera.TryReadAim(out _);

        // The HUD cursor when it's useful: always on the mirror screen, on the HUD layer only for menus
        // (or when the ground cursor isn't available).
        _cursor.Tick(Settings.ScreenCursor.Value && (_screen.Visible || (_hud.Visible && !_reticleWanted)));
    }

    private static Vector3 WithPitchOffset(Vector3 direction, float offsetDegrees)
    {
        if (MathF.Abs(offsetDegrees) < 1e-3f)
            return direction;
        float horizontal = MathF.Sqrt(direction.x * direction.x + direction.z * direction.z);
        if (horizontal < 1e-3f)
            return direction;
        float pitch = MathF.Atan2(direction.y, horizontal) + offsetDegrees * Mathf.Deg2Rad;
        pitch = Mathf.Clamp(pitch, -1.5f, 1.5f);
        float fx = direction.x / horizontal, fz = direction.z / horizontal;
        return new Vector3(fx * MathF.Cos(pitch), MathF.Sin(pitch), fz * MathF.Cos(pitch));
    }

    private void PlaceEyes(Camera game, Vector3 headPos, Quaternion headRot)
    {
        GetRig(game, out Vector3 origin, out Quaternion rigRot, out float scale);

        // The near plane is set in real-world terms: at WorldScale 14 the game camera's 0.3 game-meter
        // near plane is 2 cm from your eyes, so the trees, cliffs and roofs around the camera (which the
        // desktop's narrower view never shows) render huge and unfusable and swing with every head turn.
        float near = Mathf.Max(game.nearClipPlane, Settings.NearClipMeters.Value * scale);
        float far = Mathf.Max(game.farClipPlane, near * 10f);
        float stereo = Settings.StereoStrength.Value;

        for (int i = 0; i < 2; i++)
        {
            EVREye eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
            VRMath.ToUnity(_hmd.GetEyeToHeadTransform(eye), out Vector3 eyePos, out Quaternion eyeRot);

            // StereoStrength < 1 narrows the eye separation (less depth, easier to fuse) without
            // changing the world scale or head movement.
            Vector3 localPos = headPos + headRot * (eyePos * stereo);
            Transform t = _eyes[i].transform;
            t.SetPositionAndRotation(origin + rigRot * (localPos * scale), rigRot * headRot * eyeRot);

            _eyes[i].nearClipPlane = near;
            _eyes[i].farClipPlane = far;
            // The projection below is what renders, but some engine code (LOD selection, for one) reads
            // the camera's FOV and aspect, which would otherwise still be the game camera's 60 degrees.
            _eyes[i].fieldOfView = _eyeFov[i];
            _eyes[i].aspect = _eyeAspect[i];
            _eyes[i].projectionMatrix = VRMath.Projection(_hmd, eye, near, far);
        }
        _lastPlacedView = _eyes[0].worldToCameraMatrix;
    }
}
