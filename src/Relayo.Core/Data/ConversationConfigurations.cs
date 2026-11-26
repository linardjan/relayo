using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relayo.Core.Automation;
using Relayo.Core.Channels;
using Relayo.Core.Clients;
using Relayo.Core.Conversations;
using Relayo.Core.Files;
using Relayo.Core.Sites;

namespace Relayo.Core.Data;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> b)
    {
        b.HasKey(s => s.Id);
        b.Property(s => s.Code).HasMaxLength(Site.CodeMaxLength);
        b.HasIndex(s => s.Code).IsUnique();
        b.Property(s => s.Name).HasMaxLength(Site.NameMaxLength);
    }
}

internal sealed class SiteKeyConfiguration : IEntityTypeConfiguration<SiteKey>
{
    public void Configure(EntityTypeBuilder<SiteKey> b)
    {
        b.HasKey(k => k.Id);
        b.Property(k => k.KeyId).HasMaxLength(SiteKey.KeyIdMaxLength).IsCaseSensitive();
        b.HasIndex(k => k.KeyId).IsUnique();
        b.HasOne(k => k.Site).WithMany().HasForeignKey(k => k.SiteId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WidgetConfiguration : IEntityTypeConfiguration<Widget>
{
    internal static readonly JsonSerializerOptions WidgetOptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void Configure(EntityTypeBuilder<Widget> b)
    {
        b.HasKey(w => w.Id);
        b.Property(w => w.Name).HasMaxLength(200);
        b.Property(w => w.IdentificationMode).HasConversion<string>().HasMaxLength(32);
        b.Property(w => w.Position).HasConversion<string>().HasMaxLength(16);
        b.Property(w => w.Title).HasMaxLength(100);
        b.Property(w => w.Greeting).HasMaxLength(1000);
        b.Property(w => w.AccentColor).HasMaxLength(16);
        b.Property(w => w.DefaultLocale).HasMaxLength(16);
        b.HasOne(w => w.Site).WithMany().HasForeignKey(w => w.SiteId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TelegramBot>().WithMany().HasForeignKey(w => w.TelegramBotId).OnDelete(DeleteBehavior.SetNull);
        b.Property(w => w.WhatsAppChannelId).HasColumnName("whatsapp_channel_id");
        b.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(w => w.WhatsAppChannelId).OnDelete(DeleteBehavior.SetNull);
        b.Property(w => w.PublicKey).HasMaxLength(Widget.PublicKeyLength).IsCaseSensitive();
        // Внешний вид и поведение — один документ: читается и меняется только вместе с виджетом.
        b.Property(w => w.Options)
            .HasColumnType("nvarchar(max)")
            .HasConversion(
                o => JsonSerializer.Serialize(o, WidgetOptionsJson),
                s => JsonSerializer.Deserialize<WidgetOptions>(s, WidgetOptionsJson) ?? WidgetOptions.Default,
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<WidgetOptions>(
                    (x, y) => JsonSerializer.Serialize(x, WidgetOptionsJson) == JsonSerializer.Serialize(y, WidgetOptionsJson),
                    o => JsonSerializer.Serialize(o, WidgetOptionsJson).GetHashCode(),
                    o => o));
        b.HasIndex(w => w.PublicKey).IsUnique();
        b.Ignore(w => w.HasUnpublishedChanges);
    }
}

internal sealed class WidgetVersionConfiguration : IEntityTypeConfiguration<WidgetVersion>
{
    public void Configure(EntityTypeBuilder<WidgetVersion> b)
    {
        b.HasKey(v => v.Id);
        b.HasIndex(v => new { v.WidgetId, v.Version }).IsUnique();
        b.Property(v => v.SnapshotJson).HasColumnType("nvarchar(max)");
        b.Property(v => v.PublishedBy).HasMaxLength(Users.User.FullNameMaxLength);
        b.Property(v => v.Note).HasMaxLength(200);
        b.HasOne<Widget>().WithMany().HasForeignKey(v => v.WidgetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> b)
    {
        b.HasKey(c => c.Id);
        b.Property(c => c.ExternalClientId).HasMaxLength(Client.ExternalIdMaxLength).IsCaseSensitive();
        b.Property(c => c.Locale).HasMaxLength(16);
        b.Property(c => c.DisplayName).HasMaxLength(Client.DisplayNameMaxLength);
        b.Ignore(c => c.IsAnonymous);
        // У анонимных клиентов clientId пустой, уникальность только для заполненных.
        b.HasIndex(c => new { c.SiteId, c.ExternalClientId }).IsUnique()
            .HasFilter("external_client_id IS NOT NULL");
        b.HasIndex(c => c.UsedeskId).IsUnique().HasFilter("usedesk_id IS NOT NULL");
        b.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClientIdentityConfiguration : IEntityTypeConfiguration<ClientIdentity>
{
    public void Configure(EntityTypeBuilder<ClientIdentity> b)
    {
        b.HasKey(i => i.Id);
        b.Property(i => i.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.ExternalId).HasMaxLength(ClientIdentity.ExternalIdMaxLength).IsCaseSensitive();
        b.Property(i => i.DisplayName).HasMaxLength(ClientIdentity.DisplayNameMaxLength);
        b.HasIndex(i => new { i.Type, i.ExternalId }).IsUnique();
        b.HasIndex(i => i.ClientId);
        b.HasIndex(i => i.LinkedFromIdentityId);
        b.HasOne(i => i.Client).WithMany().HasForeignKey(i => i.ClientId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.HasKey(t => t.Id);
        b.Property(t => t.Number).UseIdentityColumn();
        b.HasIndex(t => t.Number).IsUnique();
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.LastMessagePreview).HasMaxLength(Ticket.PreviewMaxLength);
        b.Property(t => t.LastClientChannel).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.Subject).HasMaxLength(Ticket.SubjectMaxLength);
        b.Property(t => t.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(t => t.SourceChannel).HasMaxLength(Ticket.SourceChannelMaxLength);
        b.HasOne<Users.AgentGroup>().WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.SetNull);
        b.Ignore(t => t.IsOpen);
        b.HasIndex(t => new { t.ClientId, t.Status });
        b.HasIndex(t => new { t.Status, t.LastMessageAt });
        b.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(t => t.Chats).WithOne(c => c.Ticket).HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChatConfiguration : IEntityTypeConfiguration<Chat>
{
    public void Configure(EntityTypeBuilder<Chat> b)
    {
        b.HasKey(c => c.Id);
        b.HasIndex(c => new { c.ClientId, c.LastMessageAt });
        b.HasIndex(c => c.UsedeskId).IsUnique().HasFilter("usedesk_id IS NOT NULL");
        b.Property(c => c.PageUrl).HasMaxLength(Chat.PageUrlMaxLength);
        b.HasOne<Client>().WithMany().HasForeignKey(c => c.ClientId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        // Таблица секционирована по created_at, см. миграцию и MessagePartitions.
        // Ключ начинается с даты: так кластерный индекс растёт по порядку, а не вразброс по случайным id.
        b.HasKey(m => new { m.CreatedAt, m.Id });
        b.HasIndex(m => m.Id);
        b.Property(m => m.Author).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.Channel).HasConversion<string>().HasMaxLength(16);
        b.Property(m => m.Text).HasMaxLength(Message.TextMaxLength);
        b.Property(m => m.ClientMessageId).HasMaxLength(Message.ClientMessageIdMaxLength).IsCaseSensitive();
        b.Ignore(m => m.IsAutomated);
        b.Ignore(m => m.IsInternal);
        b.Property(m => m.Visibility).HasConversion<string>().HasMaxLength(16);
        // Повторный импорт из Usedesk. Таблица секционирована, поэтому индекс с датой и не уникальный:
        // уникальность проверяет импорт.
        b.HasIndex(m => new { m.UsedeskId, m.CreatedAt }).HasFilter("usedesk_id IS NOT NULL");
        b.HasIndex(m => new { m.ChatId, m.CreatedAt });
        b.HasIndex(m => new { m.VisitorIdentityId, m.CreatedAt });
        // Есть ли у сотрудника переписка (нельзя удалить учётку с историей).
        b.HasIndex(m => m.OperatorId).HasFilter("operator_id IS NOT NULL");
        b.HasOne<Chat>().WithMany().HasForeignKey(m => m.ChatId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> b)
    {
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.FileName).HasMaxLength(AttachmentPolicy.FileNameMaxLength);
        b.Property(a => a.ContentType).HasMaxLength(Attachment.ContentTypeMaxLength);
        b.Property(a => a.StorageKey).HasMaxLength(Attachment.StorageKeyMaxLength).IsCaseSensitive();
        b.HasIndex(a => new { a.ChatId, a.CreatedAt });
        b.HasIndex(a => a.MessageId);
        b.HasOne<Chat>().WithMany().HasForeignKey(a => a.ChatId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TelegramBotConfiguration : IEntityTypeConfiguration<TelegramBot>
{
    public void Configure(EntityTypeBuilder<TelegramBot> b)
    {
        b.HasKey(t => t.Id);
        b.Property(t => t.Name).HasMaxLength(TelegramBot.NameMaxLength);
        b.Property(t => t.Username).HasMaxLength(TelegramBot.UsernameMaxLength);
        b.HasIndex(t => t.TelegramId).IsUnique();
        b.HasOne<Site>().WithMany().HasForeignKey(t => t.SiteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WhatsAppChannelConfiguration : IEntityTypeConfiguration<WhatsAppChannel>
{
    public void Configure(EntityTypeBuilder<WhatsAppChannel> b)
    {
        b.ToTable("whatsapp_channels");
        b.HasKey(c => c.Id);
        b.Property(c => c.Name).HasMaxLength(WhatsAppChannel.NameMaxLength);
        b.Property(c => c.Mode).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.PhoneNumber).HasMaxLength(WhatsAppChannel.PhoneMaxLength).IsCaseSensitive();
        b.Property(c => c.AccountName).HasMaxLength(WhatsAppChannel.AccountNameMaxLength);
        // Не уникален: один номер можно подключить по QR, пока где-то остался канал «только ссылка» с ним же.
        b.HasIndex(c => c.PhoneNumber);
        b.Ignore(c => c.CanRedirect);
        b.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WhatsAppLinkCodeConfiguration : IEntityTypeConfiguration<WhatsAppLinkCode>
{
    public void Configure(EntityTypeBuilder<WhatsAppLinkCode> b)
    {
        b.ToTable("whatsapp_link_codes");
        b.HasKey(c => c.Id);
        b.Property(c => c.CodeHash).HasMaxLength(64);
        b.HasIndex(c => c.CodeHash).IsUnique();
        b.HasIndex(c => c.ExpiresAt);
        b.HasOne<WhatsAppChannel>().WithMany().HasForeignKey(c => c.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ClientIdentity>().WithMany().HasForeignKey(c => c.VisitorIdentityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChannelLinkTokenConfiguration : IEntityTypeConfiguration<ChannelLinkToken>
{
    public void Configure(EntityTypeBuilder<ChannelLinkToken> b)
    {
        b.HasKey(t => t.Id);
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.ExpiresAt);
        b.HasOne<TelegramBot>().WithMany().HasForeignKey(t => t.BotId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ClientIdentity>().WithMany().HasForeignKey(t => t.VisitorIdentityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<Rule> b)
    {
        b.HasKey(r => r.Id);
        b.Property(r => r.Name).HasMaxLength(Rule.NameMaxLength);
        b.Property(r => r.Description).HasMaxLength(Rule.DescriptionMaxLength);
        b.HasIndex(r => r.Position);
        // События, условия и действия — один документ: читаются и меняются только вместе.
        b.Property(r => r.Definition)
            .HasColumnType("nvarchar(max)")
            .HasConversion(
                d => JsonSerializer.Serialize(d, Json),
                s => JsonSerializer.Deserialize<RuleDefinition>(s, Json) ?? RuleDefinition.Empty);
    }
}

internal sealed class RuleRevisionConfiguration : IEntityTypeConfiguration<RuleRevision>
{
    public void Configure(EntityTypeBuilder<RuleRevision> b)
    {
        b.HasKey(r => r.Id);
        b.Property(r => r.UserName).HasMaxLength(Users.User.FullNameMaxLength);
        b.Property(r => r.Summary).HasMaxLength(RuleRevision.SummaryMaxLength);
        b.HasIndex(r => new { r.RuleId, r.CreatedAt });
        b.HasOne<Rule>().WithMany().HasForeignKey(r => r.RuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Users.User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class RuleExecutionConfiguration : IEntityTypeConfiguration<RuleExecution>
{
    public void Configure(EntityTypeBuilder<RuleExecution> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.RuleName).HasMaxLength(Rule.NameMaxLength);
        b.Property(e => e.Trigger).HasMaxLength(64);
        b.Property(e => e.Details).HasMaxLength(RuleExecution.DetailsMaxLength);
        b.HasIndex(e => e.CreatedAt);
        // Лимит автоответов считается по тикету за последний час.
        b.HasIndex(e => new { e.TicketId, e.CreatedAt });
    }
}
