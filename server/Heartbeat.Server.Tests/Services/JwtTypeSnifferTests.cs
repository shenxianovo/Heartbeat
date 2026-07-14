using System.Text;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services
{
    public class JwtTypeSnifferTests
    {
        private static string MakeToken(string headerJson) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')
            + ".payload.signature";

        [Fact]
        public void OidcAccessToken_AtJwtTyp_IsDetected()
        {
            var token = MakeToken("""{"alg":"RS256","typ":"at+jwt"}""");
            Assert.True(JwtTypeSniffer.IsOidcAccessToken(token));
        }

        [Fact]
        public void SessionToken_PlainJwtTyp_IsNotOidc()
        {
            var token = MakeToken("""{"alg":"RS256","typ":"JWT"}""");
            Assert.False(JwtTypeSniffer.IsOidcAccessToken(token));
        }

        [Fact]
        public void HeaderWithoutTyp_IsNotOidc()
        {
            var token = MakeToken("""{"alg":"RS256"}""");
            Assert.False(JwtTypeSniffer.IsOidcAccessToken(token));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("not-base64!!.payload.sig")]
        [InlineData(".starts-with-dot")]
        public void MalformedInput_IsNotOidc(string? token)
        {
            Assert.False(JwtTypeSniffer.IsOidcAccessToken(token));
        }
    }
}
