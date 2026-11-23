using System.Collections.Generic;
using System.Linq;

namespace XIVibration;

public static class Presets
{
    public sealed record Preset(string Name, string Description, VibrationPulse[] Pulses);

    private static VibrationPulse P(int delay, int duration, int strong, int weak, PulseShape shape = PulseShape.Constant)
        => new() { DelayMs = delay, DurationMs = duration, Strong = strong, Weak = weak, Shape = shape };

    /// <summary>
    /// Approximate motor levels for the C120 wave types used by the Classic DRG mod.
    /// The game does not expose what each type means; from how the mod uses them
    /// (2 for small hits, 3 for the second-to-last hit, 1 for the finisher)
    /// the guess is 2 = light, 3 = medium, 1 = heavy.
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
        new("Charge up", "Builds up, then releases.", new[] { P(0, 600, 70, 50, PulseShape.FadeIn), P(600, 250, 100, 100, PulseShape.FadeOut) }),
        new("Engine", "Throbbing rumble for channelled skills.", new[] { P(0, 900, 55, 30, PulseShape.Throb) }),
        new("Swell", "Soft rise and fall.", new[] { P(0, 700, 60, 40, PulseShape.Bump) }),
        new("DRG combo (Spiral Blow)", "Timing taken from the C120 entries in the Classic DRG mod's ws_s20.tmb.",
            TmbImport.ToPulses(new TmbImport.C120Entry[]
            {
                new(29, 11, 6, 0, 2), new(30, 20, 6, 0, 2), new(31, 29, 6, 0, 2), new(32, 39, 5, 0, 3), new(33, 59, 15, 0, 1),
            }).ToArray()),
    };

    public static List<VibrationPulse> Copy(Preset p) => p.Pulses.Select(x => x.Clone()).ToList();
}
