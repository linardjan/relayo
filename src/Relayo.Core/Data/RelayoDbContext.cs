using Microsoft.EntityFrameworkCore;
using Relayo.Core.Auth;
using Relayo.Core.Users;

namespace Relayo.Core.Data;

public sealed class RelayoDbContext(DbContextOptions<RelayoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayoDbContext).Assembly);
}
