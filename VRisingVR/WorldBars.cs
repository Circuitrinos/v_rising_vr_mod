using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Num = System.Numerics;

namespace VRisingVR;

/// <summary>
/// Draws the game's overhead entries (health and cast bars, names, levels, icons) above the characters, into
/// both eye textures after HDRP has finished them (see VRRig.DrawEyeOverlays). Normally each 3D entry shows the
/// game's own 2D entry: the HUD layer copies its pixels into an atlas before cutting it from the flat HUD, so
/// frame, colours, text and markers match the desktop exactly, laid out as the game lays them out. Without this
/// frame's atlas (no HUD layer, or the atlas was full) a bar is drawn from plain quads instead, and its name and
/// level stay on the flat HUD. Drawn after HDRP, they skip bloom, tonemapping and anti-aliasing, as the desktop's
/// overlay bars do, and they aren't depth-tested, so nothing hides them (also like the desktop). Being part of
/// the submitted eye images, they move and reproject together with the scene.
///
/// UnityEngine.Vector3/Color math is a native call per operation in this interop, so the per-vertex work
/// uses System.Numerics and writes the struct fields directly.
/// </summary>
internal sealed class WorldBars
{
    private const int MaxBars = 64;
    private const int QuadsPerBar = 5; // fallback: frame, empty, recent damage, health, absorb/cast strip
    private const int CutsPerBar = 12; // bar, cast bar, labels (a label cut around a bar can be up to four pieces)

    private readonly QuadMesh _plain = new("VRisingVR_WorldBars", MaxBars * QuadsPerBar);
    private readonly QuadMesh _textured = new("VRisingVR_WorldBarsTextured", MaxBars * CutsPerBar);
    private Material _plainMaterial;
    private Material _texturedMaterial;
    private Matrix4x4 _identity;
    private readonly List<(float distance, int index)> _order = new();

