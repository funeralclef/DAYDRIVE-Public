using System.Security.Cryptography;

namespace IntelligentScheduler.Infrastructure;

public sealed class LocalSecretProtector : IntelligentScheduler.Application.ISecretProtector
{
    private readonly byte[] _key;

    public LocalSecretProtector(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, ".assistant-secrets.key");
        if (File.Exists(path))
        {
            _key = Convert.FromBase64String(File.ReadAllText(path));
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(path, Convert.ToBase64String(_key));
            if (OperatingSystem.IsWindows())
            {
                try { File.SetAttributes(path, FileAttributes.Hidden); }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[System.Text.Encoding.UTF8.GetByteCount(plaintext)];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, System.Text.Encoding.UTF8.GetBytes(plaintext), cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return string.Empty;
        try
        {
            var payload = Convert.FromBase64String(ciphertext.StartsWith("v1:", StringComparison.Ordinal) ? ciphertext[3..] : ciphertext);
            var nonce = payload[..12];
            var tag = payload[12..28];
            var cipher = payload[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }
}
