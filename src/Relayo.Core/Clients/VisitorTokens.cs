using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Relayo.Core.Clients;

/// <summary>
/// Токен посетителя виджета: случайные 32 байта, живут в localStorage браузера.
/// В БД хранится только SHA-256 от токена.
/// </summary>
public static class VisitorTokens
{
    public static string Create() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
