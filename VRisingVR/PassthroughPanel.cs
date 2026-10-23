using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// Passthrough for the Steam Frame: shows the headset's own camera view, streamed by the VRisingVR passthrough
/// server on the headset (see PassthroughFeed), on a panel fixed to the head. The panel spans the camera's field
/// of view, so the desk, keyboard and mouse appear about where they really are. It fades in while you look down,
/// or stays on after the toggle key.
/// </summary>
internal sealed class PassthroughPanel
{
    private const float FadeSeconds = 0.15f;
    private const float LookDownHysteresis = 5f;
    private const long StaleFrameMs = 1000;

    private readonly OverlayPanel _panel = new("vrisingvr.passthrough", "VRisingVR passthrough", opaque: true, sortOrder: 10);
    private readonly PassthroughFeed _feed = new();

    // LoadImage may recreate its texture on every decode, so each frame is copied into a texture whose GPU
    // resource never changes, and only that one is handed to SteamVR (GetNativeTexturePtr is a full sync here).
    private Texture2D _decoded;
    private Texture2D _shown;
    private IntPtr _shownNative;
    private bool _haveImage;
    private int _seenSeq;

    private bool _pinned;
    private bool _lookingDown;
    private float _alpha;
    private int _loggedStatus = -1;
    private int _framesShown;
    private int _decodeFailures;
    private bool _loggedDecodeError;
    private bool _warnedNoToken;
    private float _leftEyeX = float.NaN;

    private static bool Configured => Settings.PassthroughEnabled.Value && !string.IsNullOrWhiteSpace(Settings.PassthroughToken.Value);

    /// <summary>The toggle key: keep the camera view on, or go back to showing it only while looking down. Returns a toast.</summary>
    public string TogglePinned()
    {
        if (!Configured)
            return "Passthrough isn't set up (see the README)";
        _pinned = !_pinned;
        Plugin.Logger.LogInfo($"Passthrough {(_pinned ? "pinned on" : "back to look-down only")}.");
        if (!_pinned)
            return Settings.PassthroughLookDown.Value ? "Passthrough: look down to see your desk" : "Passthrough off";
        return _feed.MillisecondsSinceFrame < StaleFrameMs ? "Passthrough on" : "Passthrough on, waiting for the headset camera";
    }

    /// <summary>Call once per frame from LateUpdate while VR is running.</summary>
    public void Tick(bool havePose, HmdMatrix34_t hmd)
    {
        if (!Configured)
        {
            if (Settings.PassthroughEnabled.Value && !_warnedNoToken)
            {
                _warnedNoToken = true;
                Plugin.Logger.LogWarning("Passthrough is enabled but Passthrough.Token is empty: copy the token from the headset (see the README).");
            }
            if (_feed.Running)
                _feed.Stop();
            Hide();
            return;
        }

        string host = Settings.PassthroughHost.Value.Trim();
        int port = Settings.PassthroughPort.Value;
        string token = Settings.PassthroughToken.Value.Trim();
        int width = Mathf.Clamp(Settings.PassthroughResolution.Value, 160, 1920);
        int fps = Mathf.Clamp(Settings.PassthroughFps.Value, 1, 60);
        if (!_feed.Running || !_feed.Matches(host, port, token, width, fps))
            _feed.Start(host, port, token, width, fps);

        (string status, int version) = _feed.Status;
        if (version != _loggedStatus)
        {
            _loggedStatus = version;
            Plugin.Logger.LogInfo($"Passthrough: {status}.");
        }

        // Looking down past the angle shows the desk; a few degrees of hysteresis stop it flickering at the edge.
        float angle = Settings.PassthroughLookDownAngle.Value;
        if (Settings.PassthroughLookDown.Value && havePose)
        {
            float pitch = MathF.Asin(Mathf.Clamp(-hmd.m6, -1f, 1f)) * Mathf.Rad2Deg;
            _lookingDown = _lookingDown ? pitch < -(angle - LookDownHysteresis) : pitch < -angle;
        }
        else
        {
            _lookingDown = false;
        }

        // Never show a stale picture of the desk: hide when the stream stops.
        bool fresh = _feed.MillisecondsSinceFrame < StaleFrameMs;
        bool wanted = (_pinned || _lookingDown) && fresh && havePose;
        float target = wanted ? Mathf.Clamp01(Settings.PassthroughOpacity.Value) : 0f;
        _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime / FadeSeconds);

