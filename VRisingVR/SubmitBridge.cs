using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Valve.VR;

namespace VRisingVR;

/// <summary>
/// Submits eye textures to the SteamVR compositor on Unity's render thread.
///
/// OpenVR's D3D11 Submit copies the texture on the app's immediate context, and Unity
/// drives that context from its render thread, so calling Submit from the main thread
/// races the renderer. Instead we hand Unity a function pointer via GL.IssuePluginEvent;
/// Unity invokes it on the render thread, in order with the frame's draw commands.
/// </summary>
internal static unsafe class SubmitBridge
{
    private const int SlotCount = 8;
    private const int OverlayEventBase = 100;
    private const int OverlaySlotCount = 16;
    private const int ShutdownEvent = 1000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RenderEventFn(int eventId);

    private struct Frame
    {
        public IntPtr Left;
        public IntPtr Right;
        public HmdMatrix34_t Pose;
    }

    // Kept in a static so the GC never collects the delegate behind the native pointer.
    private static readonly RenderEventFn s_callback = OnRenderEvent;
    private static readonly IntPtr s_callbackPtr = Marshal.GetFunctionPointerForDelegate(s_callback);

    private struct OverlayTexture
    {
        public ulong Overlay;
        public IntPtr Texture;
    }

    private static readonly Frame[] s_frames = new Frame[SlotCount];
    private static int s_nextSlot;

    private static readonly OverlayTexture[] s_overlayTextures = new OverlayTexture[OverlaySlotCount];
    private static int s_nextOverlaySlot;
    private static volatile bool s_overlayErrorLogged;

    // VRTextureWithPose_t starts with the same fields as Texture_t, so a pointer to it can be
    // passed through the bindings' "ref Texture_t" parameter when Submit_TextureWithPose is set.
    private static readonly VRTextureWithPose_t* s_tex =
        (VRTextureWithPose_t*)Marshal.AllocHGlobal(sizeof(VRTextureWithPose_t));

    private static readonly TrackedDevicePose_t[] s_renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private static readonly TrackedDevicePose_t[] s_gamePoses = new TrackedDevicePose_t[0];

    private static volatile bool s_errorLogged;

    // BepInEx's console writer isn't thread-safe, and an exception escaping this native callback kills the
    // game, so render-thread errors are handed to the main thread for logging.
    private static string s_pendingError;

    public static string TakePendingError() => Interlocked.Exchange(ref s_pendingError, null);

    // Every failed Submit, not just the first one that gets logged; reported in the Perf line.
    private static int s_submitErrors;

    public static int TakeSubmitErrorCount() => Interlocked.Exchange(ref s_submitErrors, 0);

    /// <summary>Re-arm the one-shot error reports for a new VR session.</summary>
    public static void ResetErrorState()
    {
        s_haveWaitPose = false;
        Volatile.Write(ref s_posesReturned, Volatile.Read(ref s_submitsQueued));
        s_errorLogged = false;
        s_overlayErrorLogged = false;
        Interlocked.Exchange(ref s_pendingError, null);
        Interlocked.Exchange(ref s_submitErrors, 0);
    }

    private static void ReportError(string message) => Volatile.Write(ref s_pendingError, message);

    // Submit/WaitGetPoses bookkeeping: the main thread must not read the compositor's poses for the
    // next frame until the render thread has returned from the WaitGetPoses that follows our last submit.
    private static int s_submitsQueued;
    private static int s_posesReturned;

    /// <summary>True while a queued submit's WaitGetPoses hasn't returned yet (its poses aren't available).</summary>
    public static bool PosesPending => Volatile.Read(ref s_posesReturned) != Volatile.Read(ref s_submitsQueued);

    /// <summary>After forcing a render-thread sync: everything queued has run.</summary>
    public static void MarkPosesReturned() => Volatile.Write(ref s_posesReturned, Volatile.Read(ref s_submitsQueued));

    // Pose pairing, measured on the render thread: was each submitted frame rendered with the pose from
    // the WaitGetPoses right before it (paired), the one before that (one frame stale), or neither?
    private static HmdMatrix34_t s_lastWaitPose;
    private static HmdMatrix34_t s_previousWaitPose;
    private static bool s_haveWaitPose;
    private static int s_paired, s_stale, s_other;

    public static (int paired, int stale, int other) TakePairingStats() =>
        (Interlocked.Exchange(ref s_paired, 0), Interlocked.Exchange(ref s_stale, 0), Interlocked.Exchange(ref s_other, 0));

    /// <summary>Queue a submit of textures rendered with the given HMD pose.</summary>
    public static void QueueSubmit(IntPtr left, IntPtr right, HmdMatrix34_t pose)
    {
        int slot = s_nextSlot;
        s_nextSlot = (s_nextSlot + 1) % SlotCount;
        s_frames[slot] = new Frame { Left = left, Right = right, Pose = pose };
        Interlocked.Increment(ref s_submitsQueued);
        GL.IssuePluginEvent(s_callbackPtr, slot);
    }

