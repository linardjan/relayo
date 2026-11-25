namespace Relayo.ServiceDefaults;

public sealed record RabbitSettings(string Host, int Port, string VirtualHost, string User, string Password)
{
    /// <summary>
    /// Читает общие параметры RabbitMQ и учётку из указанных переменных:
    /// у внутреннего и публичного сервисов разные пользователи.
    /// </summary>
    public static RabbitSettings Load(RelayoConfig config, string userKey, string passwordKey) => new(
        config.Required("RABBITMQ_HOST"),
        config.Int("RABBITMQ_PORT", 5672),
        config.Get("RABBITMQ_VHOST", "/"),
        config.Required(userKey),
        config.Required(passwordKey));
}
