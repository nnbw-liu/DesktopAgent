namespace SDemo.DesktopAgent.Services;

/// <summary>
/// 本地敏感值（API Key）的加解密。
/// 桌面端用 Windows DPAPI；移动端换成 SecureStorage（Keystore / Keychain）。
/// </summary>
public interface ISecureStore
{
    /// <summary>加密；空字符串原样返回。</summary>
    string Protect(string? plaintext);

    /// <summary>解密；密文损坏或换了用户 / 机器时返回空字符串，让用户重新填写。</summary>
    string Unprotect(string? cipherText);
}
