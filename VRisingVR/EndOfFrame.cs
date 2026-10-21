using System;
using System.Collections;
using UnityEngine;

namespace VRisingVR;

/// <summary>
/// Runs a callback after WaitForEndOfFrame, i.e. after the frame and its screen-space UI are drawn.
/// Kept out of VRRig because Il2CppInterop can't inject IEnumerator-returning methods into IL2CPP.
/// </summary>
internal static class EndOfFrame
{
    public static IEnumerator Loop(Action callback)
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return endOfFrame;
            try
            {
                callback();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"End-of-frame callback failed: {e}");
            }
        }
    }
}
