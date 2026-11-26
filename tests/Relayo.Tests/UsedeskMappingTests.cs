using Relayo.Core.Channels;
using Relayo.Core.Sites;
using Relayo.Core.Users;
using Relayo.Internal.Usedesk;

namespace Relayo.Tests;

public class UsedeskMappingTests
{
    [Theory]
    [InlineData("admin", UserRole.Admin)]
    [InlineData("employee", UserRole.Operator)]
    [InlineData("support", null)]
    [InlineData("bot", null)]
    public void Roles(string role, UserRole? expected) => Assert.Equal(expected, UsedeskMapping.Role(role));

    [Fact]
    public void Guid_as_text_or_base64_of_ad_bytes()
    {
        var guid = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        Assert.Equal(guid, UsedeskMapping.ParseGuid("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        Assert.Equal(guid, UsedeskMapping.ParseGuid("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}"));
        Assert.Equal(guid, UsedeskMapping.ParseGuid(Convert.ToBase64String(guid.ToByteArray())));
        Assert.Null(UsedeskMapping.ParseGuid(""));
        Assert.Null(UsedeskMapping.ParseGuid("not-a-guid"));
        Assert.Null(UsedeskMapping.ParseGuid(Guid.Empty.ToString()));
    }

    [Theory]
    [InlineData("ssl", false, MailSecurity.Ssl)]
    [InlineData("tls", false, MailSecurity.StartTls)]
    [InlineData("starttls", false, MailSecurity.StartTls)]
    [InlineData("", false, MailSecurity.None)]
    [InlineData("", true, MailSecurity.None)]
    public void Mail_security(string encrypted, bool unencrypted, MailSecurity expected) =>
        Assert.Equal(expected, UsedeskMapping.Security(encrypted, unencrypted));

    private static UsedeskChannel Mail(string incoming = "external", string? imapPassword = "fake-imap-password") =>
        new(10, "Support mail", "email", null, false, incoming, "imap.example.test", 993, "support@example.test", imapPassword, "ssl",
            "support@example.test", null, "smtp.example.test", null, "support@example.test", "fake-smtp-password", "tls", false, true);

    [Fact]
    public void Email_channel_maps_both_connections()
    {
        var site = Guid.NewGuid();
        var (input, problem) = UsedeskMapping.EmailChannel(Mail(), site);
        Assert.Null(problem);
        Assert.NotNull(input);
        Assert.Equal(site, input.SiteId);
        Assert.Equal(EmailQuoting.SendToClient, input.Quoting);
        Assert.Equal(("support@example.test", "imap.example.test", 993, MailSecurity.Ssl, "fake-imap-password"),
            (input.Imap.Email, input.Imap.Host, input.Imap.Port, input.Imap.Security, input.Imap.Password));
        // Порт SMTP не задан — по шифрованию.
        Assert.Equal((587, MailSecurity.StartTls), (input.Smtp.Port, input.Smtp.Security));
    }

    [Fact]
    public void Email_channel_problems()
    {
        Assert.Contains("not supported", UsedeskMapping.EmailChannel(Mail(incoming: "gmail"), Guid.Empty).Problem);
        Assert.Contains("No password", UsedeskMapping.EmailChannel(Mail(imapPassword: null), Guid.Empty).Problem);
        Assert.Contains("encrypted", UsedeskMapping.EmailChannel(Mail(imapPassword: "eyJpdiI6IkFBQUEiLCJ2YWx1ZSI6IkJCQiJ9"), Guid.Empty).Problem);
    }

    [Theory]
    [InlineData("https://t.me/relayo_fake_bot", "relayo_fake_bot")]
    [InlineData("t.me/relayo_fake_bot?start=x", "relayo_fake_bot")]
    [InlineData("@relayo_fake_bot", "relayo_fake_bot")]
    [InlineData("https://example.com", null)]
    public void Telegram_username(string link, string? expected) => Assert.Equal(expected, UsedeskMapping.TelegramUsername(link));

    private static UsedeskWidget Widget() =>
        new(1, 11, "Виджет магазина", "Напишите нам", "#32a8d9", true, 1050, "RU", "<b>Нужна&nbsp;помощь?</b>", "#fff", "rgb(255, 112, 72)",
            40, "left", "#FF7048", "Поддержка", 1, false, false, true, false, "[]");

    [Fact]
    public void Widget_settings_and_items()
    {
        var messenger = new UsedeskMessenger(11,
            """[{"type":"CHAT_CALLBACK_FORM"},{"type":"TG","externalLink":"https://t.me/relayo_fake_bot"},{"type":"WHATSAPP","externalLink":"+7 999 000-00-00"},{"type":"VK"},{"type":"VIBER"}]""",
            "", "https://vk.com/example", "viber://chat?number=79990000000", "", null, null);
        var m = UsedeskMapping.Widget(Widget(), messenger, IdentificationMode.Direct);

        Assert.Equal(("Виджет магазина", "Напишите нам", "#32a8d9", WidgetPosition.Left, "ru", IdentificationMode.Direct),
            (m.Input.Name, m.Input.Title, m.Input.AccentColor, m.Input.Position, m.Input.DefaultLocale, m.Input.IdentificationMode));
        var o = m.Input.Options!;
        Assert.Equal(("Нужна помощь?", WidgetTooltipMode.Always, "#ffffff", "#ff7048", 32), (o.TooltipText, o.TooltipMode, o.TooltipColor, o.TooltipBackground, o.TooltipTextSize));
        Assert.Equal("#ff7048", o.ButtonColor);
        Assert.Equal("Поддержка", o.OperatorName);
        Assert.Equal("relayo_fake_bot", m.TelegramUsername);
        Assert.Equal("79990000000", m.WhatsAppPhone);
        // Чат и VK; Viber — не http-ссылка и пропускается с пометкой.
        Assert.Equal([LauncherItemType.Chat, LauncherItemType.Link], o.Items.Select(i => i.Type));
        Assert.Equal("https://vk.com/example", o.Items[1].Url);
        Assert.Contains(m.Notes, n => n.Contains("VIBER"));
        Assert.Contains(m.Notes, n => n.Contains("Callback"));
        // Настройки проходят проверку Relayo.
        o.Normalize(hasTelegramBot: true, hasWhatsApp: true);
    }

    [Fact]
    public void Widget_items_bad_json_keeps_the_chat()
    {
        var m = UsedeskMapping.Widget(Widget(), new UsedeskMessenger(11, "{not json", null, null, null, null, null, null), IdentificationMode.SignedToken);
        Assert.Equal([LauncherItemType.Chat], m.Input.Options!.Items.Select(i => i.Type));
        Assert.Contains(m.Notes, n => n.Contains("active_json"));
    }

    [Fact]
    public void Widget_items_as_object()
    {
        var m = UsedeskMapping.Widget(Widget(), new UsedeskMessenger(11, """{"chat":true,"vk":true,"facebook":false}""", null,
            "https://vk.com/example", null, null, null, null), IdentificationMode.SignedToken);
        Assert.Equal([LauncherItemType.Chat, LauncherItemType.Link], m.Input.Options!.Items.Select(i => i.Type));
    }

    [Theory]
    [InlineData("#ABC", "#aabbcc")]
    [InlineData("rgba(1, 2, 3, 0.5)", "#010203")]
    [InlineData("red", null)]
    public void Colors(string value, string? expected) => Assert.Equal(expected, UsedeskMapping.Color(value));
}
