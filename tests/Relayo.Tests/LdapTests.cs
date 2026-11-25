using Relayo.Core.Directory;

namespace Relayo.Tests;

public class LdapTests
{
    // Логин подставляется в DN для входа: спецсимволы не должны менять путь к записи в AD.
    [Theory]
    [InlineData("ivanov", "ivanov")]
    [InlineData("ivanov,OU=Admins", @"ivanov\,OU\=Admins")]
    [InlineData(@"a+b""c\d<e>f;g", @"a\+b\""c\\d\<e\>f\;g")]
    [InlineData("#start", @"\#start")]
    [InlineData(" padded ", @"\ padded\ ")]
    public void Login_is_escaped_in_dn(string login, string expected) =>
        Assert.Equal(expected, LdapDirectory.EscapeDnValue(login));
}
