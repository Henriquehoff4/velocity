namespace Velocity.Engine.Core;

public sealed record SpeedProfile
{
    public string Mode { get; init; } = "auto";
    public int MaxConnections { get; init; } = 16;
    public int StartConnections { get; init; } = 4;
    public int? FixedConnections { get; init; }
    public long MinFragment { get; init; } = 1024 * 1024;
    public int BufferBytes { get; init; } = 256 * 1024;
    public long LimitBps { get; init; }
    public double GainThreshold { get; init; } = 0.06;
    public int MaxStep { get; init; } = 4;
    public int SettleMs { get; init; } = 600;
    public bool MultiLink { get; init; } = true;
    public bool Verify { get; init; } = true;

    public static SpeedProfile For(string mode, AdvancedOptions advanced) => mode switch
    {
        "turbo" => new SpeedProfile { Mode = mode, MaxConnections = 32, StartConnections = 8, MaxStep = 8, GainThreshold = 0.03, SettleMs = 500, BufferBytes = 1024 * 1024, MinFragment = 512 * 1024 },
        "eco" => new SpeedProfile { Mode = mode, MaxConnections = 2, StartConnections = 1, MaxStep = 1, GainThreshold = 0.15, SettleMs = 3000, BufferBytes = 64 * 1024, MinFragment = 4 * 1024 * 1024, LimitBps = advanced.EcoLimitBps, MultiLink = false },
        "advanced" => new SpeedProfile
        {
            Mode = mode,
            MaxConnections = Math.Clamp(advanced.MaxConnections, 1, 64),
            StartConnections = Math.Clamp(advanced.MaxConnections, 1, 4),
            FixedConnections = advanced.Adaptive ? null : Math.Clamp(advanced.MaxConnections, 1, 64),
            MinFragment = Math.Clamp(advanced.MinFragmentKiB, 64, 1024 * 1024) * 1024L,
            BufferBytes = Math.Clamp(advanced.BufferKiB, 64, 4096) * 1024,
            LimitBps = advanced.LimitBps,
            MultiLink = advanced.MultiLink,
            Verify = advanced.Verify,
        },
        _ => new SpeedProfile { Mode = "auto" },
    };
}

public sealed record AdvancedOptions
{
    public int MaxConnections { get; init; } = 16;
    public int MinFragmentKiB { get; init; } = 1024;
    public int BufferKiB { get; init; } = 256;
    public long LimitBps { get; init; }
    public long EcoLimitBps { get; init; } = 5 * 1024 * 1024;
    public bool Adaptive { get; init; } = true;
    public bool MultiLink { get; init; } = true;
    public bool Verify { get; init; } = true;
    public bool ForceSharedLinks { get; init; }
    public string[] AllowedLinks { get; init; } = Array.Empty<string>();
}