    private static bool SamePose(in HmdMatrix34_t a, in HmdMatrix34_t b) =>
        a.m0 == b.m0 && a.m1 == b.m1 && a.m2 == b.m2 && a.m3 == b.m3 && a.m4 == b.m4 && a.m5 == b.m5 &&
        a.m6 == b.m6 && a.m7 == b.m7 && a.m8 == b.m8 && a.m9 == b.m9 && a.m10 == b.m10 && a.m11 == b.m11;

    /// <summary>
    /// Queue SetOverlayTexture on the render thread. For D3D11 the overlay copies the texture on
    /// the app's immediate context, so it has the same threading constraint as Submit.
    /// </summary>
    public static void QueueOverlayTexture(ulong overlay, IntPtr texture)
    {
        int slot = s_nextOverlaySlot;
        s_nextOverlaySlot = (s_nextOverlaySlot + 1) % OverlaySlotCount;
        s_overlayTextures[slot] = new OverlayTexture { Overlay = overlay, Texture = texture };
        GL.IssuePluginEvent(s_callbackPtr, OverlayEventBase + slot);
    }

    /// <summary>Shut OpenVR down on the render thread, after any queued submits.</summary>
    public static void QueueShutdown()
    {
        GL.IssuePluginEvent(s_callbackPtr, ShutdownEvent);
    }

    private static void OnRenderEvent(int eventId)
    {
        try
        {
            if (eventId == ShutdownEvent)
            {
                OpenVR.Shutdown();
                return;
            }

            if (eventId >= OverlayEventBase && eventId < OverlayEventBase + OverlaySlotCount)
            {
                SetOverlayTexture(s_overlayTextures[eventId - OverlayEventBase]);
                return;
            }

            try
            {
                CVRCompositor compositor = OpenVR.Compositor;
                if (compositor == null)
                    return;

                Frame frame = s_frames[eventId % SlotCount];
                if (s_haveWaitPose)
                {
                    if (SamePose(frame.Pose, s_lastWaitPose)) Interlocked.Increment(ref s_paired);
                    else if (SamePose(frame.Pose, s_previousWaitPose)) Interlocked.Increment(ref s_stale);
                    else Interlocked.Increment(ref s_other);
                }

                Submit(compositor, EVREye.Eye_Left, frame.Left, frame.Pose);
                Submit(compositor, EVREye.Eye_Right, frame.Right, frame.Pose);

                // Paces the render thread to the headset and keeps SteamVR treating us as the scene app.
                // Its poses are the ones the next frame renders with (VRRig reads them via GetLastPoses).
                compositor.WaitGetPoses(s_renderPoses, s_gamePoses);
                s_previousWaitPose = s_lastWaitPose;
                s_lastWaitPose = s_renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking;
                s_haveWaitPose = true;
            }
            finally
            {
                Interlocked.Increment(ref s_posesReturned);
            }
        }
        catch (Exception e)
        {
            if (!s_errorLogged)
            {
                s_errorLogged = true;
                ReportError($"Render-thread submit failed: {e}");
            }
        }
    }

    private static void SetOverlayTexture(OverlayTexture o)
    {
        CVROverlay overlay = OpenVR.Overlay;
        if (overlay == null || o.Overlay == 0 || o.Texture == IntPtr.Zero)
            return;

        var tex = new Texture_t { handle = o.Texture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
        EVROverlayError err = overlay.SetOverlayTexture(o.Overlay, ref tex);

        // InvalidHandle just means the overlay was destroyed after this update was queued.
        if (err != EVROverlayError.None && err != EVROverlayError.InvalidHandle && !s_overlayErrorLogged)
        {
            s_overlayErrorLogged = true;
            ReportError($"SetOverlayTexture returned {err}");
        }
    }

    private static void Submit(CVRCompositor compositor, EVREye eye, IntPtr handle, HmdMatrix34_t pose)
    {
        s_tex->handle = handle;
        s_tex->eType = ETextureType.DirectX;
        s_tex->eColorSpace = EColorSpace.Auto;
        s_tex->mDeviceToAbsoluteTracking = pose;

        // Unity keeps render textures bottom-up on D3D, so flip V for the compositor
        // (confirmed on hardware: unflipped, the image is upside down and pitch looks inverted).
        var bounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = 1, vMax = 0 };
        EVRCompositorError err = compositor.Submit(eye, ref Unsafe.AsRef<Texture_t>(s_tex), ref bounds,
            EVRSubmitFlags.Submit_TextureWithPose);

        if (err != EVRCompositorError.None && err != EVRCompositorError.DoNotHaveFocus)
        {
            Interlocked.Increment(ref s_submitErrors);
            if (!s_errorLogged)
            {
                s_errorLogged = true;
                ReportError($"Compositor.Submit({eye}) returned {err}");
            }
        }
    }
}
