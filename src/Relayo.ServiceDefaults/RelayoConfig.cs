using Microsoft.Extensions.Configuration;

namespace Relayo.ServiceDefaults;

/// <summary>
/// Чтение настроек из переменных окружения с проверкой.
/// Ошибки копятся, а <see cref="ThrowIfInvalid"/> сообщает обо всех сразу,
/// чтобы не исправлять .env по одной переменной.
/// </summary>
public sealed class RelayoConfig(IConfiguration configuration)
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors => _errors;

    public string Required(string key)
    {
        var value = Optional(key);
        if (value is null)
            _errors.Add($"{key} is not set");
        return value ?? string.Empty;
    }

    public string? Optional(string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string Get(string key, string defaultValue) => Optional(key) ?? defaultValue;

    public int Int(string key, int defaultValue)
    {
        var value = Optional(key);
        if (value is null)
            return defaultValue;
        if (int.TryParse(value, out var result))
            return result;
        _errors.Add($"{key} must be an integer, got '{value}'");
        return defaultValue;
    }

    public bool Bool(string key, bool defaultValue)
    {
        var value = Optional(key);
        if (value is null)
            return defaultValue;
        if (bool.TryParse(value, out var result))
            return result;
        _errors.Add($"{key} must be true or false, got '{value}'");
        return defaultValue;
    }

    /// <summary>Ключ в base64 не короче <paramref name="minBytes"/> байт.</summary>
    public byte[] Base64Key(string key, int minBytes)
    {
        var value = Required(key);
        if (value.Length == 0)
            return [];
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < minBytes)
                _errors.Add($"{key} must be at least {minBytes} bytes (generate with: openssl rand -base64 32)");
            return bytes;
        }
        catch (FormatException)
        {
            _errors.Add($"{key} must be base64 (generate with: openssl rand -base64 32)");
            return [];
        }
    }

    public string[] List(string key) =>
        (Optional(key) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void AddError(string error) => _errors.Add(error);

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
            throw new InvalidOperationException(
                "Invalid configuration. Check your environment or .env (see .env.example):"
                + Environment.NewLine + string.Join(Environment.NewLine, _errors.Select(e => "  - " + e)));
    }
}