    /// <summary>
    /// Build this frame's bars for both eyes, seen from <paramref name="head"/> (the eyes' midpoint).
    /// Returns false if there's nothing to draw.
    /// </summary>
    public bool Prepare(GameHud hud, Vector3 head, Vector3 rightEye)
    {
        List<GameHud.Bar> bars = hud.Bars;
        _plain.Begin();
        _textured.Begin();
        if (bars.Count == 0)
            return false;
        EnsureResources();

        var h = new Num.Vector3(head.x, head.y, head.z);
        var eyeAxis = new Num.Vector3(rightEye.x - head.x, rightEye.y - head.y, rightEye.z - head.z);
        eyeAxis = eyeAxis.LengthSquared() > 1e-12f ? Num.Vector3.Normalize(eyeAxis) : Num.Vector3.UnitX;

        bool atlas = hud.HaveAtlas;
        if (atlas && _texturedMaterial.mainTexture != hud.Atlas)
            _texturedMaterial.mainTexture = hud.Atlas;

        float scale = Settings.WorldScale.Value;
        float tanMin = MathF.Tan(0.5f * Settings.BarMinAngle.Value * Mathf.Deg2Rad);
        float tanMax = MathF.Tan(0.5f * MathF.Max(Settings.BarMaxAngle.Value, Settings.BarMinAngle.Value) * Mathf.Deg2Rad);
        float tanBorder = MathF.Tan(0.5f * 0.15f * Mathf.Deg2Rad); // at least about 1.5 px

        // Far to near: with no depth test, nearer bars must be drawn last.
        _order.Clear();
        for (int i = 0; i < bars.Count && _order.Count < MaxBars; i++)
        {
            if (bars[i].CutCount > 0)
                _order.Add((Num.Vector3.DistanceSquared(bars[i].Anchor, h), i));
        }
        _order.Sort((a, b) => b.distance.CompareTo(a.distance));

        foreach ((float _, int index) in _order)
        {
            GameHud.Bar bar = bars[index];
            Num.Vector3 to = bar.Anchor - h;
            float d = to.Length();
            if (d < 1e-3f)
                continue;
            // One basis for both eyes (no stereo rivalry), kept level: no roll when the head tilts.
            Num.Vector3 f = to / d;
            Num.Vector3 right = Num.Vector3.Cross(Num.Vector3.UnitY, f);
            right = right.LengthSquared() > 1e-4f ? Num.Vector3.Normalize(right) : eyeAxis;
            Num.Vector3 up = Num.Vector3.Cross(f, right);

            // A physical size on the tabletop (real meters x WorldScale), clamped to a readable angular size,
            // and as much wider or shorter as the game's own 2D bar is (elites, small creatures).
            float ws = bar.WidthScale;
            float w = Math.Clamp(Settings.BarWidthMeters.Value * scale * ws, 2f * d * tanMin * ws, 2f * d * tanMax * ws);

            // The game's own 2D entry at its proportions, drawn level: the 2D entry is rolled by its screen
            // position, and sampling each element's own corners from the atlas undoes that. Each element sits
            // where it sits on the 2D HUD relative to the bar's frame (name above, level badge beside, ...),
            // along the entry's own axes; the frame's bottom edge rests at the anchor.
            float metersPerPixel = w / MathF.Max(bar.FrameWidth, 1f);
            float barH = bar.FrameHeight * metersPerPixel;
            Num.Vector3 c = bar.Anchor + Num.Vector3.UnitY * (Settings.BarLiftMeters.Value * scale + 0.5f * barH);
            bool texturedBars = atlas && BarsCopied(hud, bar);
            if (atlas)
            {
                for (int k = bar.FirstCut, end = Math.Min(bar.FirstCut + bar.CutCount, hud.Cuts.Count); k < end; k++)
                {
                    GameHud.Cut cut = hud.Cuts[k];
                    if (!cut.InAtlas || cut.IsBar && !texturedBars)
                        continue;
                    Num.Vector2 offset = 0.5f * (cut.Q1 + cut.Q3) - bar.FrameCentre;
                    float dx = Num.Vector2.Dot(offset, bar.FrameX), dy = Num.Vector2.Dot(offset, bar.FrameY);
                    _textured.Add(c + right * (dx * metersPerPixel) + up * (dy * metersPerPixel), right, up,
                        (cut.Q3 - cut.Q0).Length() * metersPerPixel, (cut.Q1 - cut.Q0).Length() * metersPerPixel, cut, Opaque);
                }
            }
            if (texturedBars)
                continue;

            // Plain bars, in the 2D bar's frame box, where the textured bar would be, so the labels around it keep
            // clear: the 2D bars couldn't be copied whole and clean, or there's no atlas this frame.
            float margin = GameHud.CutMargin * metersPerPixel;
            float bodyW = MathF.Max(w - 2f * margin, 0.5f * w), bodyH = MathF.Max(barH - 2f * margin, 0.5f * barH);
            float b = Math.Min(MathF.Max(0.15f * bodyH, 2f * d * tanBorder), 0.3f * bodyH);
            float innerW = bodyW - 2f * b, innerH = bodyH - 2f * b;
            float a = bar.ShowHealth ? bar.Alpha : 1f;
            if (bar.ShowHealth)
            {
                _plain.AddFill(c, right, up, bodyW, bodyH, 1f, Raw(bar.FrameColor, a));
                _plain.AddFill(c, right, up, innerW, innerH, 1f, Raw(0f, 0f, 0f, 0.65f, a));
                _plain.AddFill(c, right, up, innerW, innerH, Math.Clamp(bar.Damage, 0f, 1f), Raw(bar.DamageColor, a));
                _plain.AddFill(c, right, up, innerW, innerH, Math.Clamp(bar.Health, 0f, 1f), Raw(bar.HealthColor, a));
            }
            if (bar.Cast >= 0f)
                // Cast progress (its 2D bar was cut from the flat HUD): a thin strip just under the bar.
                _plain.AddFill(c - up * (0.5f * barH + 0.3f * bodyH), right, up, bodyW, 0.4f * bodyH, Math.Clamp(bar.Cast, 0f, 1f), Raw(1f, 1f, 1f, 1f, a));
            else if (bar.ShowHealth && bar.Absorb > 0f)
                // A shield: a thin strip along the top edge.
                _plain.AddFill(c + up * (0.25f * innerH), right, up, innerW, 0.4f * innerH, Math.Clamp(bar.Absorb, 0f, 1f), Raw(1f, 1f, 1f, 0.85f, a));
        }

        bool any = _plain.End() | _textured.End();
        return any;
    }

