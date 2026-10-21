using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace VRisingVR;

internal enum EyeAntialiasing { Game, None, FXAA, SMAA, TAA }

internal enum HudAnchorMode
{
    /// <summary>Center the HUD on the character's feet (the desktop's center is the look-at point, LookAtHeight above them).</summary>
    Feet,
    /// <summary>Tilt the HUD with the game camera's pitch.</summary>
    CameraPitch,
}

/// <summary>What the flat VR HUD shows of the game's 2D overhead layer while the health bars are drawn in 3D.</summary>
internal enum OverheadHudMode
{
    /// <summary>Cut out the 2D entries that are drawn in 3D (bars, names, levels, icons); the interact prompt stays.</summary>
    CutBars,
    /// <summary>Leave the whole overhead layer off (also names, levels and the interact prompt).</summary>
    HideCanvas,
    /// <summary>Show it as the desktop does (the bars then show twice).</summary>
    Keep,
}

/// <summary>Where the head pose each frame is rendered with comes from.</summary>
internal enum PoseSource
{
    /// <summary>The compositor's latest WaitGetPoses render pose (what SteamVR pairs with the next frame; Valve's Unity plugin does this).</summary>
    Compositor,
    /// <summary>Our own prediction from GetDeviceToAbsoluteTrackingPose.</summary>
    Predicted,
}

internal static class Settings
{
    public static ConfigFile File;
    public static ConfigEntry<PoseSource> HeadPoseSource;
    public static ConfigEntry<Key> CaptureKey;
    public static ConfigEntry<Key> SettingsKey;

    public static ConfigEntry<bool> AutoStart;
    public static ConfigEntry<float> WorldScale;
    public static ConfigEntry<float> HeightOffset;
    public static ConfigEntry<float> BackOffset;
    public static ConfigEntry<bool> FollowCameraPitch;
    public static ConfigEntry<float> RenderScale;
    public static ConfigEntry<bool> DesktopMirror;
    public static ConfigEntry<float> NearClipMeters;
    public static ConfigEntry<float> StereoStrength;
    public static ConfigEntry<EyeAntialiasing> EyeAA;
    public static ConfigEntry<bool> EyePerformanceMode;

    public static ConfigEntry<bool> HudEnabled;
    public static ConfigEntry<float> HudDistance;
    public static ConfigEntry<float> HudWidth;
    public static ConfigEntry<bool> HudFollowCameraPitch;
    public static ConfigEntry<float> HudPitchOffset;
    public static ConfigEntry<HudAnchorMode> HudAnchorMode;
    public static ConfigEntry<float> HudGroundMargin;
    public static ConfigEntry<bool> ReticleEnabled;
    public static ConfigEntry<float> ReticleSize;
    public static ConfigEntry<float> ReticleHiddenAlpha;

    public static ConfigEntry<bool> BarsEnabled;
    public static ConfigEntry<OverheadHudMode> BarsHudPanel;
    public static ConfigEntry<float> BarWidthMeters;
    public static ConfigEntry<float> BarMinAngle;
    public static ConfigEntry<float> BarMaxAngle;
    public static ConfigEntry<float> BarLiftMeters;

    public static ConfigEntry<bool> ScreenInMenus;
    public static ConfigEntry<bool> ScreenInWorld;
    public static ConfigEntry<float> ScreenDistance;
    public static ConfigEntry<float> ScreenWidth;
    public static ConfigEntry<bool> ScreenFlipVertical;
    public static ConfigEntry<bool> ScreenCursor;

    public static ConfigEntry<Key> ToggleKey;
    public static ConfigEntry<Key> ScreenToggleKey;
    public static ConfigEntry<Key> IsolationKey;
    public static ConfigEntry<int> EyeIsolation;
    public static ConfigEntry<Key> RecenterKey;
    public static ConfigEntry<Key> ScaleDownKey;
    public static ConfigEntry<Key> ScaleUpKey;
    public static ConfigEntry<float> ScaleStep;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        HeadPoseSource = cfg.Bind("View", "PoseSource", PoseSource.Compositor,
            "Compositor: render with SteamVR's own pose for each frame (recommended). Predicted: the mod's own prediction.");
        CaptureKey = cfg.Bind("Keys", "Capture3D", Key.F5,
            "Save both eye images, the HUD layer and the desktop view to BepInEx/VRisingVR_captures, for diagnosis.");
        SettingsKey = cfg.Bind("Keys", "SettingsPanel", Key.F4, "Open/close the in-game VR settings panel.");

        AutoStart = cfg.Bind("General", "AutoStart", true,
            "Start VR automatically when the game launches. The toggle key turns it off and on.");

