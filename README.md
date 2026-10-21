# VRisingVR

Play V Rising in VR like a 3D board game. The game's own top-down camera is shown in stereo 3D in your headset, so the world sits in front of you like a living tabletop. You can lean in and look around it with your head, and the game's camera rotation and zoom still work. You keep playing with mouse and keyboard as usual.

- The game's HUD, menus and map float in front of you as a transparent layer.
- Health bars, names and levels hang over the characters in 3D.
- The aim cursor lies on the ground in 3D, exactly where your attacks go.
- An in-game settings panel (F4) adjusts the view while you play.

The mod is client-side only. It doesn't change anything on the server, so there's nothing to install there.

## Requirements

- V Rising 1.1.13 on Windows (Steam), running on Direct3D 11, the game's default
- [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) 1.733.2 (mod managers install it for you)
- SteamVR and a headset that works with it. Tested with a Steam Frame streaming wirelessly through Steam Link.
- A strong graphics card: the game renders the world once per eye. On an RTX 2070, expect roughly 40-60 fps at the default settings, less in busy areas.

## Installing

**With a mod manager (recommended).** In [r2modman](https://thunderstore.io/c/v-rising/p/ebkr/r2modman/) or the Thunderstore Mod Manager, choose V Rising, install VRisingVR, and start the game with **Start modded**. BepInExPack_V_Rising is installed with it. To install a downloaded `VRisingVR-<version>.zip` instead, use **Settings > Import local mod** in r2modman.

**By hand.**

1. Install BepInExPack_V_Rising: extract its zip into a temporary folder (not the game folder), then move everything inside its `BepInExPack_V_Rising` folder into the game folder (`<Steam>\steamapps\common\VRising`, where `VRising.exe` is).
2. Start the game once and quit. The first start takes a few minutes while BepInEx prepares the game.
3. Extract `VRisingVR-<version>-manual.zip` into the game folder. It creates `BepInEx\plugins\VRisingVR`.

To update, replace the files in `BepInEx\plugins\VRisingVR`. To uninstall, delete that folder.

## Playing

Start the game with your headset connected. VR starts by itself, and SteamVR starts too if it isn't running yet.

- **Main menu:** a virtual screen mirroring the game window floats in front of you, with a dot where your mouse is.
- **In the world:** you see the tabletop view. Press **F9** to re-center whenever you change how you sit. The HUD stays centered on your character's feet, and moves closer when you zoom in so it never sinks into the ground.
- **Cursor:** in gameplay the crosshair lies on the ground in 3D, at the point the game aims at. Your character and anything else in front of it hide it, but the hidden parts still show faintly, so it never vanishes behind an enemy. In menus (inventory, map, pause) the cursor moves to the HUD layer.
- **Health bars and names:** each character's overhead bars, name, level and icons float above them in 3D, looking exactly like the game's own. The interact prompt and the combat numbers stay on the HUD layer.
- **Desktop window:** while you're in VR, the window on your monitor shows your left eye, which saves a whole render each frame. Turn `View.DesktopMirror` off to see the normal game view there instead.

Hotkeys only work while the game window has focus. A short message in the headset confirms each one.

| Key | Action |
| --- | --- |
| F4 | Open/close the VR settings panel: HUD distance, width, tilt and ground margin, cursor and health bar size, world scale, near clip, stereo strength, view height, menu screen distance |
| F7 | Show/hide the virtual screen while in the world |
| F8 | Stop/start VR |
| F9 | Re-center your view |
| F10 / F11 | Make the world look bigger / smaller (WorldScale) |
| F5 | Save a 3D capture (both eyes, the HUD and the desktop) to `BepInEx\VRisingVR_captures`, for bug reports |
| F6 | Diagnostics: cycle how much of the game's rendering the eyes use (0 is normal). Only useful when tracking down a rendering problem. |

## Settings

The F4 panel covers the settings you'll want to adjust while playing. All of them, including the keys, are in `BepInEx\config\vrisingvr.tabletop.cfg` (in r2modman: **Config editor**). The most useful:

- `View.WorldScale`: how small the world looks. Higher is more like a tabletop.
- `View.HeightOffset`: raises your viewpoint above the game camera.
- `View.RenderScale`: per-eye resolution as a multiple of SteamVR's recommendation (default 0.7). It's applied when VR starts.
- `View.PerformanceMode`: skips the most expensive effects in the eyes (volumetric fog and clouds, screen-space reflections and GI, contact shadows). On by default.
- `HUD.Distance` and `HUD.Width`: where the HUD layer floats and how big it is.
- `Bars.Enabled` and `Bars.WidthMeters`: the 3D health bars and their size.
- `Cursor.GroundCursorSize` and `Cursor.GroundCursorHiddenAlpha`: the ground cursor's size, and how strongly it shows through things in front of it.

## Performance tips

- Lower `View.RenderScale` first. That's the biggest lever.
- Lower V Rising's own shadow and ambient occlusion quality. Each eye renders with the game's quality settings.
- Keep the game's resolution at your monitor's. The VR HUD is drawn at the window's resolution, so lowering it only blurs the HUD.
- Close other programs that use the graphics card.

## Known limitations

- The game only makes health bars for characters its normal desktop view would show, so characters at the far edges of the wider VR view have none.
- Where two characters' bars overlap on screen, they're drawn in plain colours instead of the game's own graphics.
- No motion controller support. The headset is your view; you play with mouse and keyboard.
- Built for V Rising 1.1.13. A game update can break it until the mod is updated.

## Troubleshooting

- **Nothing in the headset:** make sure SteamVR is installed and sees your headset, then press F8 in the game. The log, `BepInEx\LogOutput.log`, says why VR didn't start (look for lines with `VRisingVR`).
- **Low frame rate:** see the performance tips above. The log prints a `Perf:` line every 5 seconds with the frame rate and GPU time.
- **Something looks wrong:** press F5 for a 3D capture and include it, along with `BepInEx\LogOutput.log`, when you report the problem.

## Building from source

You need the .NET SDK (6 or newer) and the game with BepInExPack_V_Rising installed and started once, so that `BepInEx\interop` exists.

```
cd VRisingVR
dotnet build -c Release
```

The build copies `VRisingVR.dll` and `openvr_api.dll` into `<GameDir>\BepInEx\plugins\VRisingVR\` and writes the release zips to `dist\`:

- `VRisingVR-<version>.zip` is the Thunderstore package. It also works with r2modman's **Import local mod**.
- `VRisingVR-<version>-manual.zip` is for installing by hand.

Options:

- `GameDir` defaults to `E:\Steam\steamapps\common\VRising`. To use another path, run `dotnet build -c Release -p:GameDir="X:\...\VRising"`.
- `-p:Deploy=false` skips the copy into the game.
- `-p:Package=false` skips the zips.

**Releasing a new version:**

1. Raise `<Version>` in `VRisingVR/VRisingVR.csproj` (Major.Minor.Patch). The plugin, the manifest and the zip names all use it.
2. Add a section to `CHANGELOG.md`.
3. Build.
4. Upload `dist\VRisingVR-<version>.zip` at <https://thunderstore.io/package/create/>: choose your team, the V Rising community and the **Client** category.

Thunderstore uploads can't be edited afterwards, so every change needs a new version.

## License

MIT, see `LICENSE`. VRisingVR includes the OpenVR SDK, under Valve's BSD-3-Clause license; see `THIRD-PARTY-NOTICES.txt`. VRisingVR is not affiliated with Stunlock Studios or Valve.
