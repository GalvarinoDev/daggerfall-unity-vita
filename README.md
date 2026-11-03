# Daggerfall Unity Vita

<p align="center"><img src="./Assets/Resources/Vita/StartupBackground2.png"></p>

This is a port of [Daggerfall Unity](https://github.com/Interkarma/daggerfall-unity), the open source recreation of The Elder Scrolls II: Daggerfall, for the *PS Vita*.

The port runs on the Vita's own Unity player, so nothing is emulated. I backported upstream's code to Unity 2018.2.19f1 and build it with IL2CPP, and the game renders at the native 960x544. Most game data is baked on a PC into packs made for the Vita and ships inside the VPK, which keeps load times and memory use down.

This is an alpha, so expect bugs. Back up your saves before updating.

## Changelog

### v.0.1.0-alpha

- Initial release.

## Disclaimer

Daggerfall Unity Vita is an unofficial fan project. It is not affiliated with or endorsed by Bethesda Softworks, Sony or Unity.

You need your own copy of Daggerfall to play. DOS Daggerfall is free on [Steam](https://store.steampowered.com/app/1812390/The_Elder_Scrolls_II_Daggerfall/).

Please report bugs in this port here, not to the Daggerfall Unity team.

## Setup Instructions (For End Users)

You need a Vita running HENkaku or Enso with [VitaShell](https://github.com/TheOfficialFloW/VitaShell/releases) installed, and about 1.7 GB free on `ux0:` for the install. The game and its data take about 1.1 GB once you delete the VPK.

- Download `DFU.vpk` from the [Releases](https://github.com/GalvarinoDev/daggerfall-unity-vita/releases) page and install it with VitaShell.
- Install DOS Daggerfall on your PC. On Steam, the folder you need is `DF/DAGGER/ARENA2` inside the game's install folder.
- Copy the whole `ARENA2` folder to your Vita, so that you end up with:

```
ux0:data/DaggerfallUnity/ARENA2
```

- Launch the game from the LiveArea. The engine load can take about a minute.

The original 1996 manual is included. Open it with the Manual button on the game's LiveArea screen.

The game keeps saves and settings in `ux0:data/DaggerfallUnity/UserData`.

## Controls

| Button | Action |
|---|---|
| Left stick | Move |
| Right stick | Look |
| L or R | Swing weapon |
| Cross | Activate, click in menus |
| Circle | Tap: inventory. Hold: transport. Back in menus |
| Square | Ready weapon |
| Triangle | Jump, right click in menus |
| Select | Tap: change HUD. Hold: spellbook |
| Start | Pause menu |
| D-pad up | Recast spell |
| D-pad right | Travel map |
| D-pad down | Automap |
| D-pad left | Logbook |
| Rear touch, left half | Quick tap: toggle sneak |
| Rear touch, both halves | Hold: crouch |

The front touch screen clicks in menus. A new game starts with the HUD hidden, so tap Select to bring it up. In a text box, Triangle reopens the system keyboard.

## Performance

I measured these on one PCH-1000 with the alpha's data and code, following a fixed test route. Frame rates change a lot depending on where you are in the game.

| Test | Result |
|---|---|
| 46 stops around the game | 53.9 FPS average |
| 7 cities | 49.3 FPS average, 31.7 FPS low |
| Loading a city | 22 to 34 seconds |
| Entering a city's palace | 34 to 50 seconds |

The default speed setting raises the CPU to 444 MHz and the GPU clock, and limits screen brightness. You can change this with CPU/GPU speed in the pause menu.

## Known Issues

- English only.
- Mods aren't supported yet, but support is coming soon. The Vita can't run PC mods as they are. Each one has to be converted into a format the Vita can read, and I need to ask each mod's author for permission first.
- The web browser and PlayStation Store can't run alongside the game, because it uses the Vita's extra memory mode.
- Replacement videos, Post Processing and TextMeshPro fonts from PC Daggerfall Unity are not available. The original cutscenes still play.

## Build Instructions (For Developers)

The Vita's Unity support is not publicly available, so you need your own licensed copy of Unity 2018.2.19f1 with PS Vita build support.

The scripts in `vita/` are written for a Linux machine running the editor under Proton, and you will need to adjust their paths for your setup. `vita/unity2017.sh` expects a machine-specific `vita/local.env`, which is not in the repository. Baked data, rendered music and some images are not in the repository either, so a fresh clone has to generate them. I haven't tested that yet.

With the Unity editor closed, run from the repository root:

```bash
vita/apply_settings.sh

# Bake game data. Set ARENA2 if your copy isn't the default Steam one.
export ARENA2=/path/to/ARENA2
for s in textures texfiles skies models blocks weapons maps mapdata blockdata images fonts strings books quests careers biogs locbooks; do
  vita/tools/bake_$s.sh
done
vita/tools/faction_bake.sh
vita/tools/namegen_bake.sh

# Render the music with Daggerfall Unity's own synth.
vita/tools/render_music.sh

# Build and pack vita/build/DFU.vpk.
vita/build.sh && python3 vita/tools/pack_vpk.py
```

Vita-only changes to upstream's files sit behind `#if UNITY_PSP2` with upstream's original lines kept beside them, so upstream merges stay readable. `vita/tools/guard_diff.py` helps keep them that way.

`vita/bootmusic` is an optional taiHEN plugin that plays the title theme during the engine load. `dfu-vita-converters` holds PC scripts that convert mod data for the port.

## Credits

- [Interkarma](https://github.com/Interkarma), Daggerfall Workshop and all the Daggerfall Unity contributors for [Daggerfall Unity](https://github.com/Interkarma/daggerfall-unity).
- Bethesda Softworks for The Elder Scrolls II: Daggerfall.
- [Vwing](https://github.com/Vwing) for ideas from their Android port of Daggerfall Unity.
- [AJ](https://github.com/AJ170) and the PS Vita Forever server for their help with the project.

## License

MIT, the same as upstream Daggerfall Unity. See [LICENSE](./LICENSE). The game's data is not covered by this license and is not included in this repository.
