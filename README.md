# XIVibration

A Dalamud plugin that makes your gamepad vibrate on every skill, right on its hits.

## How it works

- **After you log in,** the plugin analyses the animation timeline (`.tmb`) of every skill of every job. It starts with your current job. If you use a mod for an animation, Penumbra hands it the modded file, so the timing matches what you see. It saves one vibration pattern per skill. Skills that already have a pattern are left alone.
- **Every hit gets a pulse.** A timeline marks the damage number only once, so the visible hits are found from what happens in the animation:
  - the target's hit reaction (Flinch)
  - effects and sounds that land on the target
  - vibration the animation already has
  - the parts it plays from other timelines

  Buffs and heals follow their effect beats, for example a shield raised and then slammed. A damage number alone only counts when the animation shows no impact.
- **MultiHit tables** (optional): if the MultiHit plugin has a hit table for a skill, each hit in it gets its own pulse too, harder for hits that do more damage. That includes the final hit when the table doesn't add up to 100%. Tables are matched by animation, so every version of a skill gets them.
- **Strength follows the skill type:** weaponskills, spells, abilities (long cooldowns hit harder), buffs and heals (lighter), and limit breaks. The last hit of an attack is the finisher.
- **The vibration goes through the game's own controller function,** so it works with every controller the game supports and follows the game's vibration setting.
- **Animations that already vibrate on their own are left alone.** Turn on *Replace built-in vibration* in Settings to use the plugin's pattern for those too.

## Features

- Works out of the box on every skill of every job.
- The Skills tab shows your current job's skills, grouped into weaponskills, spells, abilities, role actions and limit breaks. You can also pick another job to look at.
- Switch single skills off, test them, or edit any pattern. A pattern is a list of pulses, each with a delay, a length, strong and weak motor levels and a shape. An edited pattern becomes yours and is never changed by re-analysis.
- **Re-analyse** a single skill, or all of them (Settings, or `/xivib analyse`), after changing mods. Your own patterns are kept.
- Timeline preview with the hit frames marked, a Test button with a playhead, and a live motor meter.
- Presets, Copy and Paste, and import of vibration entries from any `.tmb` file.
- Master strength, strength per skill type and per skill, and "Only in combat".
- A *Last skill* panel that shows what was played for your last skill and why.
- Commands: `/xivib`, `/xivib on|off|toggle|test|stop|analyse`.

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
