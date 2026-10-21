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
}
