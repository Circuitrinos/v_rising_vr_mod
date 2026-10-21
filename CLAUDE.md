# Read-Only Sources

Modding documentation for V Rising can be found here: https://wiki.vrisingmods.com/dev/
I have clipped relevant pages to the Documentation folder.

Start here: [[dev]]

Let me know if you need me to clip more pages.

For testing purposes, the game install location is: E:\Steam\steamapps\common\VRising
SteamVR is located here: C:\Program Files (x86)\Steam\steamapps\common\SteamVR

---

# Goals

1. I want to make a VR camera mod for V Rising.
2. I don't want the VR to be in the first person of the player model
	1. I want the default birds eye view camera to be in VR, so I can watch the game like I'm watching a 3d board game.
3. Make the mod as local only as possible to not interfere with servers.
4. I want my head rotation to work, so my vision isn't fixed in place causing motion sickness.
5. The normal camera rotation mechanic that pivots around the character should also remain.
6. Add settings in the game for controlling the mod.


---
# Claude, add additional information here

## Environment facts (verified 2026-10-20)
- Game: V Rising 1.1.13 (build r99712), Unity **2022.3.58f1**, **IL2CPP**, **HDRP**, renders on **Direct3D 11**.
- Mod loader: **BepInExPack_V_Rising 1.733.2** from Thunderstore. The vanilla BepInEx be.733 Cpp2IL fails on this game with "Failed to find code registration"; the pack's patched LibCpp2IL works.
- Interop assemblies are in `E:\Steam\steamapps\common\VRising\BepInEx\interop`. They are generated on first launch, and the csproj references them directly. The NuGet `VRising.Unhollowed.Client` package only goes up to 1.1.9, so it doesn't match this game version.
- Unity's legacy `Input` may be unavailable, so use `UnityEngine.InputSystem.Keyboard.current[Key.X]`.
- `HDAdditionalCameraData.CopyTo` **is** present (native body verified by the review workflow). The eyes use it, then re-apply the VR overrides. The game changes its camera at runtime (ConnectingView and CharacterCreationCamera swap the culling and volume masks, and the Options menu changes AA), so the eyes re-sync every 2 s.
- The game's FPS limiter is `ProjectM.SettingsManager._ClientSettings.GraphicSettings.SetFPSLimit(bool inGame)` (ProjectM.Shared). It applies a 60 fps cap in non-HUD views (menus, loading, Escape menu). The test PC's display is 165 Hz.
- Logs: `BepInEx\LogOutput.log`, and `%USERPROFILE%\AppData\LocalLow\Stunlock Studios\VRising\Player.log`.
- Verified on hardware (2026-10-20): Unity render textures on D3D11 come out upside down in SteamVR unless V is flipped in `VRTextureBounds_t` (vMin=1, vMax=0). `Camera.main` in V Rising is a GameObject named `CameraParent`. Il2CppInterop `!=` on Unity objects compares native objects, so `game != _sourceCamera` is stable.
- SteamVR's `vrcompositor.txt` shows `WaitForAcquire timed out` / `AcquireSync FAILED` warnings even when the game isn't running. They're Steam Link driver noise, not caused by the mod. Steam logs: `C:\Program Files (x86)\Steam\logs\` (`vrclient_VRising.txt` = the mod's OpenVR client). The test PC's GPU is an RTX 2070, and SteamVR recommends 2516x2516 per eye at 100%.
- **Interop gotcha:** never read or write HDRP `ref`-returning properties such as `renderingPathCustomFrameSettings`. Il2CppInterop returns a temporary boxed copy, so writes are silently lost. Use the `m_` backing field (`m_RenderingPathCustomFrameSettings`) instead.
- **The "distortion", diagnosed from a 3D capture (F5) on 2026-10-20:** the eye views (109° FOV) also see the trees, cliffs and roofs directly around and below the game camera. The desktop's 60° view never shows them. With the game camera's near plane (0.3 game m) at WorldScale 14, they rendered from about 2 cm in real-world terms, so they were huge, unfusable in stereo and swung with head turns. Fix: the eye near plane is `Settings.NearClipMeters` (real m, default 0.3) × WorldScale, plus a `StereoStrength` multiplier. Projection, view and resolution were already verified exact; isolation levels 0-4 made no difference.
- **User test, 2026-10-20: cycling F6 isolation levels fixed the "weird distortion".** Level 4 removes all post-processing, including tonemapping and colour grading, so the image looks washed out. In-VR toasts show the level.
  - **Resolved in the session of 2026-10-20 22:27–23:22.** About 7 minutes in, the user switched to level 0 and played the remaining ~48 minutes there. Afterwards they called the mod "effectively perfect" (they beat a boss in VR). So the near-plane fix was the real cure.
  - Level 0 is the default and the user's saved setting. It cost about 3 fps against level 4 at the switch (33 → 30 fps at RenderScale 1.0).
