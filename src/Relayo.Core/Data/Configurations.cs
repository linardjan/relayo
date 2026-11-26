using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relayo.Core.Auth;
using Relayo.Core.Users;

namespace Relayo.Core.Data;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasKey(u => u.Id);
        b.Property(u => u.Login).HasMaxLength(User.LoginMaxLength);
        b.Property(u => u.LoginNormalized).HasMaxLength(User.LoginMaxLength);
        b.HasIndex(u => u.LoginNormalized).IsUnique();
        b.Property(u => u.FullName).HasMaxLength(User.FullNameMaxLength);
        b.Property(u => u.Alias).HasMaxLength(User.AliasMaxLength);
        b.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
        b.Property(u => u.Source).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(u => u.AdObjectGuid).IsUnique();
        b.HasIndex(u => u.UsedeskId).IsUnique().HasFilter("usedesk_id IS NOT NULL");
        b.Property(u => u.Email).HasMaxLength(User.EmailMaxLength);
        b.Property(u => u.Position).HasMaxLength(User.PositionMaxLength);
        b.Property(u => u.Phone).HasMaxLength(User.PhoneMaxLength);
        b.Property(u => u.Locale).HasMaxLength(16);
        b.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
        // Группы сотрудника хранятся JSON-массивом: их немного, читаются и пишутся вместе с сотрудником.
        // Поиск по ним (Contains) EF переводит в OPENJSON.
        b.PrimitiveCollection(u => u.GroupIds);
        b.HasOne<AgentGroup>().WithMany().HasForeignKey(u => u.DefaultGroupId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AgentGroupConfiguration : IEntityTypeConfiguration<AgentGroup>
{
    public void Configure(EntityTypeBuilder<AgentGroup> b)
    {
        b.HasKey(g => g.Id);
        b.Property(g => g.Name).HasMaxLength(AgentGroup.NameMaxLength);
        b.HasIndex(g => g.Name).IsUnique();
    }
}

internal sealed class LdapSettingsConfiguration : IEntityTypeConfiguration<Directory.LdapSettings>
{
    public void Configure(EntityTypeBuilder<Directory.LdapSettings> b)
    {
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        // Одна запись на всю систему.
        b.ToTable(t => t.HasCheckConstraint("ck_ldap_settings_singleton", $"id = {Directory.LdapSettings.SingletonId}"));
        b.Property(s => s.Host).HasMaxLength(Directory.LdapSettings.TextMaxLength);
        b.Property(s => s.BindDn).HasMaxLength(Directory.LdapSettings.TextMaxLength);
        b.Property(s => s.UsersDn).HasMaxLength(Directory.LdapSettings.TextMaxLength);
        b.Property(s => s.GuidAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.LoginAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.FullNameAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.MailAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.PhoneAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.PositionAttribute).HasMaxLength(Directory.LdapSettings.AttributeMaxLength);
        b.Property(s => s.Security).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.DefaultRole).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.LastCheckMessage).HasMaxLength(1000);
        b.Property(s => s.LastSyncMessage).HasMaxLength(4000);
        b.Ignore(s => s.IsConfigured);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.HasKey(t => t.Id);
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);
        b.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
