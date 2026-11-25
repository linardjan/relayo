using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Relayo.Core.Auth;
using Relayo.Core.Conversations;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Relayo.Core.Sites;
using Relayo.Core.Users;

namespace Relayo.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Сервисы ядра. DbContext регистрирует хост: внутреннему сервису он нужен
    /// с интеграцией Wolverine (транзакционный outbox).
    /// </summary>
    public static IServiceCollection AddRelayoCore(
        this IServiceCollection services, JwtOptions jwt, DatabaseInitializerOptions database,
        byte[] secretsEncryptionKey, ClientTokenOptions clientTokens, Files.AttachmentLimits attachments)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(jwt);
        services.AddSingleton<TokenService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<AuthService>();

        services.AddSingleton(new SecretProtector(secretsEncryptionKey));
        services.AddSingleton(clientTokens);
        services.AddScoped<ClientTokenValidator>();
        services.AddScoped<WidgetConversationService>();
        services.AddScoped<Channels.TelegramConversationService>();
        services.AddScoped<TicketService>();
        services.AddSingleton(attachments);
        services.AddScoped<Files.AttachmentService>();
        services.AddScoped<Clients.ClientService>();
        services.AddScoped<SiteSettingsService>();
        services.AddScoped<WidgetPublisher>();
        services.AddScoped<UserAdminService>();
        services.AddSingleton<Directory.LdapDirectory>();
        services.AddScoped<Directory.DirectoryService>();
        services.AddScoped<Automation.RuleService>();
        services.AddScoped<Automation.RuleEngine>();

        services.AddSingleton(database);
        services.AddHostedService<DatabaseInitializer>();
        services.AddHostedService<MessagePartitionMaintenance>();
        services.AddHostedService<Directory.DirectorySyncScheduler>();
        return services;
    }
}
