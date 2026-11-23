using System.Collections.Generic;
using System.Linq;

namespace XIVibration;

/// <summary>Starting points for custom patterns. Delays count from the first hit.</summary>
public static class Presets
{
    public sealed record Preset(string Name, string Description, VibrationPulse[] Pulses);

    private static VibrationPulse P(int delay, int duration, int strong, int weak, PulseShape shape = PulseShape.Constant)
        => new() { DelayMs = delay, DurationMs = duration, Strong = strong, Weak = weak, Shape = shape };

    /// <summary>
    /// Approximate motor levels for C120 wave types. The game doesn't document them;
    /// 1 is used for finishers, 3 for medium hits and 2 for small hits.
    /// </summary>
    public static (int Strong, int Weak, PulseShape Shape) ForWaveType(int waveType) => waveType switch
    {
        1 => (100, 90, PulseShape.FadeOut),
        2 => (25, 55, PulseShape.Constant),
        3 => (65, 75, PulseShape.Constant),
        _ => (50, 60, PulseShape.Constant),
    };

    public static readonly Preset[] All =
    {
        new("Tap", "Short light buzz.", new[] { P(0, 110, 15, 55) }),
        new("Hit", "A solid single hit.", new[] { P(0, 200, 60, 65) }),
        new("Heavy", "A big impact that fades out.", new[] { P(0, 500, 100, 90, PulseShape.FadeOut) }),
        new("Double hit", "Two quick hits.", new[] { P(0, 150, 45, 60), P(250, 200, 70, 70) }),
        new("Triple hit", "Three hits, the last one is harder.", new[] { P(0, 130, 35, 55), P(220, 130, 45, 60), P(450, 300, 90, 85, PulseShape.FadeOut) }),
        new("Combo (5 hits)", "Three light hits, a medium one, then a heavy finisher.", new[]
        {
            P(0, 200, 25, 55), P(300, 200, 25, 55), P(600, 200, 25, 55), P(933, 167, 65, 75), P(1600, 500, 100, 90, PulseShape.FadeOut),
        }),
        new("Charge up", "Builds up, then releases.", new[] { P(0, 600, 70, 50, PulseShape.FadeIn), P(600, 250, 100, 100, PulseShape.FadeOut) }),
        new("Engine", "Throbbing rumble for channelled skills.", new[] { P(0, 900, 55, 30, PulseShape.Throb) }),
        new("Swell", "Soft rise and fall.", new[] { P(0, 700, 60, 40, PulseShape.Bump) }),
    };

    public static List<VibrationPulse> Copy(Preset p) => p.Pulses.Select(x => x.Clone()).ToList();
}