- **HUD anchoring (v0.6):**
  - `GameCamera` reads the ECS camera state: `LocalUserSystem._CameraEntity`, then `TopdownCameraState.Current.LookAtRootPos` minus `TopdownCamera.LookAtHeight` gives the feet.
  - The HUD's center aims at the feet as seen from the recentered head position (the user's request). The desktop's center pixel is the look-at pivot, LookAtHeight (about 1.2 game m) above the feet, so a cursor drawn on the HUD reads about 3° above where the game aims (about 1.2 game m beyond the character). The ground cursor is the accurate aim indicator.
  - `VRRig.GroundSafeDistance` caps the distance so the HUD's **lowest edge** stays above the ground plane at the feet. A point above that plane is in front of the ground from any head position above it. The cap is `GroundMargin × eyeHeight / (StereoStrength≥1 × (sin a + halfTan·cos a))`. The old bound, `GroundMargin × distance to the feet`, only protected the centre ray, so the action bar still sank in (review wyjvaga93).
  - The width scales with the placed distance (`HudLayer`), so the angular size stays at HUD.Width/HUD.Distance, and the smoothed distance is clamped to the cap so the glide never lags into the ground. The panel is placed every frame, because the old 0.2°/5 mm thresholds made the glides step.
  - The HUD is laid out from the camera entity's `Translation` (`Unity.Transforms.Translation`, in Unity.Entities.dll). HybridCameraSystem poses the Unity camera at `Translation + shakeAdditive` (position only), so the HUD ignores screen shake. The eyes and the ground cursor still follow the shaken Unity camera.
  - The eyes are re-placed in `RenderPipelineManager.beginContextRendering`, because the game's camera systems run after LateUpdate.
- **Cursor rules (v0.7):**
  - `ProjectM.CursorController._CursorType` is `Game_Normal` in gameplay and `Menu_Normal` in menus.
  - In menus the VR cursor (game cursor image as top-most UI) shows on the HUD layer.
  - In gameplay, `GroundReticle` draws the game's crosshair lying flat on the ground **into the eye images** (v0.10; it was a SteamVR overlay, which the compositor draws over everything, so it covered the character). The position is the ECS `ProjectM.CursorPosition` singleton (ProjectM.Shared), field **`WorldPosition`**: the nearest GameplayHeight hit under the cursor, which `GameplayInputSystem.UpdateAimKBM` aims with. Fallback: `WorldPositionPlayerPlane` while WorldPosition is still zero. `WorldPositionPlayerPlaneTerrainProjection` uses the terrain height under the *character*, so it is wrong on floors and slopes (review w2p4pgjj2).
  - **CursorPosition lives on CursorPositionSystem's system entity.** Entities 1.x queries skip system entities unless `EntityQueryOptions.IncludeSystems` is set, so the first version's plain query always found 0 entities and the reticle never showed.
  - ECS components are read with `EntityManager.GetComponentDataRawRO(entity, typeIndex)`. `GetComponentData<T>` has no exact AOT instantiation for these types, only IL2CPP's fully shared fallback.
  - Depth: the eye RTs have a 24-bit depth buffer, so HDRP's `SetFinalTarget` copies the camera's depth into them (`copyDepth` when `targetTexture.depth != 0`). `VRRig.DrawEyeOverlays` then draws the cursor with ZTest LessEqual (`unity_GUIZTestMode` 4 on the material). UnityPlayer's shaderprops::GetFloat checks the material sheet before the global one, so the material value wins (review wkp1kgo4c). It is pulled 0.35 game m toward the viewer so the ground and grass under it don't hide it, but the character in front of it does. Size is in game meters (`GroundCursorSize`, default 0.4).
  - **The aim ray passes through characters.** CursorPositionSystem raycasts GameplayHeight (0x40) only, so with the mouse over an enemy, `WorldPosition` is the ground *behind* it, and the depth-tested cursor hides behind the enemy. A second pass with ZTest Greater (5) draws the hidden parts at `GroundCursorHiddenAlpha` (default 0.3), so the aim point never vanishes.
  - `SetCursorSystem` (the only caller of `CursorController.Set`): gamepad → None_LockedToCenter; ViewLocation ≠ None → Menu_Normal; RotateCamera held → None_Locked (both cursors hide); any `OpenHUDMenuSystem.CurrentMenuType` (Tab, M, Esc, containers, stations, death) → Menu_Normal; otherwise Game_Normal. Unverified: the build menu (B).
  - `Texture.GetNativeTexturePtr` is a full main/render-thread sync on this build (`GfxDeviceClient::AcquireThreadOwnership`), so call it only when a texture object changes.
  - User test 2026-10-20 18:11: the overlay cursor at 0.08 real m (1.1 game m at WorldScale 14) was "too large and draws over the character".
