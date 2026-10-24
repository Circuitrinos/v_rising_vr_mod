using UnityEngine;
using Valve.VR;

namespace VRisingVR;

internal static class VRMath
{
    /// <summary>OpenVR (right-handed, -Z forward) to Unity (left-handed, +Z forward).</summary>
    public static void ToUnity(HmdMatrix34_t m, out Vector3 pos, out Quaternion rot)
    {
        pos = new Vector3(m.m3, m.m7, -m.m11);
        var forward = new Vector3(-m.m2, -m.m6, m.m10);
        var up = new Vector3(m.m1, m.m5, -m.m9);
        rot = Quaternion.LookRotation(forward, up);
    }

    /// <summary>
    /// OpenGL-style projection (what Camera.projectionMatrix expects) built from the eye's raw
    /// frustum tangents, following Valve's ComposeProjection reference.
    /// </summary>
    public static Matrix4x4 Projection(CVRSystem hmd, EVREye eye, float near, float far)
    {
        float l = 0, r = 0, t = 0, b = 0;
        hmd.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);

        var m = new Matrix4x4();
        m.m00 = 2f / (r - l);
        m.m02 = (r + l) / (r - l);
        m.m11 = 2f / (b - t);
        m.m12 = (b + t) / (b - t);
        m.m22 = -(far + near) / (far - near);
        m.m23 = -2f * far * near / (far - near);
        m.m32 = -1f;
        return m;
    }

    /// <summary>
    /// View matrix for drawing in OpenVR tracking space (right-handed, real meters) as one eye sees it: tracking
    /// space to that eye's space (-Z forward), the convention SetViewProjectionMatrices expects. The eye sits at
    /// <paramref name="head"/> times <paramref name="eyeToHead"/>, your real eye position.
    /// </summary>
    public static Matrix4x4 TrackingView(HmdMatrix34_t head, HmdMatrix34_t eyeToHead)
    {
        // The eye's pose in tracking space: rotation r and position t of head * eyeToHead.
        float r00 = head.m0 * eyeToHead.m0 + head.m1 * eyeToHead.m4 + head.m2 * eyeToHead.m8;
        float r01 = head.m0 * eyeToHead.m1 + head.m1 * eyeToHead.m5 + head.m2 * eyeToHead.m9;
        float r02 = head.m0 * eyeToHead.m2 + head.m1 * eyeToHead.m6 + head.m2 * eyeToHead.m10;
        float r10 = head.m4 * eyeToHead.m0 + head.m5 * eyeToHead.m4 + head.m6 * eyeToHead.m8;
        float r11 = head.m4 * eyeToHead.m1 + head.m5 * eyeToHead.m5 + head.m6 * eyeToHead.m9;
        float r12 = head.m4 * eyeToHead.m2 + head.m5 * eyeToHead.m6 + head.m6 * eyeToHead.m10;
        float r20 = head.m8 * eyeToHead.m0 + head.m9 * eyeToHead.m4 + head.m10 * eyeToHead.m8;
        float r21 = head.m8 * eyeToHead.m1 + head.m9 * eyeToHead.m5 + head.m10 * eyeToHead.m9;
        float r22 = head.m8 * eyeToHead.m2 + head.m9 * eyeToHead.m6 + head.m10 * eyeToHead.m10;
        float tx = head.m0 * eyeToHead.m3 + head.m1 * eyeToHead.m7 + head.m2 * eyeToHead.m11 + head.m3;
        float ty = head.m4 * eyeToHead.m3 + head.m5 * eyeToHead.m7 + head.m6 * eyeToHead.m11 + head.m7;
        float tz = head.m8 * eyeToHead.m3 + head.m9 * eyeToHead.m7 + head.m10 * eyeToHead.m11 + head.m11;

        // Its inverse: the transposed rotation, and the position taken back through it.
        var view = new Matrix4x4();
        view.m00 = r00;
        view.m01 = r10;
        view.m02 = r20;
        view.m03 = -(r00 * tx + r10 * ty + r20 * tz);
        view.m10 = r01;
        view.m11 = r11;
        view.m12 = r21;
        view.m13 = -(r01 * tx + r11 * ty + r21 * tz);
        view.m20 = r02;
        view.m21 = r12;
        view.m22 = r22;
        view.m23 = -(r02 * tx + r12 * ty + r22 * tz);
        view.m33 = 1f;
        return view;
    }
}
