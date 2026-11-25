using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Channels;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Relayo.Internal.Configuration;
using Relayo.ServiceDefaults.Redis;
using StackExchange.Redis;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>
/// Получение обновлений Telegram.
/// <list type="bullet">
/// <item>Webhook: при запуске регистрирует webhook всех включённых ботов (адрес мог измениться).</item>
/// <item>Long polling: для каждого включённого бота забирает обновления сам. Новые и выключенные боты
/// подхватываются в течение <see cref="RefreshInterval"/>. Telegram отдаёт обновления только одному получателю,
/// поэтому при нескольких экземплярах опрашивает тот, кто держит аренду в Redis; если он пропал, через
/// <see cref="LeaseTtl"/> опрос подхватывает другой.</item>
/// </list>
/// </summary>
internal sealed class TelegramUpdatesService(
    IServiceScopeFactory scopes,
    TelegramBotApi api,
    SecretProtector protector,
    InternalSettings settings,
    TimeProvider time,
    ILogger<TelegramUpdatesService> logger,
    IConnectionMultiplexer? redis = null) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            if (settings.Telegram.Mode == TelegramUpdatesMode.Webhook)
                await SyncWebhooksAsync(stop);
            else
                await PollAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // остановка сервиса
        }
    }

    private async Task SyncWebhooksAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<TelegramBotSettingsService>();
        foreach (var bot in await db.TelegramBots.AsNoTracking().Where(b => b.IsEnabled).ToListAsync(ct))
        {
            try
            {
                await service.SyncWebhookAsync(bot, protector.Unprotect(bot.ProtectedToken), ct);
                logger.LogInformation("Telegram webhook is set for @{Bot}", bot.Username);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Could not set Telegram webhook for @{Bot}: {Error}", bot.Username, ex.Message);
            }
        }
    }

    private async Task PollAsync(CancellationToken stop)
    {
        logger.LogInformation(redis is null
            ? "Telegram updates: long polling (single instance). Use webhook mode in production"
            : "Telegram updates: long polling by the instance that holds the Redis lease. Use webhook mode in production");
        var lease = redis is null ? null : new RedisLease(redis, "telegram-polling", LeaseTtl);
        var leader = lease is null;
        var roleLogged = false;
        var loops = new Dictionary<Guid, (string TokenHash, CancellationTokenSource Cts, Task Task)>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (lease is not null)
                    {
                        var wasLeader = leader;
                        leader = await IsLeaderAsync(lease, leader);
                        if (leader != wasLeader || !roleLogged)
                            logger.LogInformation(leader
                                ? "This instance now polls Telegram ({Owner})"
                                : "Another instance polls Telegram, this one stands by ({Owner})", lease.Owner);
                        roleLogged = true;
                    }
                    var bots = leader ? await LoadEnabledBotsAsync(stop) : [];
                    foreach (var (id, loop) in loops.ToList())
                    {
                        if (bots.TryGetValue(id, out var current) && Hash(current.Token) == loop.TokenHash)
                            continue;
                        await loop.Cts.CancelAsync();
                        loops.Remove(id);
                    }
                    foreach (var (id, bot) in bots)
                    {
                        if (loops.ContainsKey(id))
                            continue;
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        loops[id] = (Hash(bot.Token), cts, PollBotAsync(id, bot.Username, bot.Token, cts.Token));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Could not load Telegram bots: {Error}", ex.Message);
                }
                await Task.Delay(RefreshInterval, time, stop);
            }
        }
        finally
        {
            foreach (var loop in loops.Values)
                await loop.Cts.CancelAsync();
            await Task.WhenAll(loops.Values.Select(l => l.Task));
            // Остановка штатная: отдаём опрос другому экземпляру сразу, не дожидаясь истечения аренды.
            if (lease is not null && leader)
                await lease.ReleaseAsync();
        }
    }

    /// <summary>
    /// Взять или продлить аренду. Если Redis недоступен, экземпляр сохраняет прежнюю роль: два опроса
    /// одновременно Telegram не сломают (второй получит 409), а без опроса сообщения бы копились.
    /// </summary>
    private async Task<bool> IsLeaderAsync(RedisLease lease, bool current)
    {
        try
        {
            return await lease.TryAcquireOrRenewAsync();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("Could not renew the Telegram polling lease: {Error}", ex.Message);
            return current;
        }
    }

    private async Task PollBotAsync(Guid botId, string username, string token, CancellationToken ct)
    {
        logger.LogInformation("Polling Telegram updates for @{Bot}", username);
        long offset = 0;
        var webhookCleared = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Пока у бота есть webhook, getUpdates не работает.
                if (!webhookCleared)
                {
                    await api.DeleteWebhookAsync(token, ct);
                    webhookCleared = true;
                }

                foreach (var update in await api.GetUpdatesAsync(token, offset, ct))
                {
                    // Следующий запрос с offset подтверждает обновление: Telegram его больше не пришлёт.
                    offset = update.UpdateId + 1;
                    await ProcessAsync(botId, update, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram polling for @{Bot} failed, retrying in {Delay}s: {Error}",
                    username, RetryDelay.TotalSeconds, ex.Message);
                try
                {
                    await Task.Delay(RetryDelay, time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ProcessAsync(Guid botId, TgUpdate update, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramConversationService>();
            if (await telegram.FindBotAsync(botId, ct) is not { IsEnabled: true } bot)
                return;
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateProcessor>().ProcessAsync(bot, update, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Telegram update {UpdateId} for bot {BotId} was not processed", update.UpdateId, botId);
        }
    }

    private async Task<Dictionary<Guid, (string Username, string Token)>> LoadEnabledBotsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
        var bots = await db.TelegramBots.AsNoTracking().Where(b => b.IsEnabled)
            .Select(b => new { b.Id, b.Username, b.ProtectedToken }).ToListAsync(ct);
        return bots.ToDictionary(b => b.Id, b => (b.Username, protector.Unprotect(b.ProtectedToken)));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
