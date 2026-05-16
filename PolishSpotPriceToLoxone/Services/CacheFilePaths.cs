namespace PolishSpotPriceToLoxone.Services;

internal static class CacheFilePaths
{
    public static string WritablePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        var isAzureAppService = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        if (isAzureAppService && !string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, configuredPath);
        }

        return configuredPath;
    }

    public static IEnumerable<string> ReadCandidates(string configuredPath, string writablePath)
    {
        yield return writablePath;

        if (!Path.IsPathRooted(configuredPath))
        {
            yield return configuredPath;
            yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        }
        else if (!string.Equals(configuredPath, writablePath, StringComparison.OrdinalIgnoreCase))
        {
            yield return configuredPath;
        }
    }
}
