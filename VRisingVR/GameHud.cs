using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using ProjectM.UI;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Num = System.Numerics;
using Stopwatch = System.Diagnostics.Stopwatch;
using UIGraphic = UnityEngine.UI.Graphic;
using UIImage = UnityEngine.UI.Image;

namespace VRisingVR;

/// <summary>
/// Read-only view of the game's own overhead entries (health and cast bars, names, levels, icons, the hovered
/// character's blood type): the list the desktop HUD draws this frame. GetCharacterHUDSystem.Entries[i] (the
/// data) pairs with SetCharacterHUDSystem._Instances[i] (the live UI entry); SetCharacterHUDSystem.OnUpdate uses
/// the same index for both. One snapshot per frame, taken by whoever asks first after the presentation systems
/// ran (the HUD layer before the eyes render, or the eye draw after them), so an element is either drawn in 3D
/// or left on the flat HUD, never both. Never writes game state.
/// </summary>
internal sealed unsafe class GameHud
{
    public struct Bar
    {
        /// <summary>Translation + (0, CharacterHUD.Height, 0) in game meters: the point the game projects for the desktop bar.</summary>
        public Num.Vector3 Anchor;
        /// <summary>Fills, 0..1. Cast is negative unless the 2D cast bar is showing (and moved to 3D).</summary>
        public float Health, Damage, Absorb, Cast;
        /// <summary>
        /// The 2D images' tints, used raw as vertex colors as CharacterHUDsCanvas does (VertexColorAlwaysGammaSpace
        /// off). Only for the fallback look: normally the 3D bar shows the 2D bar's own pixels (see Cut.InAtlas).
        /// </summary>
        public Color HealthColor, DamageColor, FrameColor;
        /// <summary>The 2D bar's own fade: every CanvasGroup above it, its show/hide motions and the zoom fade.</summary>
        public float Alpha;
        /// <summary>The 2D bar's width relative to a normal one (elite bars are wider, small creatures' shorter).</summary>
        public float WidthScale;
        /// <summary>Whether the 2D health bar is showing; an entry can show just a name or a level.</summary>
        public bool ShowHealth;
        /// <summary>
        /// The 2D health bar's frame at rest, plus the cut margin, in screen pixels: its centre, its unit axes (the
        /// entry's roll) and its size. The entry is placed in 3D relative to it, also while the bar is hidden or
        /// animating (a player's bar shrinks and drops while it hides, its name doesn't).
        /// </summary>
        public Num.Vector2 FrameCentre, FrameX, FrameY;
        public float FrameWidth, FrameHeight;
        /// <summary>The entry's elements that are drawn in 3D: <see cref="Cuts"/>[FirstCut .. FirstCut + CutCount).</summary>
        public int FirstCut, CutCount;
    }

    /// <summary>
    /// A 2D element that is drawn in 3D instead, in screen pixels (bottom-left origin): the health bar with
    /// everything in its container, the cast bar, or a label (a name, the level badge, an icon, the blood type;
    /// labels that overlap are one cut). The game rolls each entry by up to 10 degrees depending on its screen
    /// position, so the element's own corners are kept.
    /// </summary>
    public struct Cut
    {
        /// <summary>The element's corners with a small margin, in GetWorldCorners order: bottom-left, top-left, top-right, bottom-right.</summary>
        public Num.Vector2 Q0, Q1, Q2, Q3;
        /// <summary>The corners' bounding box: what is copied to the atlas and blanked on the HUD.</summary>
        public Num.Vector2 Min, Max;
        /// <summary>The index of the bar (entry) it belongs to.</summary>
        public int Owner;
        /// <summary>
        /// Part of a bar (health or cast): its core or a glow piece around it. Bars are always blanked on the HUD,
        /// and drawn from the atlas only while all of the entry's cores are; otherwise they're drawn plain.
        /// </summary>
        public bool IsBar;
        /// <summary>
        /// A bar's core: its image with the margin. Its copy must be whole and must not overlap another character's
        /// core, since a cropped or mixed copy would misread as its fill. Glow pieces and labels are copied even
        /// where entries overlap.
        /// </summary>
        public bool IsCore;
        /// <summary>
        /// Whether the HUD layer copied it into <see cref="Atlas"/> this frame, and the atlas UVs of Q0..Q3. Not
        /// when the HUD layer didn't draw this frame, the atlas was full or the element runs off the screen, nor
        /// for a bar that overlaps another character's element. A bar is then drawn plain; a label stays on the
        /// flat HUD.
        /// </summary>
        public bool InAtlas;
        public Num.Vector2 UV0, UV1, UV2, UV3;
    }

    /// <summary>The margin kept around each cut image (px): the bar's frame glow.</summary>
    public const float CutMargin = 2f;

    /// <summary>The margin around text, in the text's own units: the outline and shadow reach past the glyph boxes.</summary>
    private const float TextMargin = 4f;

    /// <summary>Elements bigger than this (px) aren't part of one character's entry (and wouldn't fit the atlas).</summary>
    private const float MaxPartPixels = 1000f;

    /// <summary>The width of a normal unit's 2D bar, in canvas units.</summary>
    private const float NormalBarWidth = 300f;

    public readonly List<Bar> Bars = new();
    public readonly List<Cut> Cuts = new();

    /// <summary>The root overlay canvas the entries draw in (CharacterHUDsCanvas); null until found.</summary>
    public Canvas OverheadCanvas { get; private set; }

    /// <summary>The combat text's root overlay canvas (SCTCanvas, next to CharacterHUDsCanvas); null if not found.</summary>
    public Canvas SctCanvas { get; private set; }

    /// <summary>The 2D entries' pixels, copied by the HUD layer (HudLayer.RecordCuts) before it blanks them.</summary>
    public RenderTexture Atlas;
    /// <summary>The frame <see cref="Atlas"/> and the cuts' atlas UVs were last filled in.</summary>
    public int AtlasFrame = -1;

    /// <summary>Whether this frame's entries can show the game's own 2D pixels.</summary>
    public bool HaveAtlas => AtlasFrame == Time.frameCount && Atlas != null;

    private World _world;
    private SetCharacterHUDSystem _set;
    private GetCharacterHUDSystem _get;
    private ComponentType _translation;
    private ComponentType _characterHud;
    private int _stride;
    private float _retryAt;
    private bool _disabled;
    private bool _errorLogged;
    private bool _displayWarned;
    private bool _loggedFirst;
    private bool _loggedLayout;
    private bool _elementWarned;
    private bool _attachLogged;
    private bool _unmatchedLogged;
    private int _attachedMatched;
    private int _frame = -1;
    private bool _ok;
    private readonly Il2CppStructArray<Vector3> _corners = new(4);

