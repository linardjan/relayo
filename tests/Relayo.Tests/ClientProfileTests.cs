using Relayo.Core;
using Relayo.Core.Clients;

namespace Relayo.Tests;

public class ClientProfileTests
{
    private static Client NewClient() => Client.CreateIdentified(Guid.NewGuid(), "1001", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Profile_is_trimmed_and_empty_values_are_cleared()
    {
        var client = NewClient();
        client.SetProfile(" Анна Иванова ", "RU", " Директор ", " Звонить после 18:00 ", vip: true, spammer: false);
        Assert.Equal(("Анна Иванова", "ru", "Директор", "Звонить после 18:00", true, false),
            (client.DisplayName, client.Locale, client.Position, client.Note, client.IsVip, client.IsSpammer));

        client.SetProfile("", "", " ", null, vip: false, spammer: true);
        Assert.Equal((null, null, null, null, false, true),
            ((string?)client.DisplayName, client.Locale, client.Position, client.Note, client.IsVip, client.IsSpammer));
    }

    [Theory]
    [InlineData("russian")]
    [InlineData("r")]
    [InlineData("ru_RU")]
    public void Bad_language_is_rejected(string locale) =>
        Assert.Throws<DomainException>(() => NewClient().SetProfile(null, locale, null, null, false, false));

    [Fact]
    public void Too_long_note_is_rejected() =>
        Assert.Throws<DomainException>(() =>
            NewClient().SetProfile(null, null, null, new string('x', Client.NoteMaxLength + 1), false, false));

    [Fact]
    public void Messenger_name_does_not_overwrite_the_name_set_by_staff()
    {
        var client = NewClient();
        client.SetProfile("Анна", null, null, null, false, false);
        client.SetDisplayNameIfEmpty("anna_tg");
        Assert.Equal("Анна", client.DisplayName);
    }
}
