using System.Net;
using System.Text.Json;
using Orisumi.Observers.ApplicationIdentity;

namespace Orisumi.Integration.Tests;

public sealed class ApplicationModelTests
{
    [Test, Property("caseId", "application-identity")]
    public async Task SendsStructuredInputWithoutToolsAndReadsTheDecision()
    {
        using var handler = new ResponseHandler("""
            {"status":"completed","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"{\"kind\":\"new\",\"productId\":null,\"name\":\"QQ\"}"}]}]}
            """);
        using var http = new HttpClient(handler);
        var input = new IdentificationInput("macos", new("macos.bundle-id", "com.tencent.qq"),
            [[new("macos.bundle-id", "com.tencent.qq"), new("macos.executable-path", "/Applications/QQ")]], ["QQ"], []);
        var result = await new OpenAiApplicationModel(http, "test-key", "test-model").IdentifyAsync(input, TestContext.Current!.Execution.CancellationToken);
        await Assert.That(result).IsEqualTo(new IdentificationDecision("new", Name: "QQ"));
        var body = handler.Body;
        await Assert.That(body.GetProperty("model").GetString()).IsEqualTo("test-model");
        await Assert.That(body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean()).IsTrue();
        await Assert.That(body.TryGetProperty("tools", out _)).IsFalse();
        var sent = JsonSerializer.Deserialize<JsonElement>(body.GetProperty("input").GetString()!);
        await Assert.That(sent.GetProperty("applications")[0][1].GetProperty("value").GetString()).IsEqualTo("/Applications/QQ");
        await Assert.That(sent.GetProperty("identifier").TryGetProperty("isShared", out _)).IsFalse();
    }

    [Test, Property("caseId", "application-identity")]
    [Arguments("{}")]
    [Arguments("{\"status\":\"incomplete\",\"output\":[]}")]
    public async Task InvalidOrIncompleteResponseIsADiagnosticFailure(string response)
    {
        using var handler = new ResponseHandler(response);
        using var http = new HttpClient(handler);
        var model = new OpenAiApplicationModel(http, "test-key", "test-model");
        await Assert.That(async () => await model.IdentifyAsync(new("macos", new("macos.bundle-id", "test"), [], [], []),
            TestContext.Current!.Execution.CancellationToken)).Throws<InvalidDataException>();
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        internal JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