- **3D health bars (v0.9; working on hardware 2026-10-20 18:11, colours fixed in v0.10):**
  - Hardware: entry size 276, CharacterHUDsCanvas found. CutBars removed the 2D bars from the VR HUD and kept the level label. The bars sat above the bear and the player with correct fills (player 77/235).
  - But `Health.CurrentTeamHUDSettings` colours came out near-white (RGB 224-240), not the red/cyan the game shows. The review explained why: `RefreshData` never writes that field; it applies `Data.TeamSettings` colours through `UIHelper.SetColor`. So `Graphic.color` is the applied colour, and the asset's colours are LocalPlayer/LocalTeam health (0, 0.647, 0.75) and Enemy/Neutral (1, 0.183, 0.34). v0.10 therefore shows the game's **own 2D bar pixels**: `HudLayer.RecordCuts` copies each bar's screen rect into a 1024² atlas (`CopyTexture`, row-packed) just before blanking it, and `WorldBars` maps the atlas rect onto the 3D quad at the 2D bar's aspect. Without this frame's atlas, a bar falls back to plain quads tinted with the images' `Graphic.color`.
  - Data: `GameHud` pairs `GetCharacterHUDSystem.Entries[i]` (`NativeArray<CharacterHUDEntry.Data>`, read through `m_Buffer`, stepping by the native size from `il2cpp_class_value_size`, which is 276) with `SetCharacterHUDSystem._Instances[i]`. `SetCharacterHUDSystem.OnUpdate` pairs them the same way.
    - One snapshot per frame (`Refresh` caches by `Time.frameCount`), shared by the HUD layer (before the eyes) and the eye draw, so each bar is in exactly one place.
    - Visibility: `CharacterHUD_HealthComponent` sits on the entry root, so check its **fill image** (`Health.Image`) subtree. The fade is `fill.canvasRenderer.GetInheritedAlpha()`.
    - Anchor = `Translation + (0, CharacterHUD.Height._Value, 0)`, the point the game projects. `Data.Position` is desktop pixels, not world space.
    - Fills come from Data. Colours come from `Health.CurrentTeamHUDSettings`, sent **raw**, because CharacterHUDsCanvas has VertexColorAlwaysGammaSpace off; an sRGB→linear conversion would make them darker than the desktop. Width ∝ `FrameImage.rect.width / 300` (elites ×1.6).
    - The overhead canvas is `SetCharacterHUDSystem.EntriesParent.GetComponent<Canvas>()`. `_HudCanvas` is ReplayCanvas. **The interact prompt also lives on CharacterHUDsCanvas** (review w2shu114g).
  - Drawing: `WorldBars` builds quads with System.Numerics (UnityEngine.Vector3 maths is a native call per operation in this interop). The UI/Default material copy draws into both eye RTs with each eye's `worldToCameraMatrix`/`projectionMatrix`, in `endCameraRendering` once both eyes are done (`context.ExecuteCommandBuffer` + `context.Submit()`, then the submit event).
    - Drawn after HDRP, so no tonemapping, bloom or AA. They aren't depth-tested (material ZTest Always, value 8), like the desktop's overlay bars.
    - **The game rolls each 2D entry** by up to ±10° (`Transform.Rotate` with -ScreenPosNormalized × MaxZRotation 10). So each cut keeps its padded rotated corners (GameHud.Cut Q0..Q3), and the 3D quad samples them as per-corner atlas UVs. That un-rolls the bar.
    - A bar is drawn plain instead of textured when any of its bar cuts runs off the screen (a cropped copy would misread the fill) or overlaps another character's cut (its copy would carry the other's pixels).
    - Known minor issue: the HUD RT holds premultiplied texels, and UI/Default multiplies rgb by alpha again, so translucent parts of the textured bars (and text edges) come out slightly darker.
  - **Names, levels, icons and blood types go with the bars (v0.12; the user asked for names on the 3D bars, 2026-10-20).**
    - **Working on hardware** (session 2026-10-20 22:27–23:22): up to 7 entries in 3D at once, overhead read 0.01–0.10 ms/frame, no warnings. The blood-type attachment never appeared in that log, so it's still unconfirmed.
    - Prefab facts come from decoding the CharacterHUD bundle (review of v0.12):
    - `HUD_BaseUnitEntry` (mobs, type 1): the bar is `HealthBar` (scale 0.3) → BG 300×30 and fills; the name `Text (TMP)` sits above it. There is no level component.
    - `HUD_CharacterEntry` (type 6, root scale 1.2): `HealthBarParent` holds `HealthBar` → BGFilled (the frame), plus the `LevelDiff` badge and `PvPProtection`. Its In/Out motions animate scale 0.8↔1 and position y 0↔8, and it stays at 0.8 / y 0 while hidden (DisableOnFinish). The `Name` is a sibling with its own motions and a `ServantStatusIcon` child.
    - `HUD_EliteUnitNamedEntry` (type 8): `BarParent` (scale 0.3×0.5) holds BG 480×30, fills and a `BG_Glow` reaching about one bar-height above and below.
    - The hovered character's blood type is a **separate entry**, `HUD_BloodTypeEntry` (type 4). It has no Health, Name or Level, only Blood, with a backdrop image, an icon and a quality text.
  - How `GameHud` reads an entry. Each pooled entry instance is resolved once and cached by its native pointer; the cache holds the entry wrapper, so the address can't be reused while it's cached.
    - **The bar's container** is the lowest common ancestor of `FrameImage` and the fill (`Health.Image`). Everything showing in it is drawn as one bar cut, measured once while the bar shows as an extent in fractions of the frame rect (it covers an elite's glow). The cast bar is the same, with `CastBarFill`'s parent.
    - **Labels** are every other `Graphic` in the entry, found by walking the hierarchy rather than by component fields, so unnamed icons and backdrops come along. They're grouped by the entry's top-level containers under RootCanvasGroup, and one `activeInHierarchy` check skips a hidden container. TMP sub-meshes are skipped.
    - Visible = `isActiveAndEnabled` and colour alpha (`m_Color` / TMP `m_fontColor`, read as fields) × `canvasRenderer.GetInheritedAlpha()` ≥ 0.01.
    - Text uses TMP `textBounds` (the glyph box; text rects are often much wider than the text) mapped through `localToWorldMatrix`, with a 4-unit margin for outline and shadow. Images use their rect plus 2 px.
    - Entries without a bar (the blood type) are **attached** to the bar entry of the same `Data.Entity`, as extra labels. Unmatched ones stay on the flat HUD.
    - Avoid value-returning interop calls where a field will do. `il2cpp_runtime_invoke` boxes every value-type return on the IL2CPP heap, so read `Rect.m_Width/m_Height` rather than `.width`.
  - How the pieces are laid out.
    - Boxes are taken along the axes and units of the entry's **content node**: `entry.RootCanvasGroup.transform`, its `localToWorldMatrix` giving the roll and px per unit. Not the root: the game's `UpdateZoom` scales RootCanvasGroup by `2 − NormalizedZoom`, and measuring below the root baked that zoom into the learned rest frame (second review). The node sits on the root's pivot and isn't rotated itself.
    - The reference is the bar frame **at rest**: per entry type, the frame's scale below the content node and its centre in content units. It is learned whenever a bar shows (any alpha, since zoomed in the game keeps bars faded) and holds still for a frame; the motions move it every frame. Without that, CharacterEntry names grew 25% and rose about 2× whenever a player's bar hid (review). Until a type is learned, the current frame is used.
    - A bar part has a **core** (its image, the frame or the cast fill) and an extent (its whole container). Same-kind contents (label+label, core+core) that overlap merge. Margins of same-kind neighbours meet at one line (`Split` clamps the midpoint into both margins, so no strip is in both cuts or in neither).
    - `LayOut` then:
      - keeps each padded core whole;
      - cuts labels around the cores, so label pixels inside a core go with the bar;
      - fills the rest of each bar's padded extent with **glow pieces**, wherever no core or label is.
    - So the glow under a name travels with the name. In the first version the elite glow box swallowed the boss name's lower half, which vanished whenever the bar went plain (second review).
    - `scratchpad/parts_test2.py` mirrors this and checks 30,000 random layouts: no overlaps, cores intact, every label and extent pixel in some cut, no label pixel in a glow piece.
    - Each piece is a cut (`IsBar` for cores and glow, `IsCore` for cores).
      - A core needs a whole copy that doesn't overlap another character's core, as in v0.11. Otherwise the entry's bars are drawn plain, inside the frame box so labels keep clear, and its glow pieces aren't drawn.
      - Labels and glow pieces are copied even where entries overlap, so they may carry bits of the other entry, as the desktop shows them.
      - A label that can't be copied isn't blanked, so it stays on the flat HUD. Bar pieces are always blanked.
      - Overlap tests are separating-axis tests on the rolled boxes.
    - In 3D each cut sits at its 2D offset from the rest frame's centre, along the entry's axes, at metres-per-pixel = 3D bar width / rest frame pixel width. While a bar animates, it pops in like the 2D one and the labels hold still.
    - The atlas packs cuts tallest-first.
  - Known limits:
    - Blanking uses each cut's screen AABB, so at ±10° roll it also erases nearby pixels that don't move to 3D, such as the interact prompt (review finding 6; the fix is a quad with a clear shader).
    - At 1440p and 4K, about 25 or 10 entries fill the 1024² atlas.
    - One unreadable element is skipped, logged once and left on the flat HUD; it doesn't take the other bars down.
  - Check on hardware: the Perf line ends with `overhead read X ms/frame`. One-time log lines list the first labelled entry's cuts, its bar extent and every visible graphic in it (a graphic outside the cuts stays on the flat HUD), and whether a blood-type entry found its bar.
    - Binding an RT makes `SetViewProjectionMatrices` apply Unity's RT Y flip, matching HDRP's output (from disassembly; check with F5).
  - On the VR HUD only (`Bars.HudPanel`, default CutBars): during HudLayer's customRender, CharacterHUDsCanvas is moved to `targetDisplay` 7. The setter stores one byte, with no rebuild. `DrawUIOverlay` with a disabled camera on display 7 draws it alone into the HUD RT, which works because it is the bottom-most canvas (sort −2). `CopyTexture` from a transparent 1024×256 RT then blanks each cut's screen rect (bars, names, levels, icons) (`GetWorldCorners` on an overlay canvas is screen pixels from the bottom-left, the same origin as CopyTexture regions). Last, `DrawUIOverlay` on display 0 draws the rest on top.
    - `context.Submit()` runs while the canvas is moved, because the overlay draws happen at Submit. The display is restored in `finally`, and `GameHud.EnsureOverheadOnDesktop` restores it every LateUpdate as a safety net.
    - HideCanvas drops the whole layer, including names, levels and the prompt. Keep leaves the HUD unchanged. Any failure switches to Keep.
  - The game only makes entries for characters its 60° desktop camera sees, so characters seen only in the wider VR view get no bar.