    // The UI objects of each entry instance (they're pooled), resolved once.
    private readonly Dictionary<IntPtr, EntryRefs> _refs = new();

    // Where each kind of entry's bar frame sits at rest, in entry units (learned while a bar shows and is still).
    private readonly Dictionary<int, RestFrame> _restFrames = new();

    // This frame's entries with a health bar, and the ones without one, which go with a bar of the same character.
    private readonly List<(int index, EntryRefs refs)> _framed = new();
    private readonly List<(long entity, EntryRefs refs)> _attached = new();

    // The entry being read: its visible elements as boxes along its own axes, in pixels from its root's pivot,
    // without and with their margins.
    private readonly List<Part> _parts = new();
    private readonly List<Part> _padded = new();
    private readonly List<Part> _out = new();
    private readonly List<Part> _ring = new();
    private readonly List<Part> _work = new();
    private readonly List<Part> _done = new();
    private Num.Vector2 _origin, _axisX, _axisY;

    // Time spent reading the entries, for the perf log.
    private long _statTicks;
    private int _statFrames;
    private int _statMaxEntries;

    public void Reset()
    {
        _world = null;
        _set = null;
        _get = null;
        OverheadCanvas = null;
        SctCanvas = null;
        Bars.Clear();
        Cuts.Clear();
        _refs.Clear();
        _framed.Clear();
        _attached.Clear();
        _frame = -1;
    }

