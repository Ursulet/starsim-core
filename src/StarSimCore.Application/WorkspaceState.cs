namespace StarSimCore.Application;

public sealed class WorkspaceState
{
    public bool IsExpertMode { get; set; }

    public string SelectedTarget { get; set; } = "Default";

    public string SelectedPreset { get; set; } = "Default";

    public string SelectedColorPreset { get; set; } = "Natural";

    public double PresetStrength { get; set; }

    public double Detail { get; set; } = 50;

    public double NoiseReduction { get; set; }

    public double Color { get; set; } = 50;

    public double Brightness { get; set; }

    public double Contrast { get; set; }
}
