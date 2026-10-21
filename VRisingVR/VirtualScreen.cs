using System;
using UnityEngine;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// An opaque panel in VR that mirrors the whole game window (menus, loading screens), captured
/// from the finished back buffer at the end of each frame.
/// </summary>
internal sealed class VirtualScreen
{
    private readonly OverlayPanel _panel = new("vrisingvr.screen", "V Rising", opaque: true);
    private RenderTexture _capture;
    private IntPtr _captureNative;
    private bool _captureDue;
    private bool _haveCapture;
    private bool _loggedFirstFrame;

    public bool Visible => _panel.Visible;

    public void RequestPlacement() => _panel.RequestPlacement();

    /// <summary>Call once per frame from LateUpdate while VR is running.</summary>
    public void Tick(bool show, bool follow, bool havePose, HmdMatrix34_t hmdPose)
    {
        if (!show)
        {
            if (_panel.Visible)
            {
                _panel.Hide();
                _captureDue = false;
                _haveCapture = false;
            }
            return;
        }

        if (!_panel.EnsureCreated(Settings.ScreenWidth.Value, Settings.ScreenFlipVertical.Value))
            return;
        if (_panel.Placed && MathF.Abs(_panel.PlacedDistance - Settings.ScreenDistance.Value) > 0.005f)
            _panel.RequestPlacement();
        _panel.SetWidth(Settings.ScreenWidth.Value);
        if (!_panel.UpdatePlacement(follow, havePose, hmdPose, Settings.ScreenDistance.Value, 0f))
            return;
        if (!_panel.Visible)
            _panel.Show();

        EnsureCaptureSize();

        // The capture below ran at the end of the previous frame (see CaptureAtEndOfFrame), so it is
        // complete by the time the render thread reaches this event, and the next capture comes after it.
        if (_haveCapture)
        {
            _panel.SetTexture(_captureNative);
            _haveCapture = false;
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Plugin.Logger.LogInfo($"Virtual screen receiving frames ({_capture.width}x{_capture.height}).");
            }
        }

        // Every frame: SteamVR already paces the game to the headset, and a rate throttle aliased
        // with it and skipped about a third of the frames.
        _captureDue = true;
    }

    /// <summary>
    /// Called after WaitForEndOfFrame, when the frame including its screen-space UI is complete.
    /// Capturing any earlier (e.g. in LateUpdate) reads a back buffer that hasn't been drawn yet.
    /// </summary>
    public void CaptureAtEndOfFrame()
    {
        if (!_panel.Visible || !_captureDue || _capture == null)
            return;
        ScreenCapture.CaptureScreenshotIntoRenderTexture(_capture);
        _captureDue = false;
        _haveCapture = true;
    }

    public void Destroy()
    {
        _panel.Destroy();
        if (_capture != null)
        {
            _capture.Release();
            UnityEngine.Object.Destroy(_capture);
            _capture = null;
        }
        _captureNative = IntPtr.Zero;
        _captureDue = false;
        _haveCapture = false;
        _loggedFirstFrame = false;
    }

    private void EnsureCaptureSize()
    {
        int w = Screen.width;
        int h = Screen.height;
        if (_capture != null && _capture.IsCreated() && _capture.width == w && _capture.height == h)
            return;

        // Release is ordered on the render thread after any overlay update already queued for it.
        if (_capture != null)
        {
            _capture.Release();
            UnityEngine.Object.Destroy(_capture);
        }

        _capture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "VRisingVR_Screen",
            // Survive the UnloadUnusedAssets that runs on scene loads.
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        _capture.Create();
        _captureNative = _capture.GetNativeTexturePtr();
        _haveCapture = false;
    }
}
