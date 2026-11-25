using Microsoft.EntityFrameworkCore;
using Relayo.Core.Auth;
using Relayo.Core.Automation;
using Relayo.Core.Channels;
using Relayo.Core.Clients;
using Relayo.Core.Conversations;
using Relayo.Core.Directory;
using Relayo.Core.Files;
using Relayo.Core.Sites;
using Relayo.Core.Users;

namespace Relayo.Core.Data;

public sealed class RelayoDbContext(DbContextOptions<RelayoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<AgentGroup> AgentGroups => Set<AgentGroup>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<SiteKey> SiteKeys => Set<SiteKey>();

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<WidgetVersion> WidgetVersions => Set<WidgetVersion>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<ClientIdentity> ClientIdentities => Set<ClientIdentity>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Chat> Chats => Set<Chat>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<TelegramBot> TelegramBots => Set<TelegramBot>();

    public DbSet<ChannelLinkToken> ChannelLinkTokens => Set<ChannelLinkToken>();

    public DbSet<LdapSettings> LdapSettings => Set<LdapSettings>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<RuleRevision> RuleRevisions => Set<RuleRevision>();

    public DbSet<RuleExecution> RuleExecutions => Set<RuleExecution>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayoDbContext).Assembly);
}