- **Pose pairing (corrected 2026-10-20):** the earlier `pose delta 0.00°` "verification" compared a pose with itself, so it proved nothing. Also, reading GetLastPoses in LateUpdate races the render thread's WaitGetPoses and can render with last frame's pose. Now:
  - the head pose is read in `beginContextRendering`, after Unity waited for the render thread, forcing a sync via `GetNativeTexturePtr` if `SubmitBridge.PosesPending`;
  - the eyes are submitted as soon as both have rendered: `endCameraRendering` hooked through its backing field, since `add_endCameraRendering` is a stub;
  - the Perf line counts `poses paired/stale/other` exactly, on the render thread.
- **Desktop mirror (v0.10, `View.DesktopMirror`):** while VR runs in the world, `DesktopMirror` adds a `customRender` callback to the game camera's HDAdditionalCameraData. HDRP then skips its whole pipeline for that camera, as for the HUD helper camera, saving the third full render each frame. The callback draws the left eye into the window and then `DrawUIOverlay` for the UI. During that draw it moves CharacterHUDsCanvas and SCTCanvas to display 7: they are placed for the game camera's 60° view, so they wouldn't line up with the eye image, which already has the 3D bars.
  - `CopyTo` doesn't copy `customRender`, so the eyes are unaffected.
  - It detaches when VR stops, outside the world, while the virtual screen (F7) is wanted (that captures the desktop image), and for 4 frames around each F5 capture, so `_desktop.png` stays the game's own view.
  - It shows the *same* frame's eye: Unity renders cameras that have a targetTexture (the eyes, the HUD camera) in an earlier SRP call than the back-buffer camera.
  - The game camera's HDCamera.Update still runs, so its 1080p history buffers stay allocated: the mirror saves GPU time, not VRAM.
  - With EyeIsolation ≥ 2 the eyes drop custom passes (InventoryRoutingCustomPass, CharacterReadabilityPass) and custom post (LineOfSight darkening, CustomVignette). With the mirror on, those then show nowhere.
