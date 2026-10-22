# Changelog

## 1.0.2

No changes to the mod itself.

- New install instructions for players who are new to mod managers: installing from Thunderstore, importing a downloaded zip, installing by hand, updating and uninstalling.
- The README and the plugin's assembly metadata now say the mod was made with AI, as Thunderstore's rules require.
- The Thunderstore zip is named `Circuitrinos-VRisingVR-<version>.zip`, so a mod manager shows the author when you import it.
- The README notes that Stunlock Studios doesn't allow mods on its official servers.

## 1.0.1

New default view settings, tuned in play-testing:

- `View.WorldScale` 14.05 (was 8) and `View.HeightOffset` 2.57 (was 0).
- `View.RenderScale` 1 (was 0.7), SteamVR's full recommended resolution. Lower it if your frame rate is low.
- `HUD.Distance` 0.59 m and `HUD.Width` 0.71 m (were 1.1 m and 1.2 m).
- `Screen.Distance` 3.1 m (was 2.5 m).

A config file from 1.0.0 keeps its values. Delete `BepInEx\config\vrisingvr.tabletop.cfg` to get the new defaults.

## 1.0.0

First release.

- Stereo 3D tabletop view from the game's own top-down camera, with head tracking. The game's camera rotation and zoom still work.
- The game's HUD, menus and map on a transparent layer in front of you, and a virtual screen in the main menu.
- Health bars, names, levels and icons drawn over the characters in 3D, using the game's own graphics.
- The aim cursor on the ground in 3D, where your attacks go.
- An in-game settings panel (F4), and hotkeys for re-centering, world scale and toggling VR.
- The desktop window mirrors your left eye while in VR, saving a render each frame.
