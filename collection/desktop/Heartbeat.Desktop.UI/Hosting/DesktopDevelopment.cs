using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Desktop.UI.Hosting;

public static class DesktopDevelopment
{
    /// <summary>Runs under the bootstrap Profile lock, before Host.Start and Marketplace restoration.</summary>
    public static void PreparePackage(DesktopBootstrap bootstrap, IServiceProvider services)
    {
        if (bootstrap.DevelopmentPackageDirectory is not { } source) return;
        if (!bootstrap.IsDevelopment || bootstrap.UsesDefaultDirectory)
            throw new InvalidOperationException("Local Package preparation requires an independent development Profile.");
        var installed = services.GetRequiredService<CollectorPackageInstallations>().Install(source);
        var blueprint = installed.Package.Manifest.DefaultInstance
            ?? throw new PackageValidationException("Development Package requires a default Instance.");
        if (!Enum.TryParse<SubjectKind>(blueprint.SubjectKind, true, out var kind) || !Enum.IsDefined(kind))
            throw new PackageValidationException("Development Package has an invalid Subject kind.");
        var subject = services.GetRequiredService<ICollectorMarketplaceHostAdapter>().CreateSubject(kind);
        services.GetRequiredService<CollectorRuntime>().PrepareExternalHostPackage(installed.Package, subject);
    }
}
