using System.Security.Cryptography;
using System.Text;

namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）加密本地敏感值，避免 API Key 明文落盘。
/// 只有同一 Windows 用户才能解密；换用户或换机器后需要重新填写 Key。
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SDemo.DesktopAgent.ApiKey.v1");

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(cipherText);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 换了用户 / 机器，或密文损坏：当作没有 Key，让用户重新填写。
            return string.Empty;
        }
    }
}
