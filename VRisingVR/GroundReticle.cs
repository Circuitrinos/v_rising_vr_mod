using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using UnityEngine;
using UnityEngine.Rendering;
using Num = System.Numerics;

namespace VRisingVR;

/// <summary>
/// The aim cursor in 3D: the game's own crosshair lying on the ground at the game's aim point, drawn into
/// both eye images after HDRP has rendered them (with the health bars, see VRRig.DrawEyeOverlays). The eye
/// textures carry HDRP's depth buffer, so your character and anything else in front of the aim point hide
/// it. It's pulled a little toward the viewer so the ground and grass right under it don't. The game aims
/// through characters at the ground behind them, so when you target an enemy the aim point is behind it:
/// the hidden parts are drawn again, faintly (GroundCursorHiddenAlpha), so the aim point never vanishes.
/// </summary>
internal sealed class GroundReticle
{
    /// <summary>How far (game meters) the cursor is pulled toward the viewer, so the ground under it never covers it.</summary>
    private const float PullTowardViewer = 0.35f;
    /// <summary>Raised this far off the ground (game meters) before the pull.</summary>
    private const float Lift = 0.03f;

    private Mesh _mesh;
    private Material _material;
    private Material _hidden; // the parts behind something, drawn faintly
    private float _hiddenAlpha = -1f;
    private Il2CppStructArray<Vector3> _vertices;
    private Matrix4x4 _identity;
    private Texture2D _fallback;
    private Texture2D _source;
    private float _rescanAt;
    private bool _prepared;

    /// <summary>Whether the cursor is drawn this frame.</summary>
    public bool Visible => _prepared;

    /// <summary>
    /// Lay this frame's cursor on the ground at <paramref name="aim"/> (game world), seen from
    /// <paramref name="head"/> (the eyes' midpoint), or hide it. Draw it with <see cref="Draw"/>.
    /// </summary>
    public bool Prepare(bool show, Vector3 aim, Vector3 head)
    {
        _prepared = false;
        if (!show)
            return false;
        EnsureResources();
        UpdateTexture();

        // Upright as seen from the viewer: the image's top points away along the ground.
        var a = new Num.Vector3(aim.x, aim.y + Lift, aim.z);
        var h = new Num.Vector3(head.x, head.y, head.z);
        var f = new Num.Vector3(a.X - h.X, 0f, a.Z - h.Z);
        f = f.LengthSquared() > 1e-6f ? Num.Vector3.Normalize(f) : Num.Vector3.UnitZ;
        var right = new Num.Vector3(f.Z, 0f, -f.X); // Unity is left-handed: looking along +Z, right is +X
        float half = 0.5f * Settings.ReticleSize.Value;

        Span<Vector3> v = _vertices.AsSpan();
        v[0] = Pulled(a - right * half - f * half, h); // uv (0,0): near left
        v[1] = Pulled(a - right * half + f * half, h); // uv (0,1): far left
        v[2] = Pulled(a + right * half + f * half, h); // uv (1,1): far right
        v[3] = Pulled(a + right * half - f * half, h); // uv (1,0): near right
        _mesh.vertices = _vertices;
        _prepared = true;
        return true;
    }

    /// <summary>Record the cursor's draw for the bound eye (render target and matrices already set).</summary>
    public void Draw(CommandBuffer commands)
    {
        if (!_prepared)
            return;
        commands.DrawMesh(_mesh, _identity, _material, 0, 0, null);
        float alpha = Settings.ReticleHiddenAlpha.Value;
        if (alpha <= 0f)
            return;
        if (MathF.Abs(alpha - _hiddenAlpha) > 1e-4f)
        {
            // UI/Default multiplies the vertex colour by its _Color.
            Color tint = default;
            tint.r = 1f;
            tint.g = 1f;
            tint.b = 1f;
            tint.a = alpha;
            _hidden.color = tint;
            _hiddenAlpha = alpha;
        }
        // Greater than the depth there: exactly the pixels the first pass left out.
        commands.DrawMesh(_mesh, _identity, _hidden, 0, 0, null);
    }

