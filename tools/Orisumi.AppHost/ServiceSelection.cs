namespace Orisumi.Dev;

internal static class ServiceSelection
{
    public static HashSet<string> Resolve(IEnumerable<string> requested)
    {
        var services = requested.ToHashSet(StringComparer.Ordinal);
        if (services.Count == 0) services.UnionWith(["api", "docs", "web", "application-identity"]);
        if (services.Any(service => service is not ("api" or "docs" or "web" or "application-identity")))
            throw new ArgumentException("Services must be api, docs, web or application-identity.");
        if (services.Contains("web") || services.Contains("application-identity")) services.Add("api");
        if (services.Contains("api")) services.UnionWith(["db", "migrate"]);
        services.Add("nginx");
        return services;
    }
}
