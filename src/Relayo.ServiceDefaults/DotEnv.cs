using Microsoft.Extensions.Configuration;

namespace Relayo.ServiceDefaults;

/// <summary>
/// Загружает переменные из файла .env, чтобы сервисы можно было запускать из IDE
/// с теми же настройками, что и docker-compose.
/// Настоящие переменные окружения важнее: значение из .env берётся, только если
/// переменная не задана в окружении.
/// </summary>
public static class DotEnv
{
    public const string FileName = ".env";

    public static IConfigurationBuilder AddDotEnvFile(this IConfigurationBuilder builder, string? startDirectory = null)
    {
        var path = Find(startDirectory ?? Directory.GetCurrentDirectory())
                   ?? Find(AppContext.BaseDirectory);
        if (path is null)
            return builder;

        var values = Parse(File.ReadAllLines(path))
            .Where(kv => Environment.GetEnvironmentVariable(kv.Key) is null)
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value);

        return builder.AddInMemoryCollection(values);
    }

    /// <summary>Ищет .env в каталоге и выше по дереву.</summary>
    internal static string? Find(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Разбирает строки вида KEY=VALUE. Пустые строки и строки с # в начале пропускаются.
    /// Значение в двойных или одинарных кавычках берётся без кавычек.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string>> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];

            yield return new(key, value);
        }
    }
}
