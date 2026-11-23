# XIVibration

A Dalamud plugin that makes your gamepad vibrate on every skill, right on its hits.

## How it works

- **After you log in,** the plugin analyses the animation timeline (`.tmb`) of every skill of every job. It starts with your current job. If you use a mod for an animation, Penumbra hands it the modded file, so the timing matches what you see. It saves one vibration pattern per skill. Skills that already have a pattern are left alone.
- **Hits decide when, the sound decides how strong.** Every hit of the skill's animation gets a pulse. Its strength comes from how loud the skill's sound is at that hit, read through Penumbra so mod sounds count. Bass drives the strong (heavy) motor and crisp sounds the weak (buzz) motor.
- **The last hit is always the strongest:** at least 90% on both motors, fading out.
- **Optional, build from sound bumps instead** (Settings → Sound): each clear bump in the sound becomes a solid block at the sound's level with a short fade-out, and the sound's background doesn't vibrate. Its settings:
  - **Threshold:** how far a bump must stand out.
  - **Fade-out:** the length of the fade after each block.
  - **Join bumps closer than:** turns nearby bumps into one block.
  - **Also pulse on hits without a sound bump.**
- **Hits come from the animation:**
  - the target's hit reaction (Flinch)
  - effects and sounds that land on the target
  - follow-up strike sounds
  - vibration the animation already has
  - the parts it plays from other timelines

  Buffs and heals without sounds follow their effect beats.
- **MultiHit tables** (optional): if the MultiHit plugin has a hit table for a skill, each hit in it gets its own pulse too, harder for hits that do more damage. That includes the final hit when the table doesn't add up to 100%. Tables are matched by animation, so every version of a skill gets them.
- **Timed to the real animation:** a pattern starts when the animation actually starts on your character, not when the server message arrives, and it plays faster or slower along with the animation. Hits closer than 90 ms join into one continuous pulse so they can be felt.
- **Patterns stay current:** generated patterns are rebuilt automatically when a skill's animation changes (another mod or option) or the analysis improves. Patterns you edited are never touched.
- **Strength follows the skill type:** weaponskills, spells, abilities (long cooldowns hit harder), buffs and heals (lighter), and limit breaks. The last hit of an attack is the finisher.
- **The vibration goes through the game's own controller function,** so it works with every controller the game supports and follows the game's vibration setting.
- **Only your character vibrates.** Mods apply to every character's animations, so a mod with built-in vibration would also make the game rumble when other players or NPCs near you use that skill. The plugin lets the game's own vibration through only while one of your skills is playing (Settings → "Only my character's animations may vibrate", on by default).
- **Animations that already vibrate on their own are left alone.** Turn on *Replace built-in vibration* in Settings to use the plugin's pattern for those too.

## Features

- Works out of the box on every skill of every job.
- The Skills tab shows your current job's skills, grouped into weaponskills, spells, abilities, role actions and limit breaks. You can also pick another job to look at.
- Switch single skills off, test them, or edit any pattern. A pattern is a list of pulses, each with a delay, a length, strong and weak motor levels and a shape. An edited pattern becomes yours and is never changed by re-analysis.
- **Re-analyse** a single skill, or all of them (Settings, or `/xivib analyse`), after changing mods. Your own patterns are kept.
- **Timeline editor that works like clips on DAW tracks.** The Strong and Weak motors are two tracks, with the skill's sound drawn behind the clips like an audio waveform (bass in Strong, treble in Weak). Every hit of the animation is a numbered waypoint.
  - Drag a clip to move it. Drag its left edge to trim the start and its right edge to change the length. Drag the line on top of a clip to set that motor's level (Shift for both motors).
  - Clips snap to hits, to where sounds start and end, to other clips and to animation frames. Alt places freely, Ctrl moves in 1 ms steps.
  - Click a waypoint to add a clip on that hit, or to move the selected clips there. Double-click empty space to add a clip.
  - Ctrl-click or drag a box to select several clips. Delete, Ctrl+D (duplicate), Ctrl+C / Ctrl+V, Ctrl+A, and Ctrl+Z / Ctrl+Y (undo / redo) all work. Right-click a clip for its shape.
  - Ctrl + mouse wheel zooms and the wheel scrolls. The pulse table below shows the exact numbers.
- Test button with a playhead, and a live motor meter.
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