        WorldScale = cfg.Bind("View", "WorldScale", 8f, new ConfigDescription(
            "Game meters per real meter. Higher = the world looks smaller, like a tabletop.",
            new AcceptableValueRange<float>(1f, 50f)));
        HeightOffset = cfg.Bind("View", "HeightOffset", 0f,
            "Extra height (game meters) added above the game camera position.");
        BackOffset = cfg.Bind("View", "BackOffset", 0f,
            "Extra distance (game meters) pulled back from the game camera, along its facing direction.");
        FollowCameraPitch = cfg.Bind("View", "FollowCameraPitch", false,
            "Tilt the VR view with the game camera's downward pitch. Off keeps the horizon level (more comfortable); look down to see the board.");
        RenderScale = cfg.Bind("View", "RenderScale", 0.7f, new ConfigDescription(
            "Multiplier on SteamVR's recommended per-eye resolution (each eye is a full HDRP render). Applied when VR starts.",
            new AcceptableValueRange<float>(0.5f, 2f)));
        DesktopMirror = cfg.Bind("View", "DesktopMirror", true,
            "While VR runs in the world, skip the game's own desktop render and show the left eye in the window (with the " +
            "game's UI) instead. Saves a whole extra render each frame. Off: the desktop shows the game as usual.");
        NearClipMeters = cfg.Bind("View", "NearClipMeters", 0.3f, new ConfigDescription(
            "Nothing closer than this many real-world meters to your eyes is drawn (scaled by WorldScale). " +
            "Keeps trees, cliffs and roofs around the camera from filling your view.",
            new AcceptableValueRange<float>(0.02f, 3f)));
        StereoStrength = cfg.Bind("View", "StereoStrength", 1f, new ConfigDescription(
            "Eye separation multiplier. Lower = less depth, easier on the eyes; 1 = true to scale.",
            new AcceptableValueRange<float>(0f, 1.5f)));
        EyeAA = cfg.Bind("View", "Antialiasing", EyeAntialiasing.SMAA,
            "Anti-aliasing for the VR eye views. TAA smears during head turns at VR frame rates; Game copies the game's setting.");
        EyePerformanceMode = cfg.Bind("View", "PerformanceMode", true,
            "Turn off the most expensive HDRP effects in the eye views: volumetric fog and clouds, screen-space reflections and GI, contact shadows.");

        HudEnabled = cfg.Bind("HUD", "Enabled", true,
            "In the world, show the game's HUD, pause menu, map and other UI as a transparent layer over the VR view.");
        HudDistance = cfg.Bind("HUD", "Distance", 1.1f,
            "Distance of the HUD layer in meters. Keep it nearer than the board, or the HUD is drawn over things that look closer than it.");
        HudWidth = cfg.Bind("HUD", "Width", 1.2f,
            "Width of the HUD layer in meters at the full Distance (1.2 at 1.1 m spans about 57 degrees). " +
            "When the ground pulls the HUD closer, the width shrinks with it so the HUD keeps its size in view.");
        HudFollowCameraPitch = cfg.Bind("HUD", "FollowCameraPitch", true,
            "Tilt the HUD down to where the game camera looks (your character), so it frames the board like the desktop HUD.");
        HudPitchOffset = cfg.Bind("HUD", "PitchOffset", 0f, "Extra tilt of the HUD in degrees (negative = lower).");
        HudAnchorMode = cfg.Bind("HUD", "Anchor", VRisingVR.HudAnchorMode.Feet,
            "Feet: center the HUD on your character's feet. CameraPitch: tilt with the game camera.");
        ReticleEnabled = cfg.Bind("Cursor", "GroundCursor", true,
            "In gameplay, show the aim cursor on the ground in 3D, where your attacks go, instead of on the HUD. " +
            "The HUD cursor comes back while a menu (inventory, map, ...) is open.");
        ReticleSize = cfg.Bind("Cursor", "GroundCursorSize", 0.4f, new ConfigDescription(
            "Width of the ground cursor in game meters, so it keeps its size relative to the world (a character is " +
            "about 1.8 game meters tall; 0.4 matches the desktop cursor at normal zoom).", new AcceptableValueRange<float>(0.1f, 3f)));
        ReticleHiddenAlpha = cfg.Bind("Cursor", "GroundCursorHiddenAlpha", 0.3f, new ConfigDescription(
            "Opacity of the parts of the ground cursor hidden behind characters, trees, etc. The game aims through " +
            "characters at the ground behind them, so this keeps the aim point visible when you target an enemy. " +
            "It also shows faintly over your own character; 0 hides those parts completely.",
            new AcceptableValueRange<float>(0f, 1f)));
        HudGroundMargin = cfg.Bind("HUD", "GroundMargin", 0.8f, new ConfigDescription(
            "Keeps the HUD from sinking into the ground when zoomed in: its lowest edge may come down at most this fraction " +
            "of the way from your eyes to the ground under your character, and the HUD moves closer instead. " +
            "Assumes flat ground at the character's feet, so a slope rising toward you can still cut in. " +
            "Needs the game's camera state, so it is off for a moment after loading into the world.",
            new AcceptableValueRange<float>(0.2f, 1f)));

