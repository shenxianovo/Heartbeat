using Microsoft.AspNetCore.WebUtilities;

namespace Heartbeat.Api;

internal static class ApiProblem
{
    internal const string Type = "about:blank";

    internal static IResult Create(int statusCode, string detail) => Results.Problem(
        type: Type,
        title: ReasonPhrases.GetReasonPhrase(statusCode),
        statusCode: statusCode,
        detail: detail);
}
