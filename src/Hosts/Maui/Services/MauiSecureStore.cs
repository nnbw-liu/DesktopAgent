using SDemo.DesktopAgent.Services;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SDemo.DesktopAgent.Maui;

/// <summary>
/// API Key 的本地保护。
/// Windows：沿用 WPF 端的 DPAPI（同一用户），因此共用 settings.json 时密文可以互相解开。
/// Android：AES-GCM，密钥放在应用私有目录（系统沙箱保护）；不参与跨端共享。
/// </summary>
public sealed class MauiSecureStore : ISecureStore
{
#if WINDOWS
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.ApiKey.v1");

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipherText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
#else
    private const string KeyFileName = ".api-key.key";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IAppPaths _paths;
    private byte[]? _key;

    public MauiSecureStore(IAppPaths paths) => _paths = paths;

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        try
        {
            var plain = Encoding.UTF8.GetBytes(plaintext);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(GetKey(), TagSize))
            {
                aes.Encrypt(nonce, plain, cipher, tag);
            }

            var payload = new byte[NonceSize + cipher.Length + TagSize];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
            Buffer.BlockCopy(cipher, 0, payload, NonceSize, cipher.Length);
            Buffer.BlockCopy(tag, 0, payload, NonceSize + cipher.Length, TagSize);

            return Convert.ToBase64String(payload);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    public string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var payload = Convert.FromBase64String(cipherText);
            if (payload.Length <= NonceSize + TagSize)
            {
                return string.Empty;
            }

            var nonce = payload.AsSpan(0, NonceSize);
            var cipher = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
            var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(GetKey(), TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private byte[] GetKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        Directory.CreateDirectory(_paths.DataRoot);
        var path = Path.Combine(_paths.DataRoot, KeyFileName);

        if (File.Exists(path))
        {
            _key = File.ReadAllBytes(path);
            if (_key.Length == 32)
            {
                return _key;
            }
        }

        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, _key);
        return _key;
    }
#endif
}