# Daggerfall Unity Vita

<p align="center"><img src="./Assets/Resources/Vita/StartupBackground2.png"></p>

This is a port of [Daggerfall Unity](https://github.com/Interkarma/daggerfall-unity), the open source recreation of The Elder Scrolls II: Daggerfall, for the *PS Vita*.

The port runs on the Vita's own Unity player, so nothing is emulated. I backported upstream's code to Unity 2018.2.19f1 and build it with IL2CPP, and the game renders at the native 960x544. Most game data is baked on a PC into packs made for the Vita and ships inside the VPK, which keeps load times and memory use down.

This is an alpha, so expect bugs. Back up your saves before updating.

## Changelog

### v.0.2.0-alpha

This update is mostly about smoother play. Mods still aren't included because I'm waiting on permission for a couple of them.

- Towns that load while you walk build a little at a time, so the long freezes at town edges are mostly gone.
- When a town builds nearby, its far blocks are hidden right away. They used to be drawn for 12 to 15 seconds.
- Trees on new terrain fill in over several frames.
- Civilians you can't see stop moving until they're back in view, which helps in big cities.
- Leaving a town and walking straight back no longer rebuilds the whole town in one frame.
- Torches, candles and other dungeon sprites animate from one shared batch. Daggerfall castle went from about 42 to 45 FPS on my test walk.
- Garbage collection hitches dropped from about 10 to about 2 per 3-minute dungeon walk.
- The automap's background scan is cheaper.
- Songs loop in place. Each restart used to cause a short freeze and a gap of silence about once a minute.
- Common sounds load during the loading screen, so they don't stall the first time they play.
- Snow and rain cost less and look the same.
- Spell icons are prebaked, so the spellbook opens faster.
- The camera moves more smoothly while you walk.

### v.0.1.0-alpha

- Initial release.

## Disclaimer

Daggerfall Unity Vita is an unofficial fan project. It is not affiliated with or endorsed by Bethesda Softworks, Sony or Unity.

You need your own copy of Daggerfall to play. DOS Daggerfall is free on [Steam](https://store.steampowered.com/app/1812390/The_Elder_Scrolls_II_Daggerfall/).

Please report bugs in this port here, not to the Daggerfall Unity team.

## Setup for players

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

## Lighting

You can adjust the lighting from the dropdown list in the pause menu. Each entry moves to its next value when you click it, and the changes apply right away.

| Option | Values | Default |
|---|---|---|
| Dungeon light | 30%, 50%, 70%, 100% | 100% |
| Fire light (torches and lanterns) | 50%, 75%, 100%, 150%, 200% | 100% |
| Fire colour | White, Slightly warm, Warm, Warmest | White |
| Torch reach | 6 m, 9 m, 12 m | 6 m |

The defaults match the look of Daggerfall Unity on PC.

## Performance

I measured these on one PCH-1000, with a bot walking fixed routes at running speed. Frame rates change a lot depending on where you are in the game.

| Test | Result |
|---|---|
| Walking a test set of 39 towns (0.2.0, just before the last fixes) | 51.9 FPS average, no crashes |
| Walking a test set of 49 dungeons (0.2.0, just before the last fixes) | 57 FPS average, no crashes |
| Loading a city (alpha) | 22 to 34 seconds |
| Entering a city's palace (alpha) | 34 to 50 seconds |

The default speed setting raises the CPU to 444 MHz and the GPU clock, and limits screen brightness. You can change this with CPU/GPU speed in the pause menu.

## Known issues

- English only.
- Daggerfall castle still has short hitches at some doorways.
- No mods yet. The Vita can't run PC mods as they are, so each one has to be converted, and I ask each author for permission first. Support is coming.
- The web browser and PlayStation Store can't run alongside the game, because it uses the Vita's extra memory mode.
- Replacement videos, Post Processing and TextMeshPro fonts from PC Daggerfall Unity are not available. The original cutscenes still play.

## Building it yourself

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