    public void Destroy()
    {
        if (_mesh != null)
            UnityEngine.Object.Destroy(_mesh);
        if (_material != null)
            UnityEngine.Object.Destroy(_material);
        if (_hidden != null)
            UnityEngine.Object.Destroy(_hidden);
        if (_fallback != null)
            UnityEngine.Object.Destroy(_fallback);
        _mesh = null;
        _material = null;
        _hidden = null;
        _hiddenAlpha = -1f;
        _fallback = null;
        _vertices = null;
        _source = null;
        _rescanAt = 0f;
        _prepared = false;
    }

    /// <summary>Move a point toward the viewer along its line of sight: it stays in the same place in view, only nearer.</summary>
    private static Vector3 Pulled(Num.Vector3 p, Num.Vector3 head)
    {
        Num.Vector3 toHead = head - p;
        float d = toHead.Length();
        if (d > 1e-3f)
            p += toHead * (MathF.Min(PullTowardViewer, 0.5f * d) / d);
        Vector3 o = default;
        o.x = p.X;
        o.y = p.Y;
        o.z = p.Z;
        return o;
    }

    private void EnsureResources()
    {
        if (_mesh != null)
            return;
        _identity = Matrix4x4.identity;
        _material = new Material(Canvas.GetDefaultCanvasMaterial()) { name = "VRisingVR_GroundCursor", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _material.SetColor("_TextureSampleAdd", default);
        _material.SetFloat("_UIVertexColorAlwaysGammaSpace", 0f);
        _material.SetFloat("unity_GUIZTestMode", 4f); // LessEqual: hidden behind whatever is in front of it
        _hidden = new Material(_material) { name = "VRisingVR_GroundCursorHidden", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _hidden.SetFloat("unity_GUIZTestMode", 5f);   // Greater: only where something in front covers it

        _vertices = new Il2CppStructArray<Vector3>(4);
        var uv = new Il2CppStructArray<Vector2>(4);
        Span<Vector2> u = uv.AsSpan();
        u[1].y = 1f;
        u[2].x = 1f;
        u[2].y = 1f;
        u[3].x = 1f;
        var colors = new Il2CppStructArray<Color32>(4);
        Span<Color32> c = colors.AsSpan();
        for (int i = 0; i < 4; i++)
        {
            c[i].r = 255;
            c[i].g = 255;
            c[i].b = 255;
            c[i].a = 255;
        }
        var triangles = new Il2CppStructArray<int>(6);
        Span<int> t = triangles.AsSpan();
        t[0] = 0;
        t[1] = 1;
        t[2] = 2;
        t[3] = 0;
        t[4] = 2;
        t[5] = 3;
        _mesh = new Mesh { name = "VRisingVR_GroundCursor", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _mesh.MarkDynamic();
        _mesh.vertices = _vertices;
        _mesh.uv = uv;
        _mesh.colors32 = colors;
        _mesh.triangles = triangles;
    }

    /// <summary>The game's gameplay cursor image (Game_Normal), or a ring until it exists; looked up twice a second.</summary>
    private void UpdateTexture()
    {
        if (_source != null && Time.unscaledTime < _rescanAt)
            return;
        _rescanAt = Time.unscaledTime + 0.5f;
        Texture2D texture = FindGameCursorTexture() ?? Fallback();
        // Unity's == compares the native objects (and a destroyed texture reads as null).
        if (_source != null && texture == _source)
            return;
        _material.mainTexture = texture;
        _hidden.mainTexture = texture;
        _source = texture;
    }

    private static Texture2D FindGameCursorTexture()
    {
        try
        {
            var datas = CursorController._CursorDatas;
            if (datas == null)
                return null;
            for (int i = 0; i < datas.Length; i++)
            {
                CursorData data = datas[i];
                if (data != null && data.CursorType == CursorType.Game_Normal && data.Texture != null)
                    return data.Texture;
            }
        }
        catch
        {
            // use the fallback ring
        }
        return null;
    }

    private Texture2D Fallback()
    {
        if (_fallback != null)
            return _fallback;
        const int size = 64;
        var pixels = new Color32[size * size];
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                bool ring = d > 22f && d < 29f;
                bool dot = d < 4f;
                pixels[y * size + x] = ring || dot ? new Color32 { r = 255, g = 255, b = 255, a = 230 }
                    : new Color32 { r = 0, g = 0, b = 0, a = 0 };
            }
        }
        _fallback = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        _fallback.SetPixels32(pixels);
        _fallback.Apply(false, false);
        return _fallback;
    }
}
