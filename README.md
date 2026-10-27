# VRisingVR Passthrough

Play V Rising in VR like a 3D board game, and see your real keyboard and mouse whenever you need them. This is the passthrough edition of [VRisingVR](https://thunderstore.io/c/v-rising/p/Circuitrinos/VRisingVR/) for the **Steam Frame** and the **Meta Quest 3**: the same mod, plus a window onto your desk. Hold your mouse's rear side button and a window opens in the game where your keyboard and mouse are, showing them and your hands through the headset's passthrough. Your headset has to turn the window's green into passthrough: a community tool does that on the Steam Frame, and the ALVR streaming app on a Quest 3 (see "Seeing your desk"). If you play with a gamepad, or your headset can't do this, install VRisingVR instead.

The game's own top-down camera is shown in stereo 3D in your headset, so the world sits in front of you like a living tabletop. You can lean in and look around it with your head, and the game's camera rotation and zoom still work. You keep playing with mouse and keyboard as usual.

- Hold your mouse's rear side button to see your desk through a window in the game (Steam Frame). Hold G to move and turn the window with the mouse.
- The game's HUD, menus and map float in front of you as a transparent layer.
- Health bars, names and levels hang over the characters in 3D.
- The aim cursor lies on the ground in 3D, exactly where your attacks go.
- An in-game settings panel (F4) adjusts the view while you play.

The mod is client-side only. It doesn't change anything on the server, so there's nothing to install there. Stunlock Studios doesn't allow mods on its official servers, so play with it in single player, on your own server, or on a community server that allows client mods.

## Requirements

- V Rising 1.1.13 on Windows (Steam), running on Direct3D 11, the game's default
- [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) 1.733.2 (mod managers install it for you)
- SteamVR and a headset that works with it. Tested with a Steam Frame streaming wirelessly through Steam Link, and a Meta Quest 3 streaming with ALVR.
- For the desk window: a Steam Frame streaming from your PC through SteamVR, with the community chroma-key tool installed on it, or a Meta Quest 3 streaming with ALVR, with its chroma key on (see "Seeing your desk"). With other headsets or streaming apps the rest of the mod works, but the window is just a green rectangle.
- A strong graphics card: the game renders the world once per eye, by default at SteamVR's full recommended resolution. On an RTX 2070 that runs at roughly 25-45 fps depending on the scene, and `View.RenderScale` 0.7 gets roughly 40-60.

## Installing

You don't need the source code or any developer tools, only the mod itself. VRisingVR runs on BepInEx, the mod loader most V Rising mods use, and a mod manager installs that for you.

**It replaces VRisingVR.** If you have VRisingVR installed, uninstall it first, or turn it off in **My mods**. Both are the same plugin, so if both are installed BepInEx runs only the one with the higher version number. They share the settings file, so your view settings carry over.

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
2. Click **Get mods** and search for **VRisingVR Passthrough**. Click the mod, click **Download**, then **Download with dependencies**. This installs BepInExPack_V_Rising as well.
3. Click **Modded** to start the game. The first start needs an internet connection and takes a few minutes while BepInEx prepares the game, and a BepInEx console window opens beside it. Later starts are quick.

Always start the game with **Modded**. Starting it from Steam runs the game without mods.

You can also install from the mod's page on Thunderstore: with the mod manager open on V Rising and your profile, click **Install with App**.

**Installing a zip file.** If you have the mod's zip, for example from the **Download** button on its Thunderstore page, you can import it:

1. Install **BepInExPack_V_Rising** from **Get mods** first. Imported mods don't bring their dependencies with them.
2. Go to **Settings > Profile > Import local mod**, click **Import local mod**, then **Select file**.
3. Pick the zip exactly as you downloaded it, named like `Circuitrinos-VRisingVR_Passthrough-1.1.0.zip`. Don't rename or unzip it: the mod manager reads the author from the file name, a folder can't be imported, and a zip you make yourself won't work.
4. Check that the dialog shows VRisingVR_Passthrough, Circuitrinos and the mod's version number. If it says 0.0.0, it's the wrong file. Click **Import local mod**.

To update, import the new version's zip the same way; it replaces the old one.

### By hand

1. Find your game folder: in Steam, right-click V Rising and choose **Manage > Browse local files**. It's the folder with `VRising.exe` in it.
2. Install BepInEx. Download [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) with the **Download** button and extract it into a temporary folder, not the game folder. Then move everything inside its `BepInExPack_V_Rising` folder into the game folder.
3. Start the game once and quit. A BepInEx console window should open, and this first start takes a few minutes.
4. In the game folder, open `BepInEx\plugins`, create a folder named `VRisingVR`, and extract the mod's zip into it. `VRisingVR.dll` and `openvr_api.dll` must end up together in `BepInEx\plugins\VRisingVR`, replacing VRisingVR's if you had it. (If you have `VRisingVR_Passthrough-<version>-manual.zip`, extract that straight into the game folder instead.)

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

Hotkeys only work while the game window has focus. Most of them confirm with a short message in the headset.

| Key | Action |
| --- | --- |
| Rear mouse side button | Hold to see your desk (see "Seeing your desk"). |
| G | Hold to see your desk: moving the mouse slides the window, the scroll wheel turns it. |
| F4 | Open/close the VR settings panel: HUD distance, width, tilt and ground margin, cursor and health bar size, world scale, near clip, stereo strength, view height, menu screen distance, and the desk window's key, position and size |
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
- `DeskWindow.Mode` and `DeskWindow.KeyColor`: when the desk window shows, and its colour (see "Seeing your desk").

## Performance tips

- Lower `View.RenderScale` first, for example to 0.7. That's the biggest lever.
- Lower V Rising's own shadow and ambient occlusion quality. Each eye renders with the game's quality settings.
- Keep the game's resolution at your monitor's. The VR HUD is drawn at the window's resolution, so lowering it only blurs the HUD.
- Close other programs that use the graphics card.

## Seeing your desk

SteamVR doesn't let PC games use a headset's cameras. Instead, the mod draws a pure green rectangle into the picture, lying where your keyboard and mouse are, and the headset shows its own passthrough wherever the picture is that green. On the Steam Frame a community tool, [steam-frame-chroma-key](https://github.com/spyjacob/steam-frame-chroma-key), does that; on a Quest 3, the ALVR streaming app does. So the rectangle becomes a window onto your desk, with your real keyboard, mouse and hands in it. The camera image never leaves the headset.

**Setting up a Steam Frame (once).** The tool is unofficial and not part of this mod; its README has the details.

1. On the headset, turn on Developer Mode (**Steam Settings > System > Enable Developer Mode**), set a password under **Developer > Set User Password**, and note the headset's IP address from its Wi-Fi settings.
2. On your PC, open PowerShell and log in to the headset with `ssh steamos@<headset-ip>`.
3. Build and install the tool. The `git checkout` line picks the version this edition was tested with (October 2026, SteamOS 0.4.5 build 20261007); leave it out to use the newest.
   ```
   git clone https://github.com/spyjacob/steam-frame-chroma-key.git ~/mr-chroma
   cd ~/mr-chroma
   git checkout 535c5f224768f85d7f011fce9e1cf03e0ea802f8
   ./build.sh && ./install.sh
   touch ~/.config/mrchroma/enable-compositor
   ~/.local/share/mrchroma/mrctl set-bool camera forceSceneAlphaBlending 1
   systemctl --user restart steamvr
   ```
4. Put the headset on so SteamVR wakes up, start streaming from your PC, and log in again. First check that the tool works:
   ```
   cd ~/mr-chroma
   ./mrkey.sh rect
   ```
   In the headset you should now see your room through a rectangle in the middle of each eye. If you don't, the tool isn't loaded: check that every command in step 3 ran without errors, run `systemctl --user restart steamvr`, and try again.
5. Then set the key and install the tool's panel for the SteamVR dashboard:
   ```
   ./mrkey.sh chroma 0 1 0 0.12 0.06 0.5
   toggle/install_toggle.sh
   ```
   The `0.12` and `0.06` are a tighter key than the tool suggests: only colours very close to pure green become passthrough. With its looser default, bright greens in the game, such as sunlit grass or green spell effects, would show your room too. You can change both later with the **Chroma Key** panel's **Similarity** and **Edge softness** sliders in the SteamVR dashboard.

You don't need the tool's PC programs. After a SteamOS update on the headset, turn keying off in the panel and run the `mrkey.sh` line again; if it reports "unexpected shader layout", leave keying off until the tool is updated. To remove the tool, run `./uninstall.sh` in `~/mr-chroma` while SteamVR is running.

**Setting up a Meta Quest 3 (once).** Tested with ALVR 20.14, a free app that streams SteamVR to the Quest and can turn a colour into passthrough.

1. Install ALVR. On your PC, download `alvr_launcher_windows.zip` from [ALVR's releases](https://github.com/alvr-org/ALVR/releases), extract it, run `ALVR Launcher.exe`, add and install a version, click **Launch** and follow the setup wizard. Get the ALVR app on the Quest from the Meta store, or with the launcher's **Install APK**.
2. Open ALVR on the Quest, then click **Trust** next to it in the **Devices** tab on your PC.
3. In ALVR's **Settings > Video**:
   - **Passthrough**: on, with the mode **RGB chroma key**. Its defaults (green 0, 255, 0, distance threshold 85, feathering 0.05) match the mod's window.
   - **Bitrate**: **Adaptive**, with a minimum of **100 Mbps**. At a constant 30 Mbps the picture is blurry.
   - **Emulated headset view resolution** and **Transcoding view resolution**: **Scale 1.4**, for sharpness close to the Steam Frame's (about 32-36 fps on an RTX 2070). Lower both if it's too slow.
   - **Foveated encoding** and **Clientside foveation**: off. The Quest 3 has no eye tracking, so they keep only the middle of the view sharp, and in the tabletop view you look down at the board with your eyes, below the middle.
4. In SteamVR's **Settings > Video**, set **Render Resolution** to **Custom**, 100%.
5. Restart SteamVR with the button at the bottom right of the ALVR window.

Connect your PC to the router with a cable, and put the Quest on the router's 5 GHz or 6 GHz Wi-Fi. ALVR's **Statistics** tab shows the bitrate you get.

**Not tested:** Virtual Desktop has a chroma-key **VR Passthrough** option (in its Streaming tab) that should turn the window into passthrough the same way, set to green (0, 255, 0). It also has its own **Desk Passthrough**, which shows your desk without this edition. Steam Link and Air Link or Quest Link can't turn a colour into passthrough, so with them the window stays green.

**Using it.** Hold your mouse's rear side button and look at your desk. The HUD steps aside while you hold it. The window lies flat on the desk, at first 45 cm in front of and below where your eyes were when you last re-centered (F9, and whenever you put the headset on). To line it up with your keyboard and mouse:

- Hold **G** and move the mouse. The window slides across the desk, right and left, forward and back. Scroll the wheel to turn it. It stays where you let go of G, and from then on it stays put when you re-center or take the headset off, until VR restarts. The game still gets the mouse meanwhile: your aim follows it (so don't click), and the wheel also zooms the camera, so zoom back afterwards.
- Or open the **F4** panel, where the window stays visible, and adjust **Desk window ahead** and **right**, **Desk below your eyes** (your desk's height under your eyes), and the window's **width**, **depth** and **turn**. The panel covers the middle of the window in the world; in the main menu it's out of the way.

The window also works in menus and loading screens. On your monitor it shows as a green rectangle.

**Why hold a key?** In the tabletop view your character is in about the same direction as your keyboard, so a window that opened by itself whenever you looked down would cover the board while you play. `DeskWindow.Mode` changes this:

- **Hold** (default): while you hold the side button.
- **Toggle**: the side button turns it on and off.
- **LookDown**: while your head points further down than `DeskWindow.LookDownAngle` (50° below the horizon by default). The side button turns this off and on. It only works if you tilt your head further down for your keyboard than for your character; the log's `Perf:` line shows where your head points with the window hidden and shown.
- **Always**: all the time. The side button turns it off and on.

G shows the window while you hold it in every mode except Off.
- **Off**.

The window is green by default. If you pick another colour in the headset's Chroma Key panel, set `DeskWindow.KeyColor` to the same one (Green, Blue, Magenta, Red or Cyan).

**The keys.** By default your mouse's rear side button (`MouseBack`) shows the window, and **G** shows it and moves it. The side button is the easiest to hold while you move, since your left hand stays on WASD. V Rising also uses that button, as the second binding of **Interrupt Ability Cast** (the game calls it Mouse 3), so clear it once: in the game's settings, open the key bindings, click that action's second binding and press Backspace. Its main key stays. Don't use the front side button, which the game uses for Auto Run. G is a keyboard key V Rising doesn't use, next to F; without side buttons, hold G (and keep the mouse still) to see your desk.

To pick other keys, open the **F4** panel, click one of the two key rows at the top and press the key or mouse button (Esc cancels). They're saved as `DeskWindow.Key` and `DeskWindow.MoveKey`, which can also list several keys separated by commas. `DeskWindow.MoveSpeed` sets how far the window moves with the mouse, and `DeskWindow.RotateStep` how far one wheel notch turns it (negative turns it the other way).

## Known limitations

- The game only makes health bars for characters its normal desktop view would show, so characters at the far edges of the wider VR view have none.
- Where two characters' bars overlap on screen, they're drawn in plain colours instead of the game's own graphics.
- No motion controller support. The headset is your view; you play with mouse and keyboard.
- Built for V Rising 1.1.13. A game update can break it until the mod is updated.
- On the Steam Frame, the desk window depends on the unofficial chroma-key tool, which reaches into SteamVR's internals on the headset. A SteamOS update can stop it working until the tool is updated.
- Anything in the game close enough to the key colour also shows your room. With the tighter key above this is rare.

## Troubleshooting

Everything the mod writes goes into the `BepInEx` folder: the log `LogOutput.log`, the settings in `config`, and F5 captures in `VRisingVR_captures`. If you installed by hand, that's the `BepInEx` folder in the game folder. A mod manager keeps it in its profile folder instead. Paste the path into File Explorer's address bar, and replace `Default` if your profile has another name:

- Thunderstore Mod Manager: `%APPDATA%\Thunderstore Mod Manager\DataFolder\VRising\profiles\Default\BepInEx`
- r2modman: `%APPDATA%\r2modmanPlus-local\VRising\profiles\Default\BepInEx`

Common problems:

- **The game starts without mods** (no BepInEx console window): start it with **Modded** in the mod manager, not from Steam, and check that BepInExPack_V_Rising is in **My mods**. If you imported a zip, the import dialog should have shown the mod's version number, not 0.0.0.
- **The BepInEx console shows `System.AccessViolationException`:** your antivirus is probably blocking BepInEx. Allow it in your security software.
- **Nothing in the headset:** make sure SteamVR is installed and sees your headset, then press F8 in the game. The log says why VR didn't start (look for lines with `VRisingVR`).
- **Low frame rate:** see the performance tips above. The log prints a `Perf:` line every 5 seconds with the frame rate and GPU time.
- **The desk window is a green rectangle:** the headset isn't turning it into passthrough. On a Steam Frame, check that the **Chroma Key** panel in the SteamVR dashboard says ON and that its colour matches `DeskWindow.KeyColor`; if the panel is missing, set the tool up again. On a Quest 3, check that ALVR's **Passthrough** is on, with the mode **RGB chroma key** (see "Seeing your desk").
- **Blurry picture on a Quest 3 with ALVR:** raise ALVR's bitrate, set its two view resolutions and turn off its foveation, as in "Setting up a Meta Quest 3".
- **Patches of the game show your room:** lower **Similarity** in the headset's Chroma Key panel.
- **The window is in the wrong place:** press F9 while sitting normally, then hold G and move the mouse (and scroll to turn) until the window is over your keyboard.
- **Something looks wrong:** press F5 for a 3D capture and include it, along with the log, when you report the problem (see below).

## Feedback and bug reports

Report bugs and suggest improvements on [GitHub Issues](https://github.com/Circuitrinos/v_rising_vr_mod/issues). You need a free GitHub account. For a bug, attach your `LogOutput.log`, plus an F5 capture if it's something you can see; Troubleshooting above says where to find them.

## Building from source

The source code is on [GitHub](https://github.com/Circuitrinos/v_rising_vr_mod). This edition is on the `passthrough` branch; VRisingVR is `main`. You need the .NET SDK (6 or newer) and the game with BepInExPack_V_Rising installed and started once, so that `BepInEx\interop` exists.

```
cd VRisingVR
dotnet build -c Release
```

The build copies `VRisingVR.dll` and `openvr_api.dll` into `<GameDir>\BepInEx\plugins\VRisingVR\` and writes the release zips to `dist\`:

- `Circuitrinos-VRisingVR_Passthrough-<version>.zip` is the Thunderstore package. It also works with r2modman's **Import local mod**.
- `VRisingVR_Passthrough-<version>-manual.zip` is for installing by hand.

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
4. Upload `dist\Circuitrinos-VRisingVR_Passthrough-<version>.zip` at <https://thunderstore.io/package/create/>. Choose the team named in `ThunderstoreTeam` and the V Rising community, then pick the **Client** and **Oakveil Update** categories, plus **AI Generated** if the community has one. Set **Contains NSFW content** to No.

Keep the AI disclosure section below and the `AssemblyMetadata` entries in the csproj. Thunderstore requires both for mods made with AI.

Thunderstore uploads can't be edited afterwards, so every change needs a new version.

## AI disclosure

VRisingVR and this passthrough edition were made with generative AI. Their code, documentation and icons were created by Claude, Anthropic's AI model, working in Claude Code. Circuitrinos directed the work and play-tested the mod in VR on real hardware. The plugin also records this in its assembly metadata (`AI_Assisted_Creation` and `AI_Model_Vendor`).

## License

MIT, see `LICENSE`. VRisingVR includes the OpenVR SDK, under Valve's BSD-3-Clause license; see `THIRD-PARTY-NOTICES.txt`. VRisingVR is not affiliated with Stunlock Studios or Valve.