- **RenderScale 1.0 test (2026-10-20 18:11, 2500² per eye):** it looked very good at full resolution but was very laggy. It ran at 12-16 fps (GPU 55-80 ms); after the first F6 press it fell to 5-7 fps and never recovered. Each F6 press rebuilds both eye cameras: their RTs were reported lost and recreated, and HDRP allocates fresh 2500² per-camera buffers. Meanwhile 4.2 GB of the 8 GB VRAM was held by other processes (another GPU program), so VRAM was likely oversubscribed and paging.
- **v0.10 test (2026-10-20 ~19:30, RenderScale 1.0, other GPU program closed, desktop mirror on): 31-37 fps (GPU 24-31 ms), up to 46-47 fps, with every pose paired.**
- **Other GPU programs skew the VR performance numbers.** On 2026-10-20 another GPU-heavy program, left running by accident, used ~2.6-4.2 GB of VRAM and 28-96% CUDA during every VR test until the user closed it at ~18:40. Idle VRAM was then 1.75 GB of 8 GB. All perf numbers before then are pessimistic. Check `nvidia-smi` before judging VR performance.
- **3D captures** (F5) land in `BepInEx\VRisingVR_captures\` as `<stamp>_left/_right/_hud/_desktop.png` plus `_info.txt` with the full geometry. Read them with the Read tool to see what each eye renders.
- **HUD layer, verified on hardware 2026-10-20:**
  - Like the eyes, its RT is drawn by Unity, so it's bottom-up on D3D and needs flipped overlay bounds. The back-buffer capture (`ScreenCapture`) is top-down and needs none.
  - It needs a depth-stencil buffer (`depth: 24`), or UGUI Masks don't clip: the day/night indicator drew as a giant black square.
  - It's placed from the recenter pose, tilted to the game camera's pitch, so it frames the character.
- **Cursor:** the game uses an OS hardware cursor, which never appears in captures. SteamVR's overlay-cursor API showed nothing. `VrCursor` instead draws the game's own cursor image as a top-most screen-space UI element: `ProjectM.CursorController._CursorType` and `_CursorDatas` (Texture + Hotspot), in ProjectM.dll. The mirror and HUD captures then include it, and on the desktop it sits under the hardware cursor.
- **Don't use `Process.MainWindowHandle` to find the game window.** With the BepInEx console open it can return the console. Enumerate windows of class `UnityWndClass` owned by our PID instead.
- **Never log from Unity's render thread** (inside `SubmitBridge` callbacks). BepInEx's console writer isn't thread-safe, and an exception escaping the native callback kills the game. Hand errors to the main thread instead (`SubmitBridge.TakePendingError`).
- **Frame pacing:** while VR runs, vsync is off and `targetFrameRate` is -1, so `WaitGetPoses` paces the game. The game re-applies its own limit (`ProjectM.GraphicsSettings.SetFPSLimit`), so the mod re-asserts it every second. `QualitySettings.vSyncCount` has only a setter in this build, and `Application.runInBackground` only a getter.
- **Eye cameras** get custom HDRP frame settings:
  - off for comfort: motion blur, DoF, lens distortion, chromatic aberration, panini, film grain;
  - off when `PerformanceMode` is on: volumetrics, volumetric clouds, SSR, SSGI, contact shadows;
  - `AsymmetricProjection` on, since each eye uses an off-axis frustum.

  They also use SMAA instead of TAA, set `hasPersistentHistory`, and render into RTs with a 24-bit depth buffer. HDRP copies the scene depth into it for the depth-tested ground cursor.
- **Measured on the RTX 2070** at RenderScale 0.7 (2×1761²) plus the 1080p desktop. Steam Link presents at the app's rate; the headset does its own reprojection.
  - In-world before the comfort/perf frame settings: about 30 fps (GPU 20-40 ms).
  - In-world with PerformanceMode + SMAA + vsync off: 40-60 fps (GPU 12-24 ms).
  - Menu with black frames: 90 fps.
- **Verified on hardware: HDRP honours the script-set `Camera.projectionMatrix`.** The runtime check compared ours with `HDCamera.mainViewConstants.projMatrix` (m00 0.695, m02 -0.155, |m11| 0.689, |m12| 0.192, identical). The game camera is FOV 60, not physical.
- **Runtime-created textures must set `hideFlags = HideFlags.DontUnloadUnusedAsset`.** Otherwise the `UnloadUnusedAssets` that runs when `VRisingWorld` loads destroys them if nothing the engine tracks references them (e.g. eye RTs while the eyes are idle in menus). That showed up as `Compositor.Submit returned InvalidTexture` and a blank VR view until VR was restarted. `EnsureEyeTextures` also recreates lost RTs.
- Steam Frame raw projection (logged): left eye L=-1.661 R=1.216 T=-1.729 B=1.172. That's strongly asymmetric vertically. In OpenVR's raw values, B is the *upward* tangent. Valve's Unity XR plugin (Providers/Display/Display.cpp) uses top=vrB, bottom=vrT, which matches `VRMath.Projection`. IPD offset ±0.035 m, no canting.
- **Research (workflow wf_5787d8ae-73d), fact-check pending:**
  - All V Rising UI is UGUI Screen Space - Overlay: `HUDCanvas(Clone)` with 12 root canvases, and `MainMenuCanvas`. The `Canvas.worldCamera` setter is stripped.
  - Recommended HUD-in-VR approach: re-draw the overlay UI into a transparent RT with `ScriptableRenderContext.DrawUIOverlay` from an HDRP `customRender` callback on a dummy camera, then show it as a premultiplied OpenVR overlay.
  - Camera: an ECS `TopDownCamera` entity in world `Client_0`. `TopdownCameraState` holds yaw, pitch, zoom and the look-at root position; `CameraTarget.Target` is the local character; `CameraManager.TryGetCamera` returns the Unity camera.
  - Aim point: the ECS `ProjectM.CursorPosition`.
- Test headset: **Steam Frame streamed wirelessly over Steam Link**. It appears to SteamVR as a normal OpenVR HMD. The per-eye render target is large and streaming adds latency, so keep `Submit_TextureWithPose` (lets SteamVR and the headset reproject correctly) and keep RenderScale/perf in mind.

## Architecture (VRisingVR/)
- No Unity XR. The game ships without XR management, so the mod uses **OpenVR directly** (`OpenVR/openvr_api.cs` + `lib/openvr_api.dll`, v2.5.1).
- `VRRig` (an injected MonoBehaviour) leaves the game camera untouched, so gameplay, mouse aiming and the desktop view still work. Each LateUpdate it places two extra HDRP eye cameras at the game camera's position:
  `eye = origin + rigRot * (headPose * WorldScale)`. `rigRot` is the game camera's yaw only, unless FollowCameraPitch is on, which keeps the horizon level. Each eye renders to its own RenderTexture with an off-axis projection taken from `GetProjectionRaw`.
- `SubmitBridge` sends eye textures to the compositor **on Unity's render thread** through `GL.IssuePluginEvent` with a managed function pointer. D3D11 Submit uses the immediate context, so calling it from the main thread would race. Frame N is submitted at the start of frame N+1 using `Submit_TextureWithPose`, so reprojection uses the pose the frame was rendered with. `WaitGetPoses` runs on the render thread and paces the game to the headset.
- `[BepInProcess("VRising.exe")]` means the mod never loads on the dedicated server, and it changes no game state.

## Release packaging (1.0.0, 2026-10-20)
After the 22:27 session the user called the mod "effectively perfect" and asked for an easy way to deploy it.
- **Version:** `<Version>` in `VRisingVR.csproj` is the only version.
  - The `WritePluginVersion` target generates `obj\...\PluginVersion.g.cs`, and `Plugin.Version` uses it.
  - So the BepInPlugin attribute, the manifest and the zip names always agree.
- **Zips:** Release builds run `PackageMod` after the deploy (skip it with `-p:Package=false`; `dist/` is git-ignored).
  - `dist\VRisingVR-<version>.zip`: for Thunderstore, and for r2modman's Import local mod.
  - `dist\VRisingVR-<version>-manual.zip`: holds `BepInEx/plugins/VRisingVR/...`; extract it into the game folder.
  - Both carry README.md, CHANGELOG.md, LICENSE (MIT) and THIRD-PARTY-NOTICES.txt. OpenVR is BSD-3-Clause, which requires its notice in binary distributions.
- **Thunderstore rules.** The first zips were validated against these (2026-10-20):
  - `manifest.json`, `icon.png` (256×256 PNG) and `README.md` sit at the zip root.
  - Manifest keys: `name` (`[A-Za-z0-9_]`), `version_number` (Major.Minor.Patch), `website_url` (empty or a URL), `description` (at most 250 characters), `dependencies`.
  - The build stops on a description that's too long or contains `;`, `"` or `\`. The manifest's lines are an MSBuild item list, so a `;` would split them. Also avoid `*`, `?`, `%`, `$(` and `@(`.
  - No BOM: don't give WriteLinesToFile an `Encoding`, because "utf-8" adds one and Thunderstore's JSON parser rejects it.