    /// <summary>Record the bars' draw for the bound eye (render target and matrices already set).</summary>
    public void Draw(CommandBuffer commands)
    {
        if (_plain.Count > 0)
            commands.DrawMesh(_plain.Mesh, _identity, _plainMaterial, 0, 0, null);
        if (_textured.Count > 0)
            commands.DrawMesh(_textured.Mesh, _identity, _texturedMaterial, 0, 0, null);
    }

    public void Destroy()
    {
        _plain.Destroy();
        _textured.Destroy();
        if (_plainMaterial != null)
            UnityEngine.Object.Destroy(_plainMaterial);
        if (_texturedMaterial != null)
            UnityEngine.Object.Destroy(_texturedMaterial);
        _plainMaterial = null;
        _texturedMaterial = null;
    }

    /// <summary>Whether the entry's bar cores (health, cast) were all copied whole and clean, so its bars show the game's own pixels.</summary>
    private static bool BarsCopied(GameHud hud, in GameHud.Bar bar)
    {
        for (int k = bar.FirstCut, end = Math.Min(bar.FirstCut + bar.CutCount, hud.Cuts.Count); k < end; k++)
        {
            GameHud.Cut cut = hud.Cuts[k];
            if (cut.IsCore && !cut.InAtlas)
                return false;
        }
        return true;
    }

    private void EnsureResources()
    {
        if (_plainMaterial != null)
            return;
        _identity = Matrix4x4.identity;
        _plainMaterial = NewMaterial("VRisingVR_WorldBars", Texture2D.whiteTexture);
        _texturedMaterial = NewMaterial("VRisingVR_WorldBarsTextured", Texture2D.whiteTexture);
    }

    private static Material NewMaterial(string name, Texture texture)
    {
        var material = new Material(Canvas.GetDefaultCanvasMaterial()) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        material.mainTexture = texture;
        material.SetColor("_TextureSampleAdd", default);         // don't inherit a global left by text rendering
        material.SetFloat("_UIVertexColorAlwaysGammaSpace", 0f); // as CharacterHUDsCanvas: the colours are used raw
        material.SetFloat("unity_GUIZTestMode", 8f);             // Always: like the desktop, nothing hides them
        return material;
    }

    private static readonly Color32 Opaque = new Color32 { r = 255, g = 255, b = 255, a = 255 };

    private static Color32 Raw(Color c, float alpha) => Raw(c.r, c.g, c.b, c.a, alpha);

    /// <summary>
    /// The colour as an 8-bit vertex colour, unconverted: CharacterHUDsCanvas has VertexColorAlwaysGammaSpace
    /// off, so the desktop bars get the authored values raw too. (Set fields: the Color to Color32 operator is a native call.)
    /// </summary>
    private static Color32 Raw(float r, float g, float b, float a, float alpha)
    {
        Color32 o = default;
        o.r = To8(r);
        o.g = To8(g);
        o.b = To8(b);
        o.a = To8(a * alpha);
        return o;
    }

