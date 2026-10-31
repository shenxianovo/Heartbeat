using Orisumi.Application.Management;
using Microsoft.AspNetCore.WebUtilities;

namespace Orisumi.Api;

internal static class ApiProblem
{
    internal static IResult Management(ManagementException error)
    {
        var extensions = new Dictionary<string, object?>();
        if (error.Code is { } code) extensions["code"] = code;
        if (error.LastLocalSequence is { } sequence) extensions["lastLocalSequence"] = sequence;
        return Results.Problem(type: Type, title: ReasonPhrases.GetReasonPhrase(error.Status),
            statusCode: error.Status, detail: error.Message, extensions: extensions);
    }

    internal const string Type = "about:blank";

    internal static int RuleStatus(string rule, bool conflict = false) => conflict || rule is "SCHEMA-5" or "HISTORY-7" or "HISTORY-9" ? 409 : 400;

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
