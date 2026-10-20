using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace VRisingVR;

[BepInPlugin(Guid, Name, Version)]
[BepInProcess("VRising.exe")] // client only; never loads into VRisingServer.exe
public class Plugin : BasePlugin
{
    public const string Guid = "vrisingvr.tabletop";
    public const string Name = "VRisingVR";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        Settings.Bind(Config);

        string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (!OpenVRLoader.Setup(pluginDir))
            return;

        ClassInjector.RegisterTypeInIl2Cpp<VRRig>();
        AddComponent<VRRig>();

        Logger.LogInfo($"{Name} {Version} loaded. Press {Settings.ToggleKey.Value} to toggle VR.");
    }
}
