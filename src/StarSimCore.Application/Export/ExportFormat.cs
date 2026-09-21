namespace StarSimCore.Application.Export;

public enum ExportFormat
{
    Tiff16 = 1,
    Png16 = 2,
    Png8 = 3,
}

public sealed record ExportOptions(
    string DestinationPath,
    ExportFormat Format);

public sealed record ExportProgress(
    string Stage,
    double? Fraction,
    uint CurrentStep,
    uint TotalSteps);