- **Dependency:** `BepInEx-BepInExPack_V_Rising-1.733.2`, still the latest on Thunderstore on 2026-10-20 (published 2025-05-17).
  - V Rising categories: 1.0 Update, Client, Gloomrot Update, Mods, Oakveil Update, Server, Tools. This mod goes in **Client**.
- **r2modman** installs the root DLLs and other root files into `BepInEx\plugins\<Team>-VRisingVR\`.
  - `OpenVRLoader` loads `openvr_api.dll` from the plugin assembly's folder, so any folder name works.
  - BepInEx doesn't complain about the native DLL in plugins.
- **Icon:** `icon.png` at the repo root, drawn with PIL: a headset over an isometric board, in crimson. Any 256×256 PNG can replace it.
- **README** is written for players: installing, playing, keys, settings, tips, limits, troubleshooting. "Building from source" and the release steps come last.
  - Thunderstore renders it, so it has no relative links.
  - The old "Status" section is gone; the roadmap below is the status.
- **Uploading is the user's step**, from their Thunderstore team at https://thunderstore.io/package/create/. Uploads can't be edited, so every change needs a new version.
  - `ThunderstoreWebsite` is empty because the git repository isn't public.
- **1.0.1 (2026-10-21):** the user's tuned view settings became the defaults: WorldScale 14.05, HeightOffset 2.57, RenderScale 1, HUD 0.59 m / 0.71 m, Screen.Distance 3.1. The v1.0.0 commit was already on Forgejo, so the change shipped as a new version rather than a moved tag.

## Roadmap
1. ~~Stereo world view in the headset with head tracking~~ (v0.1, untested on hardware)
2. Draw the HUD in VR. Implemented in v0.4 by `HudLayer`, not yet confirmed on hardware:
   - A hidden helper camera has an HDRP `customRender` callback (`add_customRender` with an implicitly converted Il2Cpp `Action<ScriptableRenderContext, HDCamera>`). It clears a screen-sized RT to transparent, then calls `ScriptableRenderContext.DrawUIOverlay`.
   - The result is shown as a premultiplied SteamVR overlay over the eye view. `OverlayPanel` holds the shared overlay code.
   - Earlier v0.2 notes: `VirtualScreen` mirrors the game window (ScreenCapture.CaptureScreenshotIntoRenderTexture → SteamVR overlay) in menus, and in-world via F7. The game uses a hardware cursor, so a separate cursor overlay is driven from Win32 GetCursorPos. Menu detection: scene `UIEntryPoint` = main menu, `VRisingWorld` = in-game. Next step: show only the HUD over the 3D view.
3. ~~Show the mouse cursor and ground reticle in VR~~ (v0.7; the reticle's ECS read was fixed in v0.9, untested)
4. Anchor on the player character (pivot point) rather than the camera position, plus optional smoothing. The HUD already does this (v0.6); the eyes still follow the Unity camera, including screen shake (option: `GameCameraState.CameraPos`)
4b. ~~Health bars over the characters in 3D~~ (v0.9, working). Phase 2: ~~names, levels and icons~~ (v0.12, working); combat text and the interact prompt as 3D elements
5. An in-game settings panel in the Options menu (currently BepInEx config + hotkeys)
6. ~~Performance: option to skip desktop rendering and show a mirror of the left eye instead~~ (v0.10, `View.DesktopMirror`)
7. ~~Release packaging~~ (1.0.0): `dotnet build -c Release` writes the Thunderstore and manual zips to `dist\` (see Release packaging)
