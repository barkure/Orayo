namespace Orayo.Services;

internal static class EchSettings
{
    public const string Half = "half";
    public const string Full = "full";

    public static string NormalizeForceQuery(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        return value is Half or Full ? value : string.Empty;
    }
}

