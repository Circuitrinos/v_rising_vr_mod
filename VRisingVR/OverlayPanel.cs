using System;
using UnityEngine;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// A flat SteamVR overlay that shows a copy of the game window, used by both the virtual screen
/// (opaque mirror) and the HUD layer (transparent UI). The compositor draws it on top of the scene,
/// independently of HDRP. Handles placement in front of the viewer, optional gaze-follow and
/// render-thread texture updates. The mouse cursor arrives inside the texture (see VrCursor).
/// </summary>
internal sealed class OverlayPanel
{
    private const float FollowMaxAngleCos = 0.7071f; // 45 degrees
    private const float FollowMaxDistanceError = 1.0f;
    private const float FollowDelay = 0.75f;

    private readonly string _key;
    private readonly string _name;
    private readonly bool _opaque;
    private readonly uint _sortOrder;

    private ulong _overlay;
    private bool _createFailed;
    private bool _visible;
    private bool _placed;
    private float _x, _z;
    private float _placedPitch;
    private float _placedDistance;
    private float _width = -1f;
    private float _lookAwayTime;
    private float _alpha = 1f;
    private VRTextureBounds_t _bounds;
    private HmdMatrix34_t _hmdRelative;

    /// <param name="opaque">Ignore the texture's alpha (a mirror of the back buffer) rather than treating it as premultiplied (UI layer).</param>
    /// <param name="sortOrder">Overlays with a higher sort order are drawn on top.</param>
    public OverlayPanel(string key, string name, bool opaque, uint sortOrder = 0)
    {
        _key = key;
        _name = name;
        _opaque = opaque;
        _sortOrder = sortOrder;
    }

    public bool Visible => _visible;

    public bool Placed => _placed;

    public float PlacedPitch => _placedPitch;

    public float PlacedDistance => _placedDistance;

    public void RequestPlacement() => _placed = false;

