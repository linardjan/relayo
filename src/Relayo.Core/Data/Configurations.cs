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