        BarsEnabled = cfg.Bind("Bars", "Enabled", true,
            "Draw the game's overhead health bars, names and levels (yours and other characters') above the characters in the 3D view.");
        BarsHudPanel = cfg.Bind("Bars", "HudPanel", OverheadHudMode.CutBars,
            "While the 3D bars are on: CutBars moves the game's overhead bars, names, levels, icons and blood types from the " +
            "flat VR HUD into 3D (the interact prompt stays on the HUD); HideCanvas leaves the game's whole overhead layer off " +
            "the HUD and draws plain bars in 3D, without names; Keep shows the overhead layer on the HUD as the desktop does. " +
            "The desktop is never changed.");
        BarWidthMeters = cfg.Bind("Bars", "WidthMeters", 0.08f, new ConfigDescription(
            "Width of a bar on the tabletop in real-world meters (it scales with the world). The name, level and icons " +
            "keep their size relative to the bar, as on the desktop.", new AcceptableValueRange<float>(0.01f, 0.5f)));
        BarMinAngle = cfg.Bind("Bars", "MinAngleDegrees", 3.5f, new ConfigDescription(
            "Bars never look narrower than this many degrees, so far ones stay readable. Set it equal to MaxAngleDegrees for a constant size in view.",
            new AcceptableValueRange<float>(0.5f, 30f)));
        BarMaxAngle = cfg.Bind("Bars", "MaxAngleDegrees", 12f, new ConfigDescription(
            "Bars never look wider than this many degrees (close up).", new AcceptableValueRange<float>(0.5f, 45f)));
        BarLiftMeters = cfg.Bind("Bars", "LiftMeters", 0.006f, new ConfigDescription(
            "Extra real-world height above the point where the game anchors its own bar.", new AcceptableValueRange<float>(-0.1f, 0.2f)));

        ScreenInMenus = cfg.Bind("Screen", "ShowInMenus", true,
            "Show a virtual screen that mirrors the game window in the main menu and loading screens.");
        ScreenInWorld = cfg.Bind("Screen", "ShowInWorld", false,
            "Also show the virtual screen in the world, so the HUD, pause menu and map are visible. F7 toggles it.");
        ScreenDistance = cfg.Bind("Screen", "Distance", 2.5f,
            "Distance of the virtual screen in meters. It is placed in front of you when first shown or re-centered.");
        ScreenWidth = cfg.Bind("Screen", "Width", 3f, "Width of the virtual screen in meters.");
        ScreenFlipVertical = cfg.Bind("Screen", "FlipVertical", false,
            "Flip the virtual screen vertically. Use this if it appears upside down.");
        ScreenCursor = cfg.Bind("Screen", "ShowCursor", true, "Draw the mouse cursor on the virtual screen.");

        ToggleKey = cfg.Bind("Keys", "ToggleVR", Key.F8, "Start/stop VR.");
        IsolationKey = cfg.Bind("Keys", "CycleEyeIsolation", Key.F6,
            "Diagnostics: cycle how much of the game's rendering the VR eye views use (see Debug.EyeIsolation).");
        EyeIsolation = cfg.Bind("Debug", "EyeIsolation", 0, new ConfigDescription(
            "0 = normal; 1 = don't copy the game camera's HDRP settings; 2 = also no custom post-processing/passes; " +
            "3 = also no distortion, refraction, SSAO or screen-space shadows; 4 = also no post-processing at all.",
            new AcceptableValueRange<int>(0, 4)));
        ScreenToggleKey = cfg.Bind("Keys", "ToggleScreen", Key.F7,
            "Show/hide the virtual screen while in the world (e.g. to see the HUD).");
        RecenterKey = cfg.Bind("Keys", "Recenter", Key.F9, "Re-center your head on the game camera.");
        ScaleDownKey = cfg.Bind("Keys", "ScaleDown", Key.F10, "Decrease WorldScale (world looks bigger).");
        ScaleUpKey = cfg.Bind("Keys", "ScaleUp", Key.F11, "Increase WorldScale (world looks smaller).");
        ScaleStep = cfg.Bind("Keys", "ScaleStep", 1f, "WorldScale change per key press.");
    }
}