    /// <summary>Apply a width change live (e.g. from the settings panel).</summary>
    public void SetWidth(float widthMeters)
    {
        if (_overlay == 0 || MathF.Abs(widthMeters - _width) < 1e-4f)
            return;
        OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, widthMeters);
        _width = widthMeters;
    }

    public bool EnsureCreated(float widthMeters, bool flipVertical)
    {
        if (_overlay != 0)
            return true;
        if (_createFailed)
            return false;

        CVROverlay overlay = OpenVR.Overlay;
        if (overlay == null)
            return false;

        EVROverlayError err = overlay.CreateOverlay(_key, _name, ref _overlay);
        if (err != EVROverlayError.None)
        {
            Plugin.Logger.LogError($"Could not create overlay '{_key}': {err}");
            _overlay = 0;
            _createFailed = true;
            return false;
        }

        overlay.SetOverlayWidthInMeters(_overlay, widthMeters);
        _width = widthMeters;
        overlay.SetOverlaySortOrder(_overlay, _sortOrder);
        Check(overlay.SetOverlayFlag(_overlay, _opaque ? VROverlayFlags.IgnoreTextureAlpha : VROverlayFlags.IsPremultiplied, true),
            "SetOverlayFlag");
        var bounds = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = flipVertical ? 1f : 0f, vMax = flipVertical ? 0f : 1f };
        overlay.SetOverlayTextureBounds(_overlay, ref bounds);
        _bounds = bounds;

        Plugin.Logger.LogInfo($"Overlay '{_key}' created.");
        return true;
    }

    /// <summary>Overall opacity (0-1) on top of the texture's own alpha.</summary>
    public void SetAlpha(float alpha)
    {
        if (_overlay == 0 || MathF.Abs(alpha - _alpha) < 1e-3f)
            return;
        Check(OpenVR.Overlay.SetOverlayAlpha(_overlay, alpha), "SetOverlayAlpha");
        _alpha = alpha;
    }

    /// <summary>Show only the texture's columns from <paramref name="uMin"/> to <paramref name="uMax"/> (e.g. to crop an edge).</summary>
    public void SetTextureBounds(float uMin, float uMax, bool flipVertical)
    {
        var bounds = new VRTextureBounds_t { uMin = uMin, uMax = uMax, vMin = flipVertical ? 1f : 0f, vMax = flipVertical ? 0f : 1f };
        if (_overlay == 0 || (bounds.uMin == _bounds.uMin && bounds.uMax == _bounds.uMax && bounds.vMin == _bounds.vMin && bounds.vMax == _bounds.vMax))
            return;
        Check(OpenVR.Overlay.SetOverlayTextureBounds(_overlay, ref bounds), "SetOverlayTextureBounds");
        _bounds = bounds;
    }

    /// <summary>
    /// Attach the panel to the headset, so it moves with the head: <paramref name="transform"/> places it in
    /// HMD space (X right, Y up, -Z forward; an identity rotation faces the viewer).
    /// </summary>
    public void PlaceRelativeToHmd(HmdMatrix34_t transform)
    {
        if (_overlay == 0 || (_placed && SameTransform(transform, _hmdRelative)))
            return;
        Check(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlay, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform),
            "SetOverlayTransformTrackedDeviceRelative");
        _hmdRelative = transform;
        _placed = true;
    }

    private static bool SameTransform(HmdMatrix34_t a, HmdMatrix34_t b) =>
        a.m0 == b.m0 && a.m1 == b.m1 && a.m2 == b.m2 && a.m3 == b.m3 && a.m4 == b.m4 && a.m5 == b.m5 &&
        a.m6 == b.m6 && a.m7 == b.m7 && a.m8 == b.m8 && a.m9 == b.m9 && a.m10 == b.m10 && a.m11 == b.m11;

    /// <summary>
    /// Place (or keep) the panel; returns false until a head pose has placed it once. The panel's
    /// center is <paramref name="distance"/> away along the pose's heading, tilted up or down by
    /// <paramref name="pitchDegrees"/> (negative = below eye level), and faces the viewer.
    /// </summary>
    public bool UpdatePlacement(bool follow, bool havePose, HmdMatrix34_t hmd, float distance, float heightOffset, float pitchDegrees = 0f)
    {
        if (follow && havePose && _placed)
            FollowGaze(hmd, distance);
        if (!_placed)
        {
            // Wait for a head pose so the panel never shows up at the tracking origin.
            if (!havePose)
                return false;
            Place(hmd, distance, heightOffset, pitchDegrees);
        }
        return true;
    }

    public void Show()
    {
        Check(OpenVR.Overlay.ShowOverlay(_overlay), "ShowOverlay");
        _visible = true;
        Plugin.Logger.LogInfo($"Overlay '{_key}' shown.");
    }

    public void Hide()
    {
        CVROverlay overlay = OpenVR.Overlay;
        if (overlay != null && _overlay != 0)
            overlay.HideOverlay(_overlay);
        _visible = false;
        Plugin.Logger.LogInfo($"Overlay '{_key}' hidden.");
    }

    /// <summary>Queue a texture update; the copy happens on the render thread (see SubmitBridge).</summary>
    public void SetTexture(IntPtr nativeTexture) => SubmitBridge.QueueOverlayTexture(_overlay, nativeTexture);

    public void Destroy()
    {
        CVROverlay overlay = OpenVR.Overlay;
        if (overlay != null && _overlay != 0)
            overlay.DestroyOverlay(_overlay);
        _overlay = 0;
        _createFailed = false;
        _visible = false;
        _placed = false;
        _width = -1f;
        _alpha = 1f;
        _bounds = default;
    }

    /// <summary>Re-place the panel if it has been out of view (or too far/near) for a moment.</summary>
    private void FollowGaze(HmdMatrix34_t hmd, float distance)
    {
        if (!TryGetYawForward(hmd, out float fx, out float fz))
            return; // looking almost straight up or down: no useful heading

        float dx = _x - hmd.m3;
        float dz = _z - hmd.m11;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        bool away = dist < 1e-3f
                    || (fx * dx + fz * dz) / dist < FollowMaxAngleCos
                    || MathF.Abs(dist - distance) > FollowMaxDistanceError;

        _lookAwayTime = away ? _lookAwayTime + Time.unscaledDeltaTime : 0f;
        if (_lookAwayTime >= FollowDelay)
            _placed = false;
    }

    /// <summary>
    /// Place the panel at <paramref name="origin"/> + <paramref name="direction"/> * distance (OpenVR
    /// tracking space), facing back toward the origin, with its horizontal edges level.
    /// </summary>
    public void PlaceAlong(Vector3 origin, Vector3 direction, float distance)
    {
        Vector3 dir = direction.normalized;
        float horizontal = MathF.Sqrt(dir.x * dir.x + dir.z * dir.z);
        float fx, fz;
        if (horizontal < 1e-3f) { fx = 0f; fz = -1f; }
        else { fx = dir.x / horizontal; fz = dir.z / horizontal; }
        float pitchDegrees = MathF.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        PlaceCore(origin.x, origin.y, origin.z, fx, fz, pitchDegrees, distance, 0f);
    }

    /// <summary>Place the panel in front of the viewer along the pose's heading, tilted by a pitch, facing the viewer.</summary>
    private void Place(HmdMatrix34_t hmd, float distance, float heightOffset, float pitchDegrees)
    {
        if (!TryGetYawForward(hmd, out float fx, out float fz))
        {
            fx = 0f;
            fz = -1f;
        }
        PlaceCore(hmd.m3, hmd.m7, hmd.m11, fx, fz, pitchDegrees, distance, heightOffset);
    }

    private void PlaceCore(float ox, float oy, float oz, float fx, float fz, float pitchDegrees, float distance, float heightOffset)
    {
        float p = pitchDegrees * Mathf.Deg2Rad;
        float c = MathF.Cos(p);
        float s = MathF.Sin(p);
        // Direction from the eyes to the panel's center.
        float dx = fx * c, dy = s, dz = fz * c;

        _x = ox + dx * distance;
        _z = oz + dz * distance;
        float y = oy + dy * distance + heightOffset;

        // Columns: X = right (-fz, 0, fx), Y = Z x X (up, tilted with the pitch), Z = -direction (the
        // overlay's visible side faces the viewer), T = position.
        var m = new HmdMatrix34_t
        {
            m0 = -fz, m1 = -s * fx, m2 = -dx, m3 = _x,
            m4 = 0f, m5 = c, m6 = -dy, m7 = y,
            m8 = fx, m9 = -s * fz, m10 = -dz, m11 = _z,
        };
        Check(OpenVR.Overlay.SetOverlayTransformAbsolute(_overlay, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m),
            "SetOverlayTransformAbsolute");
        _placed = true;
        _placedPitch = pitchDegrees;
        _placedDistance = distance;
        _lookAwayTime = 0f;
    }

    /// <summary>Horizontal forward direction of the HMD (its -Z axis) in OpenVR tracking space.</summary>
    private static bool TryGetYawForward(HmdMatrix34_t hmd, out float fx, out float fz)
    {
        fx = -hmd.m2;
        fz = -hmd.m10;
        float len = MathF.Sqrt(fx * fx + fz * fz);
        if (len < 0.2f)
            return false;
        fx /= len;
        fz /= len;
        return true;
    }

    private static void Check(EVROverlayError err, string call)
    {
        if (err != EVROverlayError.None)
            Plugin.Logger.LogWarning($"{call} returned {err}");
    }
}
