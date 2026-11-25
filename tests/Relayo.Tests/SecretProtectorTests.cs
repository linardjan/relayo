using System.Security.Cryptography;
using Relayo.Core.Security;
using Relayo.Core.Sites;

namespace Relayo.Tests;

public class SecretProtectorTests
{
    private readonly SecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Roundtrip_and_ciphertext_differs_each_time()
    {
        var a = _protector.Protect("site secret");
        var b = _protector.Protect("site secret");

        Assert.NotEqual(a, b);
        Assert.DoesNotContain("site secret", a);
        Assert.Equal("site secret", _protector.Unprotect(a));
    }

    [Fact]
    public void Tampered_value_is_rejected()
    {
        var bytes = Convert.FromBase64String(_protector.Protect("site secret"));
        bytes[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Another_key_cannot_decrypt()
    {
        var other = new SecretProtector(RandomNumberGenerator.GetBytes(32));

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(_protector.Protect("site secret")));
    }

    [Theory]
    [InlineData("http://LocalHost:5300/", "http://localhost:5300")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("https://example.com:443/path?x=1", "https://example.com")]
    public void Origins_are_normalized_like_browsers_send_them(string input, string expected)
    {
        Assert.Equal(expected, Site.NormalizeOrigin(input));
    }
}
