# XIVibration

A Dalamud plugin that makes your gamepad vibrate on every skill, right on its hits.

## How it works

- **After you log in,** the plugin analyses the animation timeline (`.tmb`) of every skill of every job. It starts with your current job. If you use a mod for an animation, Penumbra hands it the modded file, so the timing matches what you see. It saves one vibration pattern per skill. Skills that already have a pattern are left alone.
- **Hits decide when, the sound decides how strong.** Every hit of the skill's animation gets a pulse. Its strength comes from how loud the skill's sound is at that hit, read through Penumbra so mod sounds count. Bass drives the strong (heavy) motor and crisp sounds the weak (buzz) motor.
- **The last hit is always the strongest:** at least 90% on both motors, fading out.
- **Optional, build from sound bumps instead** (Settings → Sound): each clear bump in the sound becomes a solid block at the sound's level with a short fade-out, and the sound's background doesn't vibrate.
- **You choose what counts as a hit** (Settings → Hit detection):
  - Cues: target flinches, hit sound on the target, effect on the target, follow-up strike sound, **strike sound in an effect**, built-in vibration (mods), MultiHit table, damage number, effect beats (buffs and heals).
  - "Effect on the target" and "MultiHit table" are **off by default**. Target effects often start before or after the real impact.
  - A hit needs: **any one** of the chosen cues, **at least N** at the same moment, or **all** of them at the same moment. "Same moment within" sets the window in frames. Cue types a skill's animation doesn't have don't count, so a strict rule doesn't silence it.
  - Moments the rules leave out show as faint grey markers on the timeline, with a tooltip saying why.
- **Sounds played by effects count too.** Besides the sounds the animation timeline plays, the plugin reads every effect (`.avfx`) the skill spawns, through Penumbra, and finds the sounds the effect plays and when. Mods often put a skill's strike sounds there (Paladin's Fast Blade in some mods plays all three strikes from its effect).
  - Strike sounds in an effect are hit markers (cue "Strike sound in an effect", on by default): names ending in `_t`, `_mt`, `_t2`…, names with hit, cut, counter, slash, impact, stab or blow, and any sound of an effect on the target. The hit lands where the sound really starts (its onset), not at the file's start.
  - Swing sounds (swish, swing, whoosh, swoosh) are never hits, but they add to the sound lanes, to Test and to the strength.
  - When effect strikes are a skill's only markers and its sound's peaks show more hits, the peaks are used.
- **No hit markers: the sound's peaks are the hits.** Some skills have no real markers, only a damage number or effect beats. For those, the plugin analyses the whole sound of the skill (all its files together) and uses its clear peaks as hits (pink waypoints).
  - Each hit lands on the loudest moment of its peak, and peaks closer than 120 ms count as one hit.
  - Only peaks about as loud as the loudest one are used. With the default **peak loudness window** of 20%, a peak must be at least 80% as loud, so quiet echoes and side ripples don't vibrate.
  - Sound → Peak threshold sets how clear a peak must be.
  - Without a readable sound, the damage number or effect beats are used.
- **MultiHit tables** (when chosen): each hit in the table gets its own pulse, including the final hit when the table doesn't add up to 100%. Tables are matched by animation, so every version of a skill gets them.

- **Timed to the real animation:** a pattern starts when the animation actually starts on your character, not when the server message arrives, and it plays faster or slower along with the animation. Hits closer than 90 ms join into one continuous pulse so they can be felt.
- **Patterns stay current:** generated patterns are rebuilt automatically when a skill's animation changes (another mod or option) or the analysis improves. Patterns you edited are never touched.
- **Without a readable sound, strength follows the skill type:** weaponskills, spells, abilities (long cooldowns hit harder), buffs and heals (lighter), and limit breaks.
- **The vibration goes through the game's own controller function,** so it works with every controller the game supports and follows the game's vibration setting.
- **Only your character vibrates.** Mods apply to every character's animations, so a mod with built-in vibration would also make the game rumble when other players or NPCs near you use that skill. The plugin lets the game's own vibration through only while one of your skills is playing (Settings → "Only my character's animations may vibrate", on by default).
- **Animations that already vibrate on their own are left alone.** Turn on *Replace built-in vibration* in Settings to use the plugin's pattern for those too.

## Features

- Works out of the box on every skill of every job.
- The Skills tab shows your current job's skills, grouped into weaponskills, spells, abilities, role actions and limit breaks. You can also pick another job to look at.
- Switch single skills off, test them, or edit any pattern. A pattern is a list of clips, each with a start, a length, strong and weak motor levels, a shape and **curved fade-in / fade-out**. An edited pattern becomes yours and is never changed by re-analysis.
- **Character presets** (Presets tab): every skill you change, plus the skills you switch off, is saved in your character's preset. Each character has its own, and it switches when you log in. Automatic patterns are shared.
  - Export a preset as a short code (`XIVIB1:…`, to paste in Discord) or as a `.json` file.
  - Import from the clipboard or a file, choosing **Merge** (imported skills replace yours, the rest stays) or **Replace**.
  - Copy one character's preset to another.
- **Re-analyse** a single skill, or all of them (Settings, or `/xivib analyse`), after changing mods. Your own patterns are kept.
- **Timeline editor that works like clips on DAW tracks.** The Strong and Weak motors are two tracks, with the skill's sound drawn behind the clips like an audio waveform (bass in Strong, treble in Weak). Every hit of the animation is a numbered waypoint.
  - Drag a clip to move it. Drag its left edge to trim the start and its right edge to change the length. Drag the line on top of a clip to set that motor's level (Shift for both motors).
  - **Fades like in a DAW:** drag the small squares at a clip's top corners to set the fade-in and fade-out, and drag the dot in the middle of a fade up or down to bend its curve (double-click the dot for linear). The right-click menu has curve presets: linear, fast drop with a thin tail, or hold then drop.
  - **Every sound file of the skill gets its own lane** under the motor tracks, at the time it starts, with its own waveform. Sounds played by an effect are labelled "from effect …" and have a double outline. A file that can't be played says why: "not in the game" (mods sometimes point at a missing file on purpose to silence a sound) or "can't decode". Hover a lane for the file's path (mod or game), start and length. Click a lane to mute that file in Test.
  - Clips snap to hits, to where sounds start and end, to other clips and to animation frames. Alt places freely, Ctrl moves in 1 ms steps.
  - Click a waypoint to add a clip on that hit, or to move the selected clips there. Double-click empty space to add a clip.
  - Ctrl-click or drag a box to select several clips. Delete, Ctrl+D (duplicate), Ctrl+C / Ctrl+V, Ctrl+A, and Ctrl+Z / Ctrl+Y (undo / redo) all work. Right-click a clip for its shape and fade curves.
  - Ctrl + mouse wheel zooms and the wheel scrolls. The pulse table below shows the exact numbers.
- **Test plays the skill's sound with the vibration.** All the skill's sounds, mod sounds and sounds played by effects included, are mixed at their animation times, so you hear and feel the skill together. Settings → Test has the sound volume (0% = no sound) and a "Vibration delay vs. sound" slider to make up for audio latency. Test also has a playhead, and there's a live motor meter.
- Presets, Copy and Paste, and import of vibration entries from any `.tmb` file.
- Master strength, strength per skill, and "Only in combat".
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
