using Microsoft.AspNetCore.WebUtilities;

namespace Heartbeat.Api;

internal static class ApiProblem
{
    internal const string Type = "about:blank";

    internal static int RuleStatus(string rule) => rule is "VERSION-6" or "VERSION-7" or "SCHEMA-5" or "HISTORY-7" or "VERSION-9" or "HISTORY-9" ? 409 : 400;

    internal static IResult Create(int statusCode, string detail, string? rule = null, int? index = null, Guid? versionId = null)
    {
        var extensions = new Dictionary<string, object?>();
        if (rule is not null) extensions["rule"] = rule;
        if (index is not null) extensions["index"] = index;
        if (versionId is not null) extensions["versionId"] = versionId;
        return Results.Problem(
        type: Type,
        title: ReasonPhrases.GetReasonPhrase(statusCode),
        statusCode: statusCode,
        detail: detail, extensions: extensions);
    }
}
