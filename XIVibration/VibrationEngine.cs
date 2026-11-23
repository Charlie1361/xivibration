using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace XIVibration;

/// <summary>
/// Drives the controller through the game's own <c>PadDeviceInterface::SetVibration</c>,
/// the same function the game uses for its own rumble.
/// Because it is the game's pad layer, this works for every controller the game supports
/// (XInput, DualShock 4, DualSense), including under Wine/Proton.
///
/// The vtable function is hooked so that whatever the game asks for is merged (max) with
/// the plugin's pulses instead of the two overwriting each other.
/// </summary>
public sealed unsafe class VibrationEngine : IDisposable
{
    private delegate void SetVibrationDelegate(PadDeviceInterface* self, int rightMotor, int leftMotor);

    private sealed class ActivePulse
    {
        public required VibrationPulse Pulse;
        public required long StartMs;
        public required float Scale;
    }

    private readonly Configuration config;
    private readonly List<ActivePulse> active = new();
    private readonly object sync = new();

    private Hook<SetVibrationDelegate>? hook;
    private PadDeviceInterface* pad;
    private bool hookFailed;

    // Last levels the game itself requested, and the last levels sent to the device.
    private int gameRight, gameLeft;
    private int sentRight, sentLeft;
    private long lastSendMs;
    private long nextSettingCheckMs;
    private long suppressGameUntilMs;
    private long localWindowUntilMs;
    private bool lastRequestBlocked;

    public VibrationEngine(Configuration config)
    {
        this.config = config;
    }

    public bool PadFound => pad != null;

    public bool HookActive => hook is { IsEnabled: true };

    /// <summary>null = could not read the game option.</summary>
    public bool? GameVibrationEnabled { get; private set; }

    public int CurrentStrong { get; private set; }

    public int CurrentWeak { get; private set; }

    public string? LastError { get; private set; }

    /// <param name="startMs">When the pattern's time 0 is (Clock.NowMs). May be in the past; pulses already over are skipped.</param>
    public void Play(IReadOnlyList<VibrationPulse> pulses, float scale, bool ignoreGates = false, long? startMs = null)
    {
        if (!ignoreGates && !this.config.Enabled)
            return;

        var now = startMs ?? Clock.NowMs;
        lock (this.sync)
        {

            foreach (var p in pulses)
            {
                if (p.DurationMs <= 0 || (p.Strong <= 0 && p.Weak <= 0))
                    continue;
                this.active.Add(new ActivePulse { Pulse = p.Clone(), StartMs = now, Scale = scale });
            }
        }
    }

    /// <summary>Ignore the game's own vibration requests for a while (used only when replacing built-in vibration).</summary>
    public void SuppressGame(float durationMs)
    {
        this.suppressGameUntilMs = Math.Max(this.suppressGameUntilMs, Clock.NowMs + (long)durationMs);
        this.gameRight = 0;
        this.gameLeft = 0;
    }

    /// <summary>The game's own vibration may play until then: one of my character's animations is running.</summary>
    public void OpenLocalWindow(long untilMs) => this.localWindowUntilMs = Math.Max(this.localWindowUntilMs, untilMs);

    /// <summary>How many times the game tried to vibrate for another character's animation.</summary>
    public int BlockedFromOthers { get; private set; }

    public void StopAll()
    {
        lock (this.sync)
            this.active.Clear();
    }

    /// <summary>Called every frame on the framework thread.</summary>
    public void Update()
    {
        if (!this.EnsurePad())
            return;

        var now = Clock.NowMs;
        if (now >= this.nextSettingCheckMs)
        {
            this.nextSettingCheckMs = now + 1000;
            this.GameVibrationEnabled = ReadGameVibrationSetting();
        }

        var (strong, weak) = this.Evaluate(now);
        if (this.config.RespectGameSetting && this.GameVibrationEnabled == false)
            (strong, weak) = (0, 0);

        this.CurrentStrong = strong;
        this.CurrentWeak = weak;

        var outRight = Math.Max(this.gameRight, weak);
        var outLeft = Math.Max(this.gameLeft, strong);
        var ours = strong > 0 || weak > 0;

        // Send when the value changes, and refresh while we are vibrating
        // in case something else reset the motors in between.
        if (outRight != this.sentRight || outLeft != this.sentLeft || (ours && now - this.lastSendMs > 100))
            this.Send(outRight, outLeft, now);
    }

    private (int Strong, int Weak) Evaluate(long now)
    {
        float strong = 0, weak = 0;
        lock (this.sync)
        {
            for (var i = this.active.Count - 1; i >= 0; i--)
            {
                var a = this.active[i];
                var t = now - a.StartMs - a.Pulse.DelayMs;
                if (t < 0)
                    continue;
                if (t >= a.Pulse.DurationMs)
                {
                    this.active.RemoveAt(i);
                    continue;
                }

                var env = Envelope(a.Pulse, t) * a.Scale;
                strong = Math.Max(strong, a.Pulse.Strong * env);
                weak = Math.Max(weak, a.Pulse.Weak * env);
            }
        }

        return (Math.Clamp((int)MathF.Round(strong), 0, 100), Math.Clamp((int)MathF.Round(weak), 0, 100));
    }

