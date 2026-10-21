using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VRisingVR;

/// <summary>
/// "3D screenshots": saves both eye images SteamVR receives (saved upright), the HUD layer
/// and the desktop view, plus the geometry they were rendered with, for offline inspection.
/// </summary>
internal static class CaptureTool
{
    public static string Directory => Path.Combine(Paths.BepInExRootPath, "VRisingVR_captures");

    /// <summary>Call at the end of a frame (after the eyes rendered). Returns the file stem.</summary>
    public static string Capture(RenderTexture left, RenderTexture right, RenderTexture hud, string info)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string stem = Path.Combine(Directory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));

        SavePng(left, stem + "_left.png");
        SavePng(right, stem + "_right.png");
        if (hud != null)
            SavePng(hud, stem + "_hud.png");
        ScreenCapture.CaptureScreenshot(stem + "_desktop.png");
        File.WriteAllText(stem + "_info.txt", info);
        return stem;
    }

    private static void SavePng(RenderTexture source, string path)
    {
        if (source == null || !source.IsCreated())
            return;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = source;
        var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        // ReadPixels returns the image in Unity's orientation, and EncodeToPNG writes it the right way up.
        texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
        texture.Apply(false, false);
        RenderTexture.active = previous;

        Il2CppStructArray<byte> png = ImageConversion.EncodeToPNG(texture);
        File.WriteAllBytes(path, ToManaged(png));
        UnityEngine.Object.Destroy(texture);
    }

    /// <summary>Bulk copy of an IL2CPP byte[] (element-by-element interop access would take seconds).</summary>
    private static byte[] ToManaged(Il2CppStructArray<byte> array)
    {
        int length = array.Length;
        var result = new byte[length];
        // IL2CPP arrays: object header (klass, monitor), bounds pointer and length, then the elements.
        IntPtr data = IL2CPP.Il2CppObjectBaseToPtrNotNull(array) + 4 * IntPtr.Size;
        Marshal.Copy(data, result, 0, length);
        return result;
    }
}
