# XIVibration

A Dalamud plugin that makes your gamepad vibrate on every skill, right on its hits.

## How it works

- **When a skill goes off,** the plugin reads that skill's animation timeline (`.tmb`). If you use a mod for the animation, Penumbra hands it the modded file, so the timing matches what you see.
- **The timeline marks the hit frame.** The plugin vibrates on that frame.
- **Multi-hit skills:** if the MultiHit plugin has a hit table for the skill, every hit gets its own pulse. Hits that do more damage vibrate harder.
- **Strength follows the skill type:** weaponskills, spells, abilities (long cooldowns hit harder), buffs and heals (a light tap), and limit breaks.
- **The vibration goes through the game's own controller function,** so it works with every controller the game supports and follows the game's vibration setting.
- **Animations that already vibrate on their own are left alone.** Turn on *Replace built-in vibration* in Settings to use the plugin's pattern for those too.

## Features

- Works out of the box on every skill of every job.
- The Skills tab shows your current job's skills, grouped into weaponskills, spells, abilities, role actions and limit breaks. You can also pick another job to look at.
- Switch single skills off, test them, or click **Customize** to design your own pattern. A pattern is a list of pulses, each with a delay, a length, strong and weak motor levels and a shape. It can count from the first hit or from the animation start.
- Timeline preview with the hit frames marked, a Test button with a playhead, and a live motor meter.
- Presets, Copy and Paste, and import of vibration entries from any `.tmb` file.
- Master strength, strength per skill type and per skill, and "Only in combat".
- A *Last skill* panel that shows what was played for your last skill and why.
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
