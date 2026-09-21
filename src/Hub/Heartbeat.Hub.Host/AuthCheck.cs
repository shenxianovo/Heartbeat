using System.Text.Json;

namespace Heartbeat.Hub.Host;

public static class AuthCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var authValue = Environment.GetEnvironmentVariable("Hub__AuthUrl");
            var authUrl = new Uri(string.IsNullOrWhiteSpace(authValue)
                ? "https://auth.shenxianovo.com"
                : authValue.Trim(), UriKind.Absolute);
            var apiKey = Environment.GetEnvironmentVariable("Hub__ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Missing Hub:ApiKey.");
            }

            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 2_097_152,
            };
            using var provider = new ApiKeyTokenProvider(httpClient, authUrl, apiKey.Trim());
            var token = await provider.GetTokenAsync();
            if (token is null)
            {
                throw new InvalidDataException("The API key exchange was not confirmed.");
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(new { ownerId = token.OwnerId }));
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Hub authentication check failed.");
            return 1;
        }
    }
}
