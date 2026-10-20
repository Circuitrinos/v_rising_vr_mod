using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VRisingVR;

/// <summary>Points the "openvr_api" DllImports at the copy shipped next to the plugin.</summary>
internal static class OpenVRLoader
{
    private static IntPtr s_handle;

    public static bool Setup(string pluginDir)
    {
        string path = Path.Combine(pluginDir, "openvr_api.dll");
        if (!NativeLibrary.TryLoad(path, out s_handle))
        {
            Plugin.Logger.LogError($"Could not load {path}. VR disabled.");
            return false;
        }

        NativeLibrary.SetDllImportResolver(typeof(OpenVRLoader).Assembly, Resolve);
        return true;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return name == "openvr_api" ? s_handle : IntPtr.Zero;
    }
}
