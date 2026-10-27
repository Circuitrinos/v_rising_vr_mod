# VRisingVR

Play V Rising in VR like a 3D board game. The game's own top-down camera is shown in stereo 3D in your headset, so the world sits in front of you like a living tabletop. You can lean in and look around it with your head, and the game's camera rotation and zoom still work. You keep playing as usual, with a gamepad or with mouse and keyboard.

- The game's HUD, menus and map float in front of you as a transparent layer.
- Health bars, names and levels hang over the characters in 3D.
- The aim cursor lies on the ground in 3D, exactly where your attacks go.
- An in-game settings panel (F4) adjusts the view while you play.

**Gamepad or mouse and keyboard?** VRisingVR works well with a gamepad: you never need to see your hands. If you play with mouse and keyboard on a Steam Frame, or on a Meta Quest 3 streaming with ALVR, [VRisingVR Passthrough](https://thunderstore.io/c/v-rising/p/Circuitrinos/VRisingVR_Passthrough/) is the same mod plus a window onto your desk: hold a mouse button and you see your real keyboard, mouse and hands through the headset's passthrough. Install one or the other, not both. On a Quest 3 with Virtual Desktop, its own Desk Passthrough may show your desk with plain VRisingVR, but that isn't tested.

The mod is client-side only. It doesn't change anything on the server, so there's nothing to install there. Stunlock Studios doesn't allow mods on its official servers, so play with it in single player, on your own server, or on a community server that allows client mods.

## Requirements

- V Rising 1.1.13 on Windows (Steam), running on Direct3D 11, the game's default
- [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) 1.733.2 (mod managers install it for you)
- SteamVR and a headset that works with it. Tested with a Steam Frame streaming wirelessly through Steam Link, and a Meta Quest 3.
- A strong graphics card: the game renders the world once per eye, by default at SteamVR's full recommended resolution. On an RTX 2070 that runs at roughly 25-45 fps depending on the scene, and `View.RenderScale` 0.7 gets roughly 40-60.

## Installing

You don't need the source code or any developer tools, only the mod itself. VRisingVR runs on BepInEx, the mod loader most V Rising mods use, and a mod manager installs that for you.

### With a mod manager (recommended)

A mod manager installs mods into a separate profile, brings in BepInEx, keeps your mods up to date, and leaves the game's own files alone. Use either of these:

- **Thunderstore Mod Manager** is Thunderstore's own app. Get it with the **Get Manager** button on [thunderstore.io](https://thunderstore.io/). It runs on Overwolf and shows ads.
- **r2modman** is free, open source and has no ads. Download `r2modman-Setup-<version>.exe` from its [latest release](https://github.com/ebkr/r2modmanPlus/releases/latest).

Both are built on the same code, but a few buttons have different names. This guide uses Thunderstore Mod Manager's:

| Thunderstore Mod Manager | r2modman | What it does |
| --- | --- | --- |
| Get mods | Online | Find and download mods |
| My mods | Installed | Your installed mods |
| Modded | Start modded | Start the game with mods |
| Edit config | Config editor | Change mod settings |

1. Open the mod manager. Search for **V Rising** and click **Select game**. Then click the **Default** profile and **Select profile** (r2modman: **Select**).
2. Click **Get mods** and search for **VRisingVR**. Click the mod, click **Download**, then **Download with dependencies**. This installs BepInExPack_V_Rising as well.
3. Click **Modded** to start the game. The first start needs an internet connection and takes a few minutes while BepInEx prepares the game, and a BepInEx console window opens beside it. Later starts are quick.

Always start the game with **Modded**. Starting it from Steam runs the game without mods.

You can also install from the mod's page on Thunderstore: with the mod manager open on V Rising and your profile, click **Install with App**.

**Installing a zip file.** If you have the mod's zip, for example from the **Download** button on its Thunderstore page, you can import it:

1. Install **BepInExPack_V_Rising** from **Get mods** first. Imported mods don't bring their dependencies with them.
2. Go to **Settings > Profile > Import local mod**, click **Import local mod**, then **Select file**.
3. Pick the zip exactly as you downloaded it, named like `Circuitrinos-VRisingVR-1.0.2.zip`. Don't rename or unzip it: the mod manager reads the author from the file name, a folder can't be imported, and a zip you make yourself won't work.
4. Check that the dialog shows VRisingVR, Circuitrinos and the mod's version number. If it says 0.0.0, it's the wrong file. Click **Import local mod**.

To update, import the new version's zip the same way; it replaces the old one.

### By hand

1. Find your game folder: in Steam, right-click V Rising and choose **Manage > Browse local files**. It's the folder with `VRising.exe` in it.
2. Install BepInEx. Download [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) with the **Download** button and extract it into a temporary folder, not the game folder. Then move everything inside its `BepInExPack_V_Rising` folder into the game folder.
3. Start the game once and quit. A BepInEx console window should open, and this first start takes a few minutes.
4. In the game folder, open `BepInEx\plugins`, create a folder named `VRisingVR`, and extract the mod's zip into it. `VRisingVR.dll` and `openvr_api.dll` must end up together in `BepInEx\plugins\VRisingVR`. (If you have `VRisingVR-<version>-manual.zip`, extract that straight into the game folder instead.)

The game now loads the mod whenever it starts, including from Steam.

### Updating and uninstalling

- **With a mod manager:** **My mods** marks mods that have an update. Click the mod, then **Update**. To remove it, click it and choose **Uninstall**.
- **By hand:** replace the files in `BepInEx\plugins\VRisingVR` with the new version's, or delete that folder to uninstall.

Don't mix the two ways. If you installed BepInEx by hand and later switch to a mod manager, delete the `BepInEx` folder from the game folder, or starting the game from Steam keeps loading the old copy.

## Playing

Start the game with your headset connected (with **Modded** if you use a mod manager). VR starts by itself, and SteamVR starts too if it isn't running yet.

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

The F4 panel covers the settings you'll want to adjust while playing. All of them, including the keys, are in `BepInEx\config\vrisingvr.tabletop.cfg`, which appears after the first start with the mod. In a mod manager, open it with **Edit config** (r2modman: **Config editor**); changes there apply the next time the game starts. The most useful:

- `View.WorldScale`: how small the world looks. Higher is more like a tabletop.
- `View.HeightOffset`: raises your viewpoint above the game camera.
- `View.RenderScale`: per-eye resolution as a multiple of SteamVR's recommendation (default 1). It's applied when VR starts.
- `View.PerformanceMode`: skips the most expensive effects in the eyes (volumetric fog and clouds, screen-space reflections and GI, contact shadows). On by default.
- `HUD.Distance` and `HUD.Width`: where the HUD layer floats and how big it is.
- `Bars.Enabled` and `Bars.WidthMeters`: the 3D health bars and their size.
- `Cursor.GroundCursorSize` and `Cursor.GroundCursorHiddenAlpha`: the ground cursor's size, and how strongly it shows through things in front of it.

## Performance tips

- Lower `View.RenderScale` first, for example to 0.7. That's the biggest lever.
- Lower V Rising's own shadow and ambient occlusion quality. Each eye renders with the game's quality settings.
- Keep the game's resolution at your monitor's. The VR HUD is drawn at the window's resolution, so lowering it only blurs the HUD.
- Close other programs that use the graphics card.

## Known limitations

- The game only makes health bars for characters its normal desktop view would show, so characters at the far edges of the wider VR view have none.
- Where two characters' bars overlap on screen, they're drawn in plain colours instead of the game's own graphics.
- No motion controller support. The headset is your view; you play with a gamepad or with mouse and keyboard.
- Built for V Rising 1.1.13. A game update can break it until the mod is updated.

## Troubleshooting

Everything the mod writes goes into the `BepInEx` folder: the log `LogOutput.log`, the settings in `config`, and F5 captures in `VRisingVR_captures`. If you installed by hand, that's the `BepInEx` folder in the game folder. A mod manager keeps it in its profile folder instead. Paste the path into File Explorer's address bar, and replace `Default` if your profile has another name:

- Thunderstore Mod Manager: `%APPDATA%\Thunderstore Mod Manager\DataFolder\VRising\profiles\Default\BepInEx`
- r2modman: `%APPDATA%\r2modmanPlus-local\VRising\profiles\Default\BepInEx`

Common problems:

- **The game starts without mods** (no BepInEx console window): start it with **Modded** in the mod manager, not from Steam, and check that BepInExPack_V_Rising is in **My mods**. If you imported a zip, the import dialog should have shown the mod's version number, not 0.0.0.
- **The BepInEx console shows `System.AccessViolationException`:** your antivirus is probably blocking BepInEx. Allow it in your security software.
- **Nothing in the headset:** make sure SteamVR is installed and sees your headset, then press F8 in the game. The log says why VR didn't start (look for lines with `VRisingVR`).
- **Blurry picture on a Quest 3 with ALVR:** in ALVR's **Settings > Video**, set **Bitrate** to **Adaptive** with a minimum of 100 Mbps (a constant 30 Mbps is blurry), set **Emulated headset view resolution** and **Transcoding view resolution** to **Scale 1.4** (lower both if it's too slow), and turn off **Foveated encoding** and **Clientside foveation**. In SteamVR's **Settings > Video**, set **Render Resolution** to **Custom**, 100%.
- **Low frame rate:** see the performance tips above. The log prints a `Perf:` line every 5 seconds with the frame rate and GPU time.
- **Something looks wrong:** press F5 for a 3D capture and include it, along with the log, when you report the problem (see below).

## Feedback and bug reports

Report bugs and suggest improvements on [GitHub Issues](https://github.com/Circuitrinos/v_rising_vr_mod/issues). You need a free GitHub account. For a bug, attach your `LogOutput.log`, plus an F5 capture if it's something you can see; Troubleshooting above says where to find them.

## Building from source

The source code is on [GitHub](https://github.com/Circuitrinos/v_rising_vr_mod). You need the .NET SDK (6 or newer) and the game with BepInExPack_V_Rising installed and started once, so that `BepInEx\interop` exists.

```
cd VRisingVR
dotnet build -c Release
```

The build copies `VRisingVR.dll` and `openvr_api.dll` into `<GameDir>\BepInEx\plugins\VRisingVR\` and writes the release zips to `dist\`:

- `Circuitrinos-VRisingVR-<version>.zip` is the Thunderstore package. It also works with r2modman's **Import local mod**.
- `VRisingVR-<version>-manual.zip` is for installing by hand.

Options:

- `GameDir` defaults to `E:\Steam\steamapps\common\VRising`. To use another path, run `dotnet build -c Release -p:GameDir="X:\...\VRising"`.
- `-p:Deploy=false` skips the copy into the game.
- If BepInEx came from a mod manager, the game folder has none. Point the build at the profile's with `-p:BepInExDir="<profile folder>\BepInEx"` (the paths are under Troubleshooting) and add `-p:Deploy=false`, so the build doesn't put a second copy of the mod into the profile.
- `-p:Package=false` skips the zips.
- `ThunderstoreTeam` in `VRisingVR/VRisingVR.csproj` (`Circuitrinos`) is the Thunderstore team that uploads the package. It starts the Thunderstore zip's name, which is where r2modman's Import local mod gets the author from.

**Releasing a new version:**

1. Raise `<Version>` in `VRisingVR/VRisingVR.csproj` (Major.Minor.Patch). The plugin, the manifest and the zip names all use it.
2. Add a section to `CHANGELOG.md`.
3. Build.
4. Upload `dist\Circuitrinos-VRisingVR-<version>.zip` at <https://thunderstore.io/package/create/>. Choose the team named in `ThunderstoreTeam` and the V Rising community, then pick the **Client** and **Oakveil Update** categories, plus **AI Generated** if the community has one. Set **Contains NSFW content** to No.

Keep the AI disclosure section below and the `AssemblyMetadata` entries in the csproj. Thunderstore requires both for mods made with AI.

Thunderstore uploads can't be edited afterwards, so every change needs a new version.

## AI disclosure

VRisingVR was made with generative AI. Its code, documentation and icon were created by Claude, Anthropic's AI model, working in Claude Code. Circuitrinos directed the work and play-tested the mod in VR on real hardware. The plugin also records this in its assembly metadata (`AI_Assisted_Creation` and `AI_Model_Vendor`).

## License

MIT, see `LICENSE`. VRisingVR includes the OpenVR SDK, under Valve's BSD-3-Clause license; see `THIRD-PARTY-NOTICES.txt`. VRisingVR is not affiliated with Stunlock Studios or Valve.
