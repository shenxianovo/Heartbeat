using Heartbeat.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Heartbeat.Server.Filters;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireHeartbeatProtocolAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var version = context.HttpContext.Request.Headers[HeartbeatProtocol.VersionHeader].FirstOrDefault();
        if (!string.Equals(version, HeartbeatProtocol.RequiredVersion, StringComparison.Ordinal))
        {
            context.Result = UpgradeRequiredResult.Create(context.HttpContext.Response);
            return Task.CompletedTask;
        }

        return next();
    }
}

public static class UpgradeRequiredResult
{
    public static ObjectResult Create(HttpResponse response, string? message = null)
    {
        response.Headers.Upgrade = $"Heartbeat/{HeartbeatProtocol.RequiredVersion}";
        return new(new
        {
            code = HeartbeatProtocol.UpdateRequiredCode,
            requiredVersion = HeartbeatProtocol.RequiredVersion,
            message = message ?? "This Heartbeat client uses an incompatible ingest protocol. Update the client and retry."
        })
        {
            StatusCode = StatusCodes.Status426UpgradeRequired
        };
    }
}
