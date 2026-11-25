using System.Security.Cryptography;
using System.Text;

namespace Relayo.Core.Security;

/// <summary>
/// Шифрует секреты перед записью в БД: ключи сайтов, позже токены ботов и каналов (п. 5.2 ТЗ).
/// AES-256-GCM, ключ из SECRETS_ENCRYPTION_KEY. Формат: base64(nonce | tag | ciphertext).
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes.", nameof(key));
        _key = key;
    }

    public string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedValue)
    {
        var input = Convert.FromBase64String(protectedValue);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Protected value is too short.");
        var plaintext = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize),
            input.AsSpan(NonceSize, TagSize), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
