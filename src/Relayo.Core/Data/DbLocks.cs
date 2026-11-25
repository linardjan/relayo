using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Relayo.Core.Data;

/// <summary>
/// Именованные блокировки в БД (sp_getapplock): держатся до конца текущей транзакции.
/// Нужны, когда два одновременных запроса не должны создать две записи (два тикета, два клиента)
/// или выполнить одно действие дважды. Работают между экземплярами сервиса.
/// </summary>
public static class DbLocks
{
    /// <summary>Сколько ждать чужую блокировку, прежде чем сдаться.</summary>
    private const int TimeoutMs = 30_000;

    public static async Task LockAsync(this RelayoDbContext db, string key, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A database lock needs an open transaction");

        // Имя ресурса ограничено 255 символами, поэтому берём хеш ключа.
        var resource = "relayo:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = {TimeoutMs};
            IF @result < 0 THROW 50001, 'Could not acquire an application lock', 1;
            """, ct);
    }
}
