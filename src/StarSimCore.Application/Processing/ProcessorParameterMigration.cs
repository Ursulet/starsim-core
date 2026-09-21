namespace StarSimCore.Application.Processing;

public static class ProcessorParameterMigration
{
    public static IReadOnlyList<double> Upgrade(string processorId, IReadOnlyList<double> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentNullException.ThrowIfNull(values);

        if (processorId == BuiltInProcessors.WaveletId &&
            values.Count is WaveletMacroMapper.LegacyParameterCount or WaveletMacroMapper.PreviousParameterCount)
            return WaveletParameterMigration.Upgrade(values);

        if (processorId == BuiltInProcessors.MultiScaleSharpenId && values.Count == 4)
        {
            return
            [
                values[0] * 0.65,
                values[1],
                values[0] * 0.35,
                values[2],
                values[3],
            ];
        }

        if (processorId == BuiltInProcessors.RichardsonLucyId && values.Count == 5)
            return values.Take(4).ToArray();

        return values;
    }
}