    /// <summary>A clip's level at time t (ms into it): its shape, times its fade-in and fade-out.</summary>
    public static float Envelope(VibrationPulse p, float t)
    {
        var env = Envelope(p.Shape, t, p.DurationMs);
        if (p.FadeInMs > 0 && t < p.FadeInMs)
            env *= Fade(t / p.FadeInMs, p.FadeInCurve);
        var outStart = p.DurationMs - p.FadeOutMs;
        if (p.FadeOutMs > 0 && t > outStart)
            env *= Fade(1f - (t - outStart) / p.FadeOutMs, p.FadeOutCurve);
        return env;
    }

    /// <summary>
    /// A fade from 0 (x = 0) to 1 (x = 1). Curve -100..100: 0 is linear; positive bends it so a
    /// fade-out drops fast and leaves a thin tail; negative holds the level and drops late.
    /// </summary>
    public static float Fade(float x, int curve)
    {
        x = Math.Clamp(x, 0f, 1f);
        var k = MathF.Pow(2f, Math.Clamp(curve, -100, 100) / 33f);
        return MathF.Pow(x, k);
    }

    public static float Envelope(PulseShape shape, float t, float duration)
    {
        var x = duration <= 0 ? 1f : Math.Clamp(t / duration, 0f, 1f);
        return shape switch
        {
            PulseShape.FadeOut => 1f - x,
            PulseShape.FadeIn => x,
            PulseShape.Bump => MathF.Sin(MathF.PI * x),
            PulseShape.Throb => ((int)(t / 70f) & 1) == 0 ? 1f : 0f,
            _ => 1f,
        };
    }

    private bool EnsurePad()
    {
        try
        {
            var mgr = InputDeviceManager.Instance();
            if (mgr == null || mgr->PadDevice == null)
            {
                this.pad = null;
                return false;
            }

            this.pad = (PadDeviceInterface*)mgr->PadDevice;

            if (this.hook == null && !this.hookFailed)
            {
                var address = (nint)this.pad->VirtualTable->SetVibration;
                this.hook = Plugin.GameInterop.HookFromAddress<SetVibrationDelegate>(address, this.SetVibrationDetour);
                this.hook.Enable();
                Plugin.Log.Info($"Hooked PadDeviceInterface::SetVibration at 0x{address:X}");
            }

            return true;
        }
        catch (Exception ex)
        {
            this.hookFailed = true;
            this.LastError = ex.Message;
            Plugin.Log.Error(ex, "Could not hook SetVibration; falling back to calling it directly.");
            return this.pad != null;
        }
    }

    private void Send(int right, int left, long now)
    {
        this.sentRight = right;
        this.sentLeft = left;
        this.lastSendMs = now;
        try
        {
            if (this.hook != null)
                this.hook.Original(this.pad, right, left);
            else
                this.pad->SetVibration(right, left);
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }
    }

    private void SetVibrationDetour(PadDeviceInterface* self, int rightMotor, int leftMotor)
    {
        if (self == this.pad)
        {
            if (Clock.NowMs < this.suppressGameUntilMs)
            {
                // Replacing built-in vibration: drop what the game asked for.
                rightMotor = 0;
                leftMotor = 0;
            }
            else if (this.config.Enabled && this.config.OnlyMyCharacter && Clock.NowMs >= this.localWindowUntilMs && (rightMotor > 0 || leftMotor > 0))
            {
                // Vibration from someone else's animation (mods apply to everyone): not mine, drop it.
                if (!this.lastRequestBlocked)
                    this.BlockedFromOthers++;
                this.lastRequestBlocked = true;
                rightMotor = 0;
                leftMotor = 0;
            }
            else
            {
                this.lastRequestBlocked = false;
            }

            this.gameRight = rightMotor;
            this.gameLeft = leftMotor;
            rightMotor = Math.Max(rightMotor, this.CurrentWeak);
            leftMotor = Math.Max(leftMotor, this.CurrentStrong);
            this.sentRight = rightMotor;
            this.sentLeft = leftMotor;
        }

        this.hook!.Original(self, rightMotor, leftMotor);
    }

    private static bool? ReadGameVibrationSetting()
    {
        try
        {
            if (Plugin.GameConfig.TryGet(SystemConfigOption.ForceFeedBack, out uint u))
                return u != 0;
            if (Plugin.GameConfig.TryGet(SystemConfigOption.ForceFeedBack, out bool b))
                return b;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public void Dispose()
    {
        this.StopAll();
        if (this.pad != null)
        {
            // Hand the motors back to whatever the game wanted.
            try
            {
                if (this.hook != null)
                    this.hook.Original(this.pad, this.gameRight, this.gameLeft);
                else
                    this.pad->SetVibration(0, 0);
            }
            catch
            {
                // ignored
            }
        }

        this.hook?.Dispose();
    }
}
