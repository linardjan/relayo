using Relayo.Core.Automation;
using Relayo.Core.Conversations;

namespace Relayo.Tests;

public class RuleMatcherTests
{
    [Theory]
    [InlineData("Где мои депозиты?", "депозит", true)]
    [InlineData("Проблема с оплатой", "оплата", true)]
    [InlineData("оплаты нет", "оплату", true)]
    [InlineData("Хочу картошку", "карта", false)]
    [InlineData("I want refunds", "refund", true)]
    [InlineData("Где мой депозит", "вывод средств", false)]
    [InlineData("Не пришёл вывод средств", "вывод средства", true)]
    public void Contains_with_word_forms(string text, string pattern, bool expected) =>
        Assert.Equal(expected, RuleMatcher.MatchesText("contains", pattern, text, useWordForms: true, ignoreHtml: true));

    [Fact]
    public void Contains_without_word_forms_is_case_insensitive_substring()
    {
        Assert.True(RuleMatcher.MatchesText("contains", "ДЕПОЗИТ", "где мой депозит?", false, true));
        Assert.False(RuleMatcher.MatchesText("contains", "депозиты", "где мой депозит?", false, true));
        Assert.True(RuleMatcher.MatchesText("not_contains", "refund", "hello", false, true));
    }

    [Fact]
    public void Html_is_ignored_when_asked()
    {
        Assert.True(RuleMatcher.MatchesText("eq", "hello world", "<b>hello</b> world", false, ignoreHtml: true));
        Assert.False(RuleMatcher.MatchesText("eq", "hello world", "<b>hello</b> world", false, ignoreHtml: false));
    }

    [Fact]
    public void Regex_with_timeout_and_case()
    {
        Assert.True(RuleMatcher.MatchesText("regex", "(оплат|refund)", "Нет ОПЛАТЫ", false, true));
        Assert.False(RuleMatcher.MatchesText("regex", "^a+$", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", false, true));
    }

    [Fact]
    public void All_and_any_groups()
    {
        var site = Guid.NewGuid();
        var ctx = new RuleContext(Channel.Telegram, "telegram:x", site, TicketStatus.New, null, false, "hi", "ru-RU");
        var rule = new Rule("r", 0, DateTimeOffset.UtcNow);
        rule.Update("r", null, true, false, false, true, false, new RuleDefinition([], [],
            [new RuleCondition("channel_type", "eq", "Telegram"), new RuleCondition("assignee", "eq", "none")],
            [new RuleCondition("client_locale", "eq", "en"), new RuleCondition("client_locale", "eq", "ru")],
            [new RuleAction("unassign")]), DateTimeOffset.UtcNow);
        Assert.True(RuleMatcher.Matches(rule, ctx));
        Assert.False(RuleMatcher.Matches(rule, ctx with { AssigneeId = Guid.NewGuid() }));
        Assert.False(RuleMatcher.Matches(rule, ctx with { ClientLocale = "de" }));
    }

    [Fact]
    public void Variables_are_rendered() =>
        Assert.Equal("Hi Ivan, ticket #42 — Anna", RuleMatcher.Render("Hi {client.name}, ticket #{ticket.number} — {operator.alias}", "Ivan", "Anna", 42));
}
