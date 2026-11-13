// SPDX-License-Identifier: GPL-3.0-only
namespace AapHeadtracker;

internal sealed record AirPodsPreset(string Init, string Start, string Delay)
{
    public static AirPodsPreset? Resolve(string? model) => model switch
    {
        null => null,
        "airpods5" => new("librepods", "airpods5", "750"),
        "pro2" => new("rtbuddy-aacp", "devmotion6", "0"),
        "max2" => new("rtbuddy", "max2", "0"),
        "airpods3" or "airpods4" or "pro1" or "pro3" or "max1" => new("librepods", "alternate", "750"),
        _ => throw new ArgumentException($"Unknown --model value '{model}'. See --help for supported presets.")
    };
}