    private static byte To8(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    /// <summary>A fixed-capacity dynamic mesh of quads, rebuilt each frame.</summary>
    private sealed class QuadMesh
    {
        private readonly string _name;
        private readonly int _capacity;
        private Il2CppStructArray<Vector3> _vertices;
        private Il2CppStructArray<Vector2> _uvs;
        private Il2CppStructArray<Color32> _colors;
        private int _used;
        private int _previous;

        public QuadMesh(string name, int capacity)
        {
            _name = name;
            _capacity = capacity;
        }

        public Mesh Mesh { get; private set; }

        public int Count => _used;

        public void Begin()
        {
            _previous = Math.Max(_previous, _used);
            _used = 0;
        }

        /// <summary>The left-anchored fill [0, fill] of a w x h rectangle centred on c, sampling the white texture.</summary>
        public void AddFill(Num.Vector3 c, Num.Vector3 right, Num.Vector3 up, float w, float h, float fill, Color32 colour)
        {
            if (fill <= 0f)
                return;
            Num.Vector3 x0 = c - right * (0.5f * w);
            Num.Vector3 x1 = c + right * (w * (fill - 0.5f));
            Add(x0, x1, up * (0.5f * h), new Num.Vector4(0.5f, 0.5f, 0f, 0f), colour);
        }

        /// <summary>A w x h rectangle centred on c showing the cut's atlas corners (bottom-left, top-left, top-right, bottom-right).</summary>
        public void Add(Num.Vector3 c, Num.Vector3 right, Num.Vector3 up, float w, float h, GameHud.Cut cut, Color32 colour)
        {
            Add(c - right * (0.5f * w), c + right * (0.5f * w), up * (0.5f * h), cut.UV0, cut.UV1, cut.UV2, cut.UV3, colour);
        }

        private void Add(Num.Vector3 left, Num.Vector3 right, Num.Vector3 halfUp, Num.Vector4 uv, Color32 colour)
        {
            Add(left, right, halfUp, new Num.Vector2(uv.X, uv.Y), new Num.Vector2(uv.X, uv.Y + uv.W),
                new Num.Vector2(uv.X + uv.Z, uv.Y + uv.W), new Num.Vector2(uv.X + uv.Z, uv.Y), colour);
        }

        private void Add(Num.Vector3 left, Num.Vector3 right, Num.Vector3 halfUp,
            Num.Vector2 uv0, Num.Vector2 uv1, Num.Vector2 uv2, Num.Vector2 uv3, Color32 colour)
        {
            EnsureCreated();
            if (_used >= _capacity)
                return;
            int v = _used * 4;
            Span<Vector3> vs = _vertices.AsSpan();
            Span<Vector2> us = _uvs.AsSpan();
            Span<Color32> cs = _colors.AsSpan();
            vs[v] = ToUnity(left - halfUp);
            vs[v + 1] = ToUnity(left + halfUp);
            vs[v + 2] = ToUnity(right + halfUp);
            vs[v + 3] = ToUnity(right - halfUp);
            us[v].x = uv0.X;
            us[v].y = uv0.Y;
            us[v + 1].x = uv1.X;
            us[v + 1].y = uv1.Y;
            us[v + 2].x = uv2.X;
            us[v + 2].y = uv2.Y;
            us[v + 3].x = uv3.X;
            us[v + 3].y = uv3.Y;
            for (int k = 0; k < 4; k++)
                cs[v + k] = colour;
            _used++;
        }

        /// <summary>Upload this frame's quads; false if there are none.</summary>
        public bool End()
        {
            if (Mesh == null)
                return false;
            // Collapse the quads left over from earlier frames.
            Span<Vector3> vs = _vertices.AsSpan();
            for (int i = _used * 4; i < _previous * 4; i++)
                vs[i] = default;
            _previous = _used;
            Mesh.vertices = _vertices;
            Mesh.uv = _uvs;
            Mesh.colors32 = _colors;
            return _used > 0;
        }

        public void Destroy()
        {
            if (Mesh != null)
                UnityEngine.Object.Destroy(Mesh);
            Mesh = null;
            _vertices = null;
            _uvs = null;
            _colors = null;
            _used = 0;
            _previous = 0;
        }

        private void EnsureCreated()
        {
            if (Mesh != null)
                return;
            _vertices = new Il2CppStructArray<Vector3>(_capacity * 4);
            _uvs = new Il2CppStructArray<Vector2>(_capacity * 4);
            _colors = new Il2CppStructArray<Color32>(_capacity * 4);
            var triangles = new Il2CppStructArray<int>(_capacity * 6);
            Span<int> t = triangles.AsSpan();
            for (int q = 0; q < _capacity; q++)
            {
                int v = q * 4, i = q * 6;
                t[i] = v;
                t[i + 1] = v + 1;
                t[i + 2] = v + 2;
                t[i + 3] = v;
                t[i + 4] = v + 2;
                t[i + 5] = v + 3;
            }
            Mesh = new Mesh { name = _name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            Mesh.MarkDynamic();
            Mesh.vertices = _vertices;
            Mesh.uv = _uvs;
            Mesh.colors32 = _colors;
            Mesh.triangles = triangles;
        }

        private static Vector3 ToUnity(Num.Vector3 v)
        {
            Vector3 o = default;
            o.x = v.X;
            o.y = v.Y;
            o.z = v.Z;
            return o;
        }
    }
}
