# XIVibration

A Dalamud plugin that adds controller vibration to any skill.

## Why the Classic DRG mod vibrates and the Classic Paladin mod doesn't

Every skill animation has a timeline file (`.tmb`). A timeline is a list of timed entries: `C012` spawns a VFX, `C063` plays a sound, and so on. **`C120` is "Controller Vibration".** When the game reaches a C120 entry it rumbles the pad.

| | C120 entries | Where |
|---|---|---|
| Classic DRG | 33 | True Thrust, Vorpal Thrust, Heavens' Thrust, Lance Barrage, Spiral Blow, Mirage Dive, Rise of the Dragon, Wyrmwind Thrust, and more |
| Classic Paladin | 3 | only `lbk_tank_lv3.tmb`, the tank limit break, which already vibrates in the unmodded game |

Screen shake (the CameraQuake effector, type 6, in `.avfx` files) is in **both** mods, so it is not the cause.

C120 layout (0x18 bytes):

```
0x00  "C120"
0x04  int32   size = 0x18
0x08  uint16  entry id
0x0A  uint16  start time, in frames (30 fps)
0x0C  int32   duration, in frames
0x10  int32   unknown, always 0
0x14  int32   wave type: 1, 2 or 3
```

Example, Spiral Blow (`ws_s20.tmb`): hits at frames 11, 20, 29 (type 2, 6 frames each), frame 39 (type 3, 5f), and frame 59 (type 1, 15f). The mod uses 2 for small hits, 3 just before the finisher, and 1 for the finisher, so the plugin treats them as 2 = light, 3 = medium, 1 = heavy. That mapping is a guess.

## How the plugin works

You don't need to edit `.tmb` files:

- **Detecting a skill**: it hooks `ActionEffectHandler.Receive`. For your own character this fires when the skill goes off, which is when its animation timeline starts. So pulse delays line up the same way C120 frames do.
- **Vibrating**: it calls the game's own `PadDeviceInterface::SetVibration` (from `InputDeviceManager → PadDevice`), the same pad layer the game uses for its own rumble. It works with every controller the game supports (XInput, DualShock 4, DualSense), including under Wine/Proton. That function is hooked so the plugin's vibration and the game's (limit breaks, modded C120) combine instead of cancelling each other.

## Features

- Add any skill from a searchable list (current job, or all jobs), or click a skill you just used under "Recently used".
- Per-skill pattern: any number of pulses, each with a delay, a length, strong and weak motor levels, and a shape (constant, fade out, fade in, swell, throb).
- Timeline preview, Test button with a playhead, live motor meter.
- Presets, plus Copy and Paste between skills.
- **Import from .tmb**: turns the C120 entries of any `.tmb` (a file from a mod folder, or a game path like `chara/action/ws/bt_2sp_emp/ws_s20.tmb`) into pulses. Use it to copy the DRG mod's vibration onto a Paladin skill.
- An optional fallback pattern for every skill without its own rule.
- Master strength, a per-skill strength setting, "Only in combat", and an option to follow the game's vibration setting.
- Commands: `/xivib`, `/xivib on|off|toggle|test|stop`.

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, tick **Enabled** and click **Save**:

   ```
   https://raw.githubusercontent.com/Charlie1361/xivibration/main/repo.json
   ```

3. Open `/xlplugins`, search for **XIVibration** and install it.
4. Type `/xivib` to open it.

## Publishing an update

1. Bump `<Version>` in [XIVibration/XIVibration.csproj](XIVibration/XIVibration.csproj).
2. Run `./publish.sh`. It builds the plugin, copies the zip to `dist/latest.zip` and regenerates `repo.json`.
3. Commit and push. Dalamud will offer the update to everyone who has the plugin.
