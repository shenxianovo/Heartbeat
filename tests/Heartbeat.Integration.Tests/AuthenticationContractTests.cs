using System.Security.Claims;
using System.Text;
using Heartbeat.Api.Authentication;

namespace Heartbeat.Integration.Tests;

public sealed class AuthenticationContractTests
{
    [Fact]
    public void TokenSelectorRecognizesOidcAccessTokenType()
    {
        var token = TokenWithHeader("{\"typ\":\"at+jwt\",\"alg\":\"RS256\"}");

        Assert.True(JwtTypeSniffer.IsOidcAccessToken(token));
        Assert.False(JwtTypeSniffer.IsOidcAccessToken(TokenWithHeader("{\"typ\":\"JWT\"}")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"header\"")]
    [InlineData("null")]
    [InlineData("{\"typ\":123}")]
    [InlineData("{\"typ\":{}}")]
    public void TokenSelectorRejectsValidJsonWithInvalidHeaderShape(string header)
    {
        Assert.False(JwtTypeSniffer.IsOidcAccessToken(TokenWithHeader(header)));
    }

    [Fact]
    public void OwnerIdRequiresUuidSubject()
    {
        var expected = Guid.Parse("019d9026-def4-74db-bf9a-f854c16a993e");
        var valid = Principal(expected.ToString());
        var invalid = Principal("not-a-uuid");

        Assert.True(OwnerClaims.TryGetOwnerId(valid, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(OwnerClaims.TryGetOwnerId(invalid, out _));
        Assert.False(OwnerClaims.TryGetOwnerId(new ClaimsPrincipal(), out _));
    }

    private static ClaimsPrincipal Principal(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)], "Test"));

    private static string TokenWithHeader(string header)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(header))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{encoded}.payload.signature";
    }
}