    /// <summary>This frame's entries (<see cref="Bars"/>, <see cref="Cuts"/>); false if the game's HUD isn't available.</summary>
    public bool Refresh()
    {
        int frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            long start = Stopwatch.GetTimestamp();
            _ok = RefreshNow();
            _statTicks += Stopwatch.GetTimestamp() - start;
            _statFrames++;
            _statMaxEntries = Math.Max(_statMaxEntries, Bars.Count);
        }
        return _ok;
    }

    /// <summary>The average time spent reading the entries per frame since the last call (for the perf log); null if none.</summary>
    public string TakeStats()
    {
        if (_statFrames == 0)
            return null;
        double ms = _statTicks * 1000.0 / Stopwatch.Frequency / _statFrames;
        string stats = $"overhead read {ms:F2}ms/frame, up to {_statMaxEntries} entries";
        _statTicks = 0;
        _statFrames = 0;
        _statMaxEntries = 0;
        return stats;
    }

    /// <summary>
    /// Safety net: outside the HUD layer's and the desktop mirror's draws, the overhead and combat-text
    /// canvases must be on display 0, or the desktop would lose its overhead bars, names, prompts or numbers.
    /// </summary>
    public void EnsureOverheadOnDesktop()
    {
        if (RestoreDisplay(OverheadCanvas) | RestoreDisplay(SctCanvas) && !_displayWarned)
        {
            _displayWarned = true;
            Plugin.Logger.LogWarning("3D health bars: a game canvas was left off the desktop display; restored.");
        }
    }

    private static bool RestoreDisplay(Canvas canvas)
    {
        if (canvas == null || canvas.targetDisplay == 0)
            return false;
        canvas.targetDisplay = 0;
        return true;
    }

    /// <summary>Whether two cuts' (rolled) boxes overlap: a separating-axis test on their corners.</summary>
    public static bool Intersects(in Cut a, in Cut b)
    {
        if (a.Min.X >= b.Max.X || b.Min.X >= a.Max.X || a.Min.Y >= b.Max.Y || b.Min.Y >= a.Max.Y)
            return false;
        return !Separates(a.Q3 - a.Q0, a, b) && !Separates(a.Q1 - a.Q0, a, b)
               && !Separates(b.Q3 - b.Q0, a, b) && !Separates(b.Q1 - b.Q0, a, b);
    }

    private static bool Separates(Num.Vector2 axis, in Cut a, in Cut b)
    {
        Project(axis, a, out float a0, out float a1);
        Project(axis, b, out float b0, out float b1);
        return a1 <= b0 || b1 <= a0;
    }

    private static void Project(Num.Vector2 axis, in Cut c, out float min, out float max)
    {
        float p0 = Num.Vector2.Dot(c.Q0, axis), p1 = Num.Vector2.Dot(c.Q1, axis);
        float p2 = Num.Vector2.Dot(c.Q2, axis), p3 = Num.Vector2.Dot(c.Q3, axis);
        min = Math.Min(Math.Min(p0, p1), Math.Min(p2, p3));
        max = Math.Max(Math.Max(p0, p1), Math.Max(p2, p3));
    }

    private bool RefreshNow()
    {
        Bars.Clear();
        Cuts.Clear();
        if (_disabled)
            return false;
        try
        {
            if (!EnsureSystems())
                return false;
            if (OverheadCanvas == null)
                ResolveCanvas();

            NativeArray<CharacterHUDEntry.Data> entries = _get.Entries;
            var instances = _set._Instances;
            if (instances == null || entries.m_Buffer == null)
                return false;

            // Sort this frame's entries: the ones with a health bar, and the ones without (the hovered
            // character's blood type), which are drawn with the bar of the same character.
            _framed.Clear();
            _attached.Clear();
            int count = Math.Min(entries.m_Length, instances.Count);
            byte* buffer = (byte*)entries.m_Buffer;
            for (int i = 0; i < count; i++)
            {
                // Step by the native element size, which can differ from the managed struct's size.
                ref CharacterHUDEntry.Data d = ref *(CharacterHUDEntry.Data*)(buffer + (long)i * _stride);
                if (d.Entity.Index == 0 && d.Entity.Version == 0)
                    continue; // a free slot (Entity.Null); the game skips these too
                CharacterHUDEntry entry = instances[i];
                if (entry == null)
                    continue;
                EntryRefs refs = GetRefs(entry);
                if (refs.Root == null || !refs.GameObject.activeInHierarchy)
                    continue;
                if (refs.FrameRect != null)
                    _framed.Add((i, refs));
                else if (refs.Groups.Length > 0)
                    _attached.Add((Key(d.Entity), refs));
            }

            EntityManager em = _world.EntityManager;
            _attachedMatched = 0;
            foreach ((int index, EntryRefs refs) in _framed)
                ReadEntry(em, ref *(CharacterHUDEntry.Data*)(buffer + (long)index * _stride), refs);
            if (_attachedMatched < _attached.Count && !_unmatchedLogged)
            {
                _unmatchedLogged = true;
                Plugin.Logger.LogInfo("3D health bars: an entry without a bar has no character bar to go with this frame; " +
                                      "it stays on the flat HUD.");
            }

            if (!_loggedFirst && Bars.Count > 0)
            {
                _loggedFirst = true;
                Bar b = Bars[0];
                Plugin.Logger.LogInfo($"3D health bars: {Bars.Count} visible (first at {b.Anchor}, health {b.Health:F2}, " +
                                      $"width x{b.WidthScale:F2}, {b.CutCount} element(s)); overhead canvas " +
                                      $"{(OverheadCanvas != null ? OverheadCanvas.name : "not found")}.");
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                Plugin.Logger.LogWarning($"Reading the game's overhead health bars failed (retrying): {ex.Message}");
            }
            Bars.Clear();
            Cuts.Clear();
            _refs.Clear();
            _world = null;
            _set = null;
            _get = null;
            _retryAt = Time.unscaledTime + 1f;
            return false;
        }
    }

    /// <summary>Adds one entry with a health bar (and whatever goes with it) to <see cref="Bars"/> and <see cref="Cuts"/>.</summary>
    private void ReadEntry(EntityManager em, ref CharacterHUDEntry.Data d, EntryRefs refs)
    {
        Entity e = d.Entity;
        if (!em.Exists(e) || !em.HasComponent(e, _translation))
            return;
        var position = ((Translation*)em.GetComponentDataRawRO(e, _translation.TypeIndex))->Value;
        if (position.x == 0f && position.y == 0f && position.z == 0f)
            return; // not placed in the world yet (seen while loading)
        float height = em.HasComponent(e, _characterHud)
            ? ((CharacterHUD*)em.GetComponentDataRawRO(e, _characterHud.TypeIndex))->Height._Value
            : 2f;

        // The entry's own axes (the game rolls the entry by its screen position) and scale, from its content
        // node (RootCanvasGroup): the game also scales that node with the camera zoom, so measured below it the
        // bar's rest pose is the same at every zoom. It sits on the root's pivot and isn't rotated itself.
        Matrix4x4 m = refs.Content.localToWorldMatrix;
        var axisX = new Num.Vector2(m.m00, m.m10);
        float unit = axisX.Length(); // screen pixels per entry unit
        if (!(unit > 1e-4f))
            return;
        _origin = new Num.Vector2(m.m03, m.m13);
        _axisX = axisX / unit;
        _axisY = Direction(new Num.Vector2(m.m01, m.m11), new Num.Vector2(-_axisX.Y, _axisX.X));

        // The bar's frame as it is now.
        refs.FrameRect.GetWorldCorners(_corners);
        Span<Vector3> f = _corners.AsSpan();
        Num.Vector2 bl = V2(f[0]), tl = V2(f[1]), tr = V2(f[2]), br = V2(f[3]);
        Box(bl, tl, tr, br, out float fu0, out float fv0, out float fu1, out float fv1);
        Rect rect = refs.FrameRect.rect;
        float rectW = rect.m_Width, rectH = rect.m_Height;
        if (!(fu1 - fu0 >= 4f) || !(fv1 - fv0 >= 1f) || !(rectW > 0f) || !(rectH > 0f))
            return; // no usable reference: the entry stays on the flat HUD

        // The bar itself is the fill image's subtree, which the game's motions fade in and out and switch off
        // (full health, not hovered, ...). The name, level and icons can show without it.
        float alpha = 0f;
        if (refs.Fill != null && refs.Fill.isActiveAndEnabled)
            alpha = refs.FillRenderer.GetInheritedAlpha();
        bool showHealth = alpha >= 0.01f;

        // Where the frame sits at rest. Some entries animate the bar's container (a player's bar shrinks to 0.8
        // and drops while it hides), and the rest of the entry must keep its place and size meanwhile. Learned
        // per kind of entry whenever a bar shows and holds still for a frame (its motions move it every frame).
        // Not only at full alpha: zoomed in, the game keeps the bars faded.
        float chainX = (fu1 - fu0) / (unit * rectW), chainY = (fv1 - fv0) / (unit * rectH);
        float centreU = 0.5f * (fu0 + fu1) / unit, centreV = 0.5f * (fv0 + fv1) / unit;
        if (showHealth && MathF.Abs(chainX - refs.LastChainX) < 1e-4f
            && MathF.Abs(centreU - refs.LastCentreU) < 0.01f && MathF.Abs(centreV - refs.LastCentreV) < 0.01f)
            _restFrames[refs.Type] = new RestFrame { ChainX = chainX, ChainY = chainY, CentreU = centreU, CentreV = centreV };
        refs.LastChainX = chainX;
        refs.LastCentreU = centreU;
        refs.LastCentreV = centreV;
        float restW, restH, restU, restV;
        if (_restFrames.TryGetValue(refs.Type, out RestFrame rest))
        {
            restW = rectW * rest.ChainX * unit;
            restH = rectH * rest.ChainY * unit;
            restU = rest.CentreU * unit;
            restV = rest.CentreV * unit;
        }
        else
        {
            restW = fu1 - fu0;
            restH = fv1 - fv0;
            restU = 0.5f * (fu0 + fu1);
            restV = 0.5f * (fv0 + fv1);
        }

        _parts.Clear();
        if (showHealth)
        {
            // The frame is the bar's core; the rest of its container (fills, highlight, an elite's glow) reaches
            // around it.
            if (!refs.FrameExtent.Known)
                refs.FrameExtent = MeasureExtent(refs.FrameNode, refs.FrameRect);
            AddBar(bl, tl, tr, br, refs.FrameExtent);
        }
        float cast = -1f;
        if (d.CastBarFill.hasValue && AddCastBar(refs))
            cast = d.CastBarFill.value;
        int barParts = _parts.Count;
        AddLabels(refs);
        long key = Key(e);
        foreach ((long entity, EntryRefs attached) in _attached)
        {
            if (entity != key)
                continue;
            AddLabels(attached);
            _attachedMatched++;
            if (!_attachLogged)
            {
                _attachLogged = true;
                Plugin.Logger.LogInfo($"3D health bars: an entry without a bar (type {attached.Type}, such as the blood type) " +
                                      $"goes with its character's bar (type {refs.Type}).");
            }
        }
        int labels = _parts.Count - barParts;
        if (_parts.Count == 0)
            return;

        // Lay the pieces out so that no pixel is in two cuts (drawn twice) or in none, the bars' cores stay whole
        // and labels never depend on a bar's copy.
        MergeParts();
        PadParts();
        LayOut();
        int first = Cuts.Count;
        foreach (Part part in _out)
            AddCut(part, Bars.Count);

        UIImage fill = refs.Fill, frameImage = refs.Frame, damageImage = refs.Damage;
        Bars.Add(new Bar
        {
            Anchor = new Num.Vector3(position.x, position.y + height, position.z),
            Health = d.HealthFill,
            Damage = d.RecentDamageTakenFill,
            Absorb = d.AbsorbFill,
            Cast = cast,
            HealthColor = showHealth ? fill.m_Color : default,
            DamageColor = showHealth ? (damageImage != null ? damageImage.m_Color : fill.m_Color) : default,
            FrameColor = showHealth && frameImage != null ? frameImage.m_Color : Black,
            Alpha = alpha,
            WidthScale = Math.Clamp(rectW / NormalBarWidth, 0.5f, 2f),
            ShowHealth = showHealth,
            FrameCentre = _origin + _axisX * restU + _axisY * restV,
            FrameX = _axisX,
            FrameY = _axisY,
            FrameWidth = restW + 2f * CutMargin,
            FrameHeight = restH + 2f * CutMargin,
            FirstCut = first,
            CutCount = Cuts.Count - first,
        });

        if (labels > 0 && !_loggedLayout)
        {
            _loggedLayout = true;
            LogLayout(refs, first);
        }
    }

    private static readonly Color Black = new Color(0f, 0f, 0f, 1f);

    private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;

    private EntryRefs GetRefs(CharacterHUDEntry entry)
    {
        IntPtr key = entry.Pointer;
        if (_refs.TryGetValue(key, out EntryRefs refs))
            return refs;
        if (_refs.Count >= 256)
            _refs.Clear(); // entries are pooled, so this only happens after many come and go
        try
        {
            refs = new EntryRefs(entry);
            if (refs.Partial != null)
                WarnOnce($"some of an overhead entry's labels can't be read ({refs.Partial}); they stay on the flat HUD");
        }
        catch (Exception e)
        {
            refs = EntryRefs.Unusable;
            WarnOnce($"an overhead entry can't be read ({e.Message}); it stays on the flat HUD");
        }
        _refs[key] = refs;
        return refs;
    }

    private void WarnOnce(string message)
    {
        if (_elementWarned)
            return;
        _elementWarned = true;
        Plugin.Logger.LogWarning($"3D health bars: {message}.");
    }

    /// <summary>The cast bar (with its frame) as a bar part, if it shows; true if it does.</summary>
    private bool AddCastBar(EntryRefs refs)
    {
        Element cast = refs.CastFill;
        if (cast == null || cast.Broken)
            return false;
        try
        {
            if (!cast.Graphic.isActiveAndEnabled || cast.Renderer.GetInheritedAlpha() * cast.Graphic.m_Color.a < 0.01f)
                return false;
            if (!refs.CastExtent.Known)
                refs.CastExtent = MeasureExtent(refs.CastNode, cast.Rect);
            cast.Rect.GetWorldCorners(_corners);
            Span<Vector3> c = _corners.AsSpan();
            return AddBar(V2(c[0]), V2(c[1]), V2(c[2]), V2(c[3]), refs.CastExtent);
        }
        catch (Exception e)
        {
            cast.Broken = true;
            WarnOnce($"the cast bar can't be read ({e.Message}); it stays on the flat HUD");
            return false;
        }
    }

    /// <summary>
    /// A bar part: the bar image (its core, given by its screen corners) and how far its container reaches around
    /// it (the image's corners stretched by the measured extent). True if it was added.
    /// </summary>
    private bool AddBar(Num.Vector2 bl, Num.Vector2 tl, Num.Vector2 tr, Num.Vector2 br, in Extent extent)
    {
        if (!AddBox(bl, tl, tr, br, bar: true, CutMargin))
            return false;
        Num.Vector2 ex = br - bl, ey = tl - bl;
        Box(bl + ex * extent.X0 + ey * extent.Y0, bl + ex * extent.X0 + ey * extent.Y1,
            bl + ex * extent.X1 + ey * extent.Y1, bl + ex * extent.X1 + ey * extent.Y0,
            out float u0, out float v0, out float u1, out float v1);
        Part part = _parts[_parts.Count - 1];
        part.EU0 = Math.Min(part.U0, u0);
        part.EV0 = Math.Min(part.V0, v0);
        part.EU1 = Math.Max(part.U1, u1);
        part.EV1 = Math.Max(part.V1, v1);
        _parts[_parts.Count - 1] = part;
        return true;
    }

    /// <summary>
    /// How far everything showing in a bar's container reaches past the bar image, in fractions of the image's
    /// rect (an elite bar's glow reaches well above and below it). Measured once per entry, while the bar shows.
    /// </summary>
    private Extent MeasureExtent(Transform node, RectTransform reference)
    {
        var extent = new Extent { X0 = 0f, Y0 = 0f, X1 = 1f, Y1 = 1f, Known = true };
        if (node == null || reference == null)
            return extent;
        try
        {
            reference.GetWorldCorners(_corners);
            Span<Vector3> c = _corners.AsSpan();
            Num.Vector2 o = V2(c[0]), ex = V2(c[3]) - o, ey = V2(c[1]) - o;
            float det = Cross(ex, ey);
            if (!(MathF.Abs(det) >= 1f))
                return extent;
            foreach (UIGraphic g in node.GetComponentsInChildren<UIGraphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled || g.m_Color.a < 0.01f || g.TryCast<TMP_SubMeshUI>() != null)
                    continue;
                g.rectTransform.GetWorldCorners(_corners);
                c = _corners.AsSpan();
                for (int k = 0; k < 4; k++)
                {
                    Num.Vector2 p = V2(c[k]) - o;
                    float x = Cross(p, ey) / det, y = Cross(ex, p) / det;
                    extent.X0 = Math.Min(extent.X0, x);
                    extent.Y0 = Math.Min(extent.Y0, y);
                    extent.X1 = Math.Max(extent.X1, x);
                    extent.Y1 = Math.Max(extent.Y1, y);
                }
            }
            // A stray helper object mustn't turn the bar into a huge cut.
            extent.X0 = Math.Max(extent.X0, -0.25f);
            extent.X1 = Math.Min(extent.X1, 1.25f);
            extent.Y0 = Math.Max(extent.Y0, -3f);
            extent.Y1 = Math.Min(extent.Y1, 4f);
        }
        catch (Exception e)
        {
            WarnOnce($"measuring a bar's container failed ({e.Message}); the bar image alone is used");
        }
        return extent;
    }

    private static float Cross(Num.Vector2 a, Num.Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>The entry's labels that show this frame, as label parts.</summary>
    private void AddLabels(EntryRefs refs)
    {
        foreach (Group group in refs.Groups)
        {
            // One check skips a whole hidden container (a cast-time text, a progress bar, a voice icon).
            if (group.Container != null && !group.Container.activeInHierarchy)
                continue;
            foreach (Element element in group.Elements)
                AddElement(element);
        }
    }

    /// <summary>Adds the element as a label part if it shows; true if it did.</summary>
    private bool AddElement(Element element)
    {
        if (element.Broken)
            return false;
        try
        {
            return AddShowingElement(element);
        }
        catch (Exception e)
        {
            // One odd element mustn't take every bar down; it stays on the flat HUD.
            element.Broken = true;
            WarnOnce($"an overhead element can't be read ({e.Message}); it stays on the flat HUD");
            return false;
        }
    }

    private bool AddShowingElement(Element element)
    {
        if (!element.Graphic.isActiveAndEnabled)
            return false;
        TMP_Text text = element.Text;
        float alpha = element.Renderer.GetInheritedAlpha() * (text != null ? text.m_fontColor.a : element.Graphic.m_Color.a);
        if (alpha < 0.01f)
            return false;
        if (text == null)
        {
            element.Rect.GetWorldCorners(_corners);
            Span<Vector3> c = _corners.AsSpan();
            return AddBox(V2(c[0]), V2(c[1]), V2(c[2]), V2(c[3]), bar: false, CutMargin);
        }

        // Text: its glyphs' box, in its own (local) units, rather than its rect, which is often much wider.
        Bounds bounds = text.textBounds;
        Vector3 centre = bounds.m_Center, extents = bounds.m_Extents;
        if (!(extents.x > 0f) || !(extents.y > 0f))
            return false; // empty
        Matrix4x4 m = element.Transform.localToWorldMatrix;
        float x0 = centre.x - extents.x, x1 = centre.x + extents.x;
        float y0 = centre.y - extents.y, y1 = centre.y + extents.y;
        float pixelsPerUnit = MathF.Sqrt(m.m00 * m.m00 + m.m10 * m.m10);
        return AddBox(ToScreen(m, x0, y0), ToScreen(m, x0, y1), ToScreen(m, x1, y1), ToScreen(m, x1, y0), bar: false,
            TextMargin * Math.Clamp(pixelsPerUnit, 0.5f, 4f));
    }

    /// <summary>A point in the element's local space, in screen pixels (an overlay canvas's world space).</summary>
    private static Num.Vector2 ToScreen(in Matrix4x4 m, float x, float y) =>
        new(m.m00 * x + m.m01 * y + m.m03, m.m10 * x + m.m11 * y + m.m13);

    /// <summary>A box given by its screen corners, along the current entry's axes.</summary>
    private void Box(Num.Vector2 a, Num.Vector2 b, Num.Vector2 c, Num.Vector2 d, out float u0, out float v0, out float u1, out float v1)
    {
        float ua = U(a), ub = U(b), uc = U(c), ud = U(d);
        float va = V(a), vb = V(b), vc = V(c), vd = V(d);
        u0 = Math.Min(Math.Min(ua, ub), Math.Min(uc, ud));
        u1 = Math.Max(Math.Max(ua, ub), Math.Max(uc, ud));
        v0 = Math.Min(Math.Min(va, vb), Math.Min(vc, vd));
        v1 = Math.Max(Math.Max(va, vb), Math.Max(vc, vd));
    }

    /// <summary>Adds a part given by its screen corners, with a margin, along the current entry's axes.</summary>
    private bool AddBox(Num.Vector2 a, Num.Vector2 b, Num.Vector2 c, Num.Vector2 d, bool bar, float margin)
    {
        Box(a, b, c, d, out float u0, out float v0, out float u1, out float v1);
        // Skip empty or broken boxes, and anything too big to belong to one character's entry.
        if (!(u1 - u0 >= 0.5f) || !(v1 - v0 >= 0.5f) || u1 - u0 > MaxPartPixels || v1 - v0 > MaxPartPixels)
            return false;
        _parts.Add(new Part { U0 = u0, V0 = v0, U1 = u1, V1 = v1, Margin = margin, Bar = bar });
        return true;
    }

    private float U(Num.Vector2 point) => Num.Vector2.Dot(point - _origin, _axisX);

    private float V(Num.Vector2 point) => Num.Vector2.Dot(point - _origin, _axisY);

    private static bool Overlap(in Part a, in Part b) => a.U0 < b.U1 && b.U0 < a.U1 && a.V0 < b.V1 && b.V0 < a.V1;

    /// <summary>Joins parts of the same kind (bars, labels) whose content overlaps, until none do.</summary>
    private void MergeParts()
    {
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < _parts.Count && !merged; i++)
            {
                for (int j = i + 1; j < _parts.Count; j++)
                {
                    Part a = _parts[i], b = _parts[j];
                    if (a.Bar != b.Bar || !Overlap(a, b))
                        continue;
                    _parts[i] = new Part
                    {
                        U0 = Math.Min(a.U0, b.U0),
                        V0 = Math.Min(a.V0, b.V0),
                        U1 = Math.Max(a.U1, b.U1),
                        V1 = Math.Max(a.V1, b.V1),
                        EU0 = Math.Min(a.EU0, b.EU0),
                        EV0 = Math.Min(a.EV0, b.EV0),
                        EU1 = Math.Max(a.EU1, b.EU1),
                        EV1 = Math.Max(a.EV1, b.EV1),
                        Margin = Math.Max(a.Margin, b.Margin),
                        Bar = a.Bar,
                    };
                    _parts.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        } while (merged);
    }

    /// <summary>
    /// The final pieces (<see cref="_out"/>), from <see cref="_padded"/>:
    /// <list type="number">
    /// <item>each bar's core (its image and margin) stays whole;</item>
    /// <item>labels are cut around the cores, and the label pixels inside a core go with the bar;</item>
    /// <item>the rest of each bar's container (fills reaching out, a highlight, an elite's glow) fills the space
    /// around its core that's still free, as glow pieces, so the glow under a name travels with the name.</item>
    /// </list>
    /// A label never depends on a bar's copy, and no pixel is in two pieces or in none. Mirrored and checked on
    /// random layouts by scratchpad parts_test2.py.
    /// </summary>
    private void LayOut()
    {
        _out.Clear();
        foreach (Part p in _padded)
        {
            if (p.Bar)
                _out.Add(p);
        }
        int cores = _out.Count;
        foreach (Part p in _padded)
        {
            if (!p.Bar)
                SubtractAll(p, _out, 0, cores, bar: false);
        }
        for (int i = 0; i < _parts.Count; i++)
        {
            Part bar = _parts[i], core = _padded[i];
            if (!bar.Bar)
                continue;
            var extent = new Part
            {
                U0 = Math.Min(bar.EU0 - bar.Margin, core.U0),
                V0 = Math.Min(bar.EV0 - bar.Margin, core.V0),
                U1 = Math.Max(bar.EU1 + bar.Margin, core.U1),
                V1 = Math.Max(bar.EV1 + bar.Margin, core.V1),
            };
            _ring.Clear();
            CutOut(extent, core, _ring, bar: true);
            foreach (Part ring in _ring)
                SubtractAll(ring, _out, 0, _out.Count, bar: true);
        }
    }

    /// <summary>Adds what's left of <paramref name="box"/> after removing <paramref name="holes"/>[from..to) to <see cref="_out"/>.</summary>
    private void SubtractAll(in Part box, List<Part> holes, int from, int to, bool bar)
    {
        _work.Clear();
        _done.Clear();
        _work.Add(box);
        while (_work.Count > 0)
        {
            Part piece = _work[_work.Count - 1];
            _work.RemoveAt(_work.Count - 1);
            bool cut = false;
            for (int h = from; h < to; h++)
            {
                if (Overlap(piece, holes[h]))
                {
                    CutOut(piece, holes[h], _work, bar);
                    cut = true;
                    break;
                }
            }
            if (!cut)
                _done.Add(piece);
        }
        _out.AddRange(_done);
    }

    /// <summary><paramref name="box"/> minus an overlapping <paramref name="hole"/>: up to four pieces (above and below full width, then left and right).</summary>
    private static void CutOut(in Part box, in Part hole, List<Part> pieces, bool bar)
    {
        float v0 = Math.Max(box.V0, hole.V0), v1 = Math.Min(box.V1, hole.V1);
        AddPiece(pieces, box.U0, hole.V1, box.U1, box.V1, bar);
        AddPiece(pieces, box.U0, box.V0, box.U1, hole.V0, bar);
        AddPiece(pieces, box.U0, v0, hole.U0, v1, bar);
        AddPiece(pieces, hole.U1, v0, box.U1, v1, bar);
    }

    private static void AddPiece(List<Part> pieces, float u0, float v0, float u1, float v1, bool bar)
    {
        // Even a thin piece is kept: dropping it would leave a crack between the cuts.
        if (u1 - u0 > 1e-3f && v1 - v0 > 1e-3f)
            pieces.Add(new Part { U0 = u0, V0 = v0, U1 = u1, V1 = v1, Bar = bar });
    }

    /// <summary>
    /// The parts with their margins (<see cref="_padded"/>). Where two margins of the same kind (two labels, or
    /// the bar and the cast bar) would reach into each other, they meet at one line between the two elements
    /// instead, along the axis that separates them most (the name above the level text, the level badge beside
    /// the name), so together the cuts hold every pixel exactly once. Labels and bars are sorted out by
    /// <see cref="LayOut"/>.
    /// </summary>
    private void PadParts()
    {
        _padded.Clear();
        foreach (Part p in _parts)
        {
            _padded.Add(new Part
            {
                U0 = p.U0 - p.Margin, V0 = p.V0 - p.Margin, U1 = p.U1 + p.Margin, V1 = p.V1 + p.Margin, Bar = p.Bar, Core = p.Bar,
            });
        }
        for (int i = 0; i < _padded.Count; i++)
        {
            for (int j = i + 1; j < _padded.Count; j++)
            {
                Part a = _padded[i], b = _padded[j];
                if (a.Bar != b.Bar || !Overlap(a, b))
                    continue;
                // The contents don't overlap (they'd have been merged), so one of the gaps is >= 0.
                Part ca = _parts[i], cb = _parts[j];
                float gapU = Math.Max(cb.U0 - ca.U1, ca.U0 - cb.U1);
                float gapV = Math.Max(cb.V0 - ca.V1, ca.V0 - cb.V1);
                if (gapU >= gapV)
                {
                    if (ca.U1 <= cb.U0)
                        Split(ref a.U1, ref b.U0, 0.5f * (ca.U1 + cb.U0));
                    else
                        Split(ref b.U1, ref a.U0, 0.5f * (cb.U1 + ca.U0));
                }
                else
                {
                    if (ca.V1 <= cb.V0)
                        Split(ref a.V1, ref b.V0, 0.5f * (ca.V1 + cb.V0));
                    else
                        Split(ref b.V1, ref a.V0, 0.5f * (cb.V1 + ca.V0));
                }
                _padded[i] = a;
                _padded[j] = b;
            }
        }
    }

    /// <summary>
    /// Moves a lower (or left) box's far edge and its neighbour's near edge to one line, as close to
    /// <paramref name="middle"/> as both margins reach: no strip is in both cuts or in neither.
    /// </summary>
    private static void Split(ref float lowerEnd, ref float upperStart, float middle)
    {
        float line = Math.Min(Math.Max(middle, upperStart), lowerEnd);
        lowerEnd = line;
        upperStart = line;
    }

    private void AddCut(in Part part, int owner)
    {
        var cut = new Cut
        {
            Q0 = Point(part.U0, part.V0),
            Q1 = Point(part.U0, part.V1),
            Q2 = Point(part.U1, part.V1),
            Q3 = Point(part.U1, part.V0),
            Owner = owner,
            IsBar = part.Bar,
            IsCore = part.Core,
        };
        cut.Min = Num.Vector2.Min(Num.Vector2.Min(cut.Q0, cut.Q1), Num.Vector2.Min(cut.Q2, cut.Q3));
        cut.Max = Num.Vector2.Max(Num.Vector2.Max(cut.Q0, cut.Q1), Num.Vector2.Max(cut.Q2, cut.Q3));
        Cuts.Add(cut);
    }

    private Num.Vector2 Point(float u, float v) => _origin + _axisX * u + _axisY * v;

    private static Num.Vector2 V2(in Vector3 v) => new(v.x, v.y);

    private static Num.Vector2 Direction(Num.Vector2 v, Num.Vector2 fallback) =>
        v.LengthSquared() > 1e-6f ? Num.Vector2.Normalize(v) : fallback;

    /// <summary>
    /// Once, for checking on hardware: the first entry with a label, its cuts and every graphic that shows in
    /// it, so an element the cuts miss (it would stay on the flat HUD) can be found.
    /// </summary>
    private void LogLayout(EntryRefs refs, int firstCut)
    {
        try
        {
            var log = new System.Text.StringBuilder();
            log.Append($"3D health bars: the first entry with a label (type {refs.Type}) moves to 3D as {Cuts.Count - firstCut} cut(s):");
            for (int k = firstCut; k < Cuts.Count; k++)
            {
                Cut c = Cuts[k];
                log.Append($" [{(c.IsCore ? "bar" : c.IsBar ? "glow" : "label")} {c.Min.X:F0},{c.Min.Y:F0}..{c.Max.X:F0},{c.Max.Y:F0}]");
            }
            log.Append($"; bar extent {refs.FrameExtent.X0:F2},{refs.FrameExtent.Y0:F2}..{refs.FrameExtent.X1:F2},{refs.FrameExtent.Y1:F2}" +
                       $" of the frame; {refs.Groups.Length} label group(s). Its visible graphics (screen px):");
            foreach (UIGraphic g in refs.Entry.GetComponentsInChildren<UIGraphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled)
                    continue;
                g.rectTransform.GetWorldCorners(_corners);
                Span<Vector3> q = _corners.AsSpan();
                float minX = Math.Min(Math.Min(q[0].x, q[1].x), Math.Min(q[2].x, q[3].x));
                float minY = Math.Min(Math.Min(q[0].y, q[1].y), Math.Min(q[2].y, q[3].y));
                float maxX = Math.Max(Math.Max(q[0].x, q[1].x), Math.Max(q[2].x, q[3].x));
                float maxY = Math.Max(Math.Max(q[0].y, q[1].y), Math.Max(q[2].y, q[3].y));
                log.Append($"\n  {g.name} ({g.GetIl2CppType().Name}) alpha {g.canvasRenderer.GetInheritedAlpha():F2} " +
                           $"rect {minX:F0},{minY:F0}..{maxX:F0},{maxY:F0}");
            }
            Plugin.Logger.LogInfo(log.ToString());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"3D health bars: logging the entry layout failed: {e.Message}");
        }
    }

    private void ResolveCanvas()
    {
        // EntriesParent is the CharacterHUDs canvas object (the entries and the interact prompt live under
        // it). Not SetCharacterHUDSystem._HudCanvas: that's the first Canvas under the HUD root (ReplayCanvas).
        Transform parent = _set.EntriesParent;
        Canvas canvas = parent != null ? parent.GetComponent<Canvas>() : null;
        if (canvas != null && canvas.isRootCanvas && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            OverheadCanvas = canvas;

        // The combat text has its own root canvas next to it under HUDCanvas(Clone).
        Transform hudRoot = parent != null ? parent.parent : null;
        if (hudRoot == null)
            return;
        for (int i = 0; i < hudRoot.childCount; i++)
        {
            Transform child = hudRoot.GetChild(i);
            if (child == null || !child.name.Contains("SCT"))
                continue;
            Canvas sct = child.GetComponent<Canvas>();
            if (sct != null && sct.isRootCanvas && sct.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                SctCanvas = sct;
                break;
            }
        }
    }

    private bool EnsureSystems()
    {
        if (_world != null && !_world.IsCreated)
            Reset(); // world torn down (disconnect, server change)
        if (_set != null && _get != null)
            return true;
        if (Time.unscaledTime < _retryAt)
            return false;
        _retryAt = Time.unscaledTime + 1f;

        var all = World.All;
        for (int i = 0; i < all.Count; i++)
        {
            World w = all[i];
            if (w != null && w.IsCreated && w.Name == "Client_0")
            {
                _world = w;
                break;
            }
        }
        if (_world == null)
            return false;
        _set = _world.GetExistingSystemManaged<SetCharacterHUDSystem>();
        _get = _set != null ? _set._CharacterHUDSystem : null;
        if (_set == null || _get == null)
        {
            _world = null;
            _set = null;
            _get = null;
            return false;
        }

        if (_stride == 0)
        {
            _translation = new ComponentType(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadOnly);
            _characterHud = new ComponentType(Il2CppType.Of<CharacterHUD>(), ComponentType.AccessMode.ReadOnly);
            uint align = 0;
            _stride = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<CharacterHUDEntry.Data>.NativeClassPtr, ref align);
            Plugin.Logger.LogInfo($"3D health bars: HUD systems found; entry size {_stride} bytes (276 in 1.1.13), managed {sizeof(CharacterHUDEntry.Data)}.");
            // The fields read here end at 272; a smaller element means the layout changed in a game patch.
            if (_stride < 273)
            {
                Plugin.Logger.LogError("3D health bars: unexpected CharacterHUDEntry.Data layout; disabled until the interop is regenerated.");
                _disabled = true;
                Reset();
                return false;
            }
        }
        return true;
    }

    /// <summary>A visible element of the entry being read, as a box along its axes, in pixels from its root's pivot.</summary>
    private struct Part
    {
        /// <summary>The element's box; for a bar, its image (the core).</summary>
        public float U0, V0, U1, V1;
        /// <summary>A bar's whole container around its image (fills reaching out, a highlight, a glow).</summary>
        public float EU0, EV0, EU1, EV1;
        /// <summary>The margin kept around it (a bar's glow, a text's outline and shadow).</summary>
        public float Margin;
        /// <summary>A bar piece (its core or its glow), as opposed to a label.</summary>
        public bool Bar;
        /// <summary>A bar's core: its image with the margin.</summary>
        public bool Core;
    }

    /// <summary>A bar's whole look relative to its image, in fractions of the image's rect (0..1 is the image).</summary>
    private struct Extent
    {
        public float X0, Y0, X1, Y1;
        public bool Known;
    }

    /// <summary>Where a kind of entry's bar frame sits at rest: its scale below the entry root, and its centre in entry units.</summary>
    private struct RestFrame
    {
        public float ChainX, ChainY, CentreU, CentreV;
    }

    /// <summary>A graphic of an entry that can show: for text, the component that knows its glyphs' box.</summary>
    private sealed class Element
    {
        public readonly UIGraphic Graphic;
        public readonly CanvasRenderer Renderer;
        public readonly RectTransform Rect;
        public readonly Transform Transform;
        public readonly TMP_Text Text;
        /// <summary>Reading it failed once; it's left on the flat HUD from then on.</summary>
        public bool Broken;

        public Element(UIGraphic graphic, TMP_Text text)
        {
            Graphic = graphic;
            Renderer = graphic.canvasRenderer;
            Rect = graphic.rectTransform;
            Transform = graphic.transform;
            Text = text;
        }
    }

    /// <summary>The labels under one of the entry's top-level containers, which one check can skip while it's hidden.</summary>
    private sealed class Group
    {
        /// <summary>Null when the container is the group's only graphic (its own check covers it).</summary>
        public readonly GameObject Container;
        public readonly Element[] Elements;

        public Group(GameObject container, Element[] elements)
        {
            Container = container;
            Elements = elements;
        }
    }

    /// <summary>
    /// The UI objects of one pooled entry instance: the health bar (the reference the entry is placed by) and its
    /// container, the cast bar and its container, and every other graphic as a label (name, level badge, icons,
    /// texts, a blood type's backdrop), grouped by the entry's top-level containers. Found by walking the entry's
    /// own hierarchy, so elements no component field names come along too. Holding the entry keeps its native
    /// object alive, so its address (the cache key) can't be reused while it's cached.
    /// </summary>
    private sealed class EntryRefs
    {
        /// <summary>For an entry that can't be read at all: it's skipped, so it stays on the flat HUD.</summary>
        public static readonly EntryRefs Unusable = new();

        public readonly CharacterHUDEntry Entry;
        public readonly GameObject GameObject;
        public readonly Transform Root;
        /// <summary>The node holding everything (RootCanvasGroup), which the game scales with the camera zoom.</summary>
        public readonly Transform Content;
        public readonly int Type;
        public readonly UIImage Fill, Frame, Damage;
        public readonly CanvasRenderer FillRenderer;
        public readonly RectTransform FrameRect;
        public readonly Transform FrameNode, CastNode;
        public readonly Element CastFill;
        public readonly Group[] Groups = Array.Empty<Group>();
        /// <summary>Why some of its parts couldn't be resolved (the bar still works); null if all were.</summary>
        public readonly string Partial;
        public Extent FrameExtent, CastExtent;
        // Last frame's frame geometry, to tell when the bar holds still.
        public float LastChainX = -1f, LastCentreU, LastCentreV;

        private EntryRefs()
        {
        }

        public EntryRefs(CharacterHUDEntry entry)
        {
            Entry = entry;
            GameObject = entry.gameObject;
            Root = entry.transform;
            Type = (int)entry.EntryType;
            CharacterHUD_HealthComponent health = entry.Health;
            if (health != null)
            {
                Fill = health.Image;
                Frame = health.FrameImage;
                Damage = health.RecentDamageTakenImage;
            }
            FillRenderer = Fill != null ? Fill.canvasRenderer : null;
            if (FillRenderer == null)
                Fill = null;
            // The frame image covers the whole 2D bar.
            FrameRect = Frame != null ? Frame.rectTransform : Fill != null ? Fill.rectTransform : null;

            // Entries keep everything under one RootCanvasGroup, the node the game's zoom scales.
            CanvasGroup rootGroup = entry.RootCanvasGroup;
            Content = rootGroup != null ? rootGroup.transform : Root.childCount == 1 ? Root.GetChild(0) : Root;
            try
            {
                Transform content = Content;
                if (FrameRect != null)
                    FrameNode = BarNode(FrameRect, Fill != null ? Fill.transform : null, content);
                CharacterHUD_CastTimeComponent castBar = entry.CastBar;
                UIImage castFill = castBar != null ? castBar.CastBarFill : null;
                if (castFill != null)
                {
                    CastFill = new Element(castFill, null);
                    CastNode = BarNode(castFill.transform, null, content);
                }
                Groups = FindLabels(content);
            }
            catch (Exception e)
            {
                Partial = e.Message;
            }
        }

        /// <summary>
        /// The container a bar is drawn with: the lowest common ancestor of its image and its fill (or the image's
        /// parent), unless that's the whole entry, in which case only the image itself.
        /// </summary>
        private Transform BarNode(Transform image, Transform fill, Transform content)
        {
            Transform node = image.parent;
            if (fill != null)
            {
                var ancestors = new HashSet<IntPtr>();
                for (Transform t = image; t != null && t.Pointer != Root.Pointer; t = t.parent)
                    ancestors.Add(t.Pointer);
                node = null;
                for (Transform t = fill; t != null && t.Pointer != Root.Pointer; t = t.parent)
                {
                    if (ancestors.Contains(t.Pointer))
                    {
                        node = t;
                        break;
                    }
                }
            }
            return node == null || node.Pointer == content.Pointer || node.Pointer == Root.Pointer ? image : node;
        }

        private Group[] FindLabels(Transform content)
        {
            var groups = new List<Group>();
            for (int i = 0; i < content.childCount; i++)
            {
                Transform container = content.GetChild(i);
                var elements = new List<Element>();
                foreach (UIGraphic g in container.GetComponentsInChildren<UIGraphic>(true))
                {
                    // TMP's sub-meshes (fallback fonts) are inside their text's glyph box.
                    if (g == null || g.TryCast<TMP_SubMeshUI>() != null || Under(g.transform, FrameNode) || Under(g.transform, CastNode))
                        continue;
                    elements.Add(new Element(g, g.TryCast<TMP_Text>()));
                }
                if (elements.Count == 0)
                    continue;
                bool alone = elements.Count == 1 && elements[0].Transform.Pointer == container.Pointer;
                groups.Add(new Group(alone ? null : container.gameObject, elements.ToArray()));
            }
            return groups.ToArray();
        }

        private bool Under(Transform t, Transform node)
        {
            if (node == null)
                return false;
            for (; t != null && t.Pointer != Root.Pointer; t = t.parent)
            {
                if (t.Pointer == node.Pointer)
                    return true;
            }
            return false;
        }
    }
}
