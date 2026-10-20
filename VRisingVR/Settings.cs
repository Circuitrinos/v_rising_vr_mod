using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace VRisingVR;

internal static class Settings
{
    public static ConfigEntry<bool> AutoStart;
    public static ConfigEntry<float> WorldScale;
    public static ConfigEntry<float> HeightOffset;
    public static ConfigEntry<float> BackOffset;
    public static ConfigEntry<bool> FollowCameraPitch;
    public static ConfigEntry<float> RenderScale;

    public static ConfigEntry<Key> ToggleKey;
    public static ConfigEntry<Key> RecenterKey;
    public static ConfigEntry<Key> ScaleDownKey;
    public static ConfigEntry<Key> ScaleUpKey;
    public static ConfigEntry<float> ScaleStep;

    public static void Bind(ConfigFile cfg)
    {
        AutoStart = cfg.Bind("General", "AutoStart", false,
            "Start VR automatically when the game launches. Otherwise press the toggle key.");

        WorldScale = cfg.Bind("View", "WorldScale", 8f, new ConfigDescription(
            "Game meters per real meter. Higher = the world looks smaller, like a tabletop.",
            new AcceptableValueRange<float>(1f, 50f)));
        HeightOffset = cfg.Bind("View", "HeightOffset", 0f,
            "Extra height (game meters) added above the game camera position.");
        BackOffset = cfg.Bind("View", "BackOffset", 0f,
            "Extra distance (game meters) pulled back from the game camera, along its facing direction.");
        FollowCameraPitch = cfg.Bind("View", "FollowCameraPitch", false,
            "Tilt the VR view with the game camera's downward pitch. Off keeps the horizon level (more comfortable); look down to see the board.");
        RenderScale = cfg.Bind("View", "RenderScale", 1f, new ConfigDescription(
            "Multiplier on SteamVR's recommended per-eye resolution. Applied when VR starts.",
            new AcceptableValueRange<float>(0.5f, 2f)));

        ToggleKey = cfg.Bind("Keys", "ToggleVR", Key.F8, "Start/stop VR.");
        RecenterKey = cfg.Bind("Keys", "Recenter", Key.F9, "Re-center your head on the game camera.");
        ScaleDownKey = cfg.Bind("Keys", "ScaleDown", Key.F10, "Decrease WorldScale (world looks bigger).");
        ScaleUpKey = cfg.Bind("Keys", "ScaleUp", Key.F11, "Increase WorldScale (world looks smaller).");
        ScaleStep = cfg.Bind("Keys", "ScaleStep", 1f, "WorldScale change per key press.");
    }
}
