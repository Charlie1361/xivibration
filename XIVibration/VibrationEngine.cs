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
    private readonly Stopwatch clock = Stopwatch.StartNew();
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

    public void Play(IReadOnlyList<VibrationPulse> pulses, float scale, bool ignoreGates = false)
    {
        if (!ignoreGates && !this.config.Enabled)
            return;

        var now = this.clock.ElapsedMilliseconds;
        lock (this.sync)
        {
            if (!ignoreGates && this.config.CancelPreviousOnNewAction)
                this.active.Clear();

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
        this.suppressGameUntilMs = Math.Max(this.suppressGameUntilMs, this.clock.ElapsedMilliseconds + (long)durationMs);
        this.gameRight = 0;
        this.gameLeft = 0;
    }

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

        var now = this.clock.ElapsedMilliseconds;
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

                var env = Envelope(a.Pulse.Shape, t, a.Pulse.DurationMs) * a.Scale;
                strong = Math.Max(strong, a.Pulse.Strong * env);
                weak = Math.Max(weak, a.Pulse.Weak * env);
            }
        }

        return (Math.Clamp((int)MathF.Round(strong), 0, 100), Math.Clamp((int)MathF.Round(weak), 0, 100));
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
            if (this.clock.ElapsedMilliseconds < this.suppressGameUntilMs)
            {
                // Replacing built-in vibration: drop what the game asked for.
                rightMotor = 0;
                leftMotor = 0;
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