        if (_alpha <= 0.001f)
        {
            Hide();
            return;
        }
        if (!_panel.EnsureCreated(1f, flipVertical: true))
            return;
        // Decode only while the panel shows: the decode runs on the main thread.
        UpdateImage();
        if (!_haveImage)
            return;
        Place();
        _panel.SetAlpha(_alpha);
        if (!_panel.Visible)
            _panel.Show();
    }

    /// <summary>For the Perf log line; null while passthrough is off.</summary>
    public string TakeStats()
    {
        if (!_feed.Running)
            return null;
        (int frames, long bytes) = _feed.TakeStats();
        int shown = _framesShown;
        _framesShown = 0;
        string failures = _decodeFailures > 0 ? $", {_decodeFailures} bad frames" : "";
        _decodeFailures = 0;
        return frames == 0
            ? $"passthrough: {_feed.Status.Text}"
            : $"passthrough: received {frames} frames ({bytes / 1024} KB), shown {shown}{failures}";
    }

    public void Destroy()
    {
        _feed.Stop();
        _panel.Destroy();
        if (_decoded != null)
            UnityEngine.Object.Destroy(_decoded);
        if (_shown != null)
            UnityEngine.Object.Destroy(_shown);
        _decoded = null;
        _shown = null;
        _shownNative = IntPtr.Zero;
        _haveImage = false;
        _seenSeq = 0;
        _pinned = false;
        _lookingDown = false;
        _alpha = 0f;
        _loggedStatus = -1;
        _leftEyeX = float.NaN;
    }

    private void Hide()
    {
        if (_panel.Visible)
            _panel.Hide();
        _alpha = 0f;
    }

    private void UpdateImage()
    {
        if (!_feed.TryTakeFrame(ref _seenSeq, out byte[] jpeg))
            return;
        try
        {
            _decoded ??= new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = "VRisingVR_PassthroughDecode",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            if (!ImageConversion.LoadImage(_decoded, new Il2CppStructArray<byte>(jpeg), false))
            {
                _decodeFailures++;
                return;
            }

            int w = _decoded.width;
            int h = _decoded.height;
            if (_shown == null || _shown.width != w || _shown.height != h || _shown.format != _decoded.format)
            {
                // Destroying the old texture is ordered on the render thread after any update already queued for it.
                if (_shown != null)
                    UnityEngine.Object.Destroy(_shown);
                _shown = new Texture2D(w, h, _decoded.format, false)
                {
                    name = "VRisingVR_Passthrough",
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _shown.Apply(false, true); // create the GPU texture and drop the CPU copy
                _shownNative = _shown.GetNativeTexturePtr();
                Plugin.Logger.LogInfo($"Passthrough: showing {w}x{h} camera frames.");
            }
            Graphics.CopyTexture(_decoded, _shown);
            _panel.SetTexture(_shownNative);
            _haveImage = true;
            _framesShown++;
        }
        catch (Exception e)
        {
            _decodeFailures++;
            if (!_loggedDecodeError)
            {
                _loggedDecodeError = true;
                Plugin.Logger.LogError($"Passthrough: could not show a camera frame: {e}");
            }
        }
    }

    /// <summary>
    /// SteamVR's camera image is the left eye's view, a pinhole view, so a flat panel at distance d in front of the
    /// left eye, perpendicular to the view direction, reproduces it for that eye when each point sits at
    /// d * tan(angle). The left edge is cropped where the image smears (beyond the camera's coverage).
    /// </summary>
    private void Place()
    {
        if (float.IsNaN(_leftEyeX))
        {
            _leftEyeX = 0f;
            try { _leftEyeX = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Left).m3; }
            catch { }
            Plugin.Logger.LogInfo($"Passthrough: placing the camera view in front of the left eye ({_leftEyeX * 1000f:F0} mm).");
        }
        float d = Mathf.Clamp(Settings.PassthroughDistance.Value, 0.2f, 5f);
        float crop = Mathf.Clamp(Settings.PassthroughCropLeft.Value, 0f, 0.9f);
        float half = MathF.Tan(Mathf.Clamp(Settings.PassthroughFieldOfView.Value, 20f, 170f) * 0.5f * Mathf.Deg2Rad);
        float cx = MathF.Tan(Settings.PassthroughHorizontalOffset.Value * Mathf.Deg2Rad);
        float cy = MathF.Tan(Settings.PassthroughVerticalOffset.Value * Mathf.Deg2Rad);
        float left = cx - half;
        float right = cx + half;
        left += crop * (right - left);

        _panel.SetTextureBounds(crop, 1f, flipVertical: true);
        _panel.SetWidth(d * (right - left));
        var m = new HmdMatrix34_t
        {
            m0 = 1f, m3 = _leftEyeX + d * (left + right) * 0.5f,
            m5 = 1f, m7 = d * cy,
            m10 = 1f, m11 = -d,
        };
        _panel.PlaceRelativeToHmd(m);
    }
}
