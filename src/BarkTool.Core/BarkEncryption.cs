using System.Security.Cryptography;
using System.Text;

namespace BarkTool.Core;

public static class BarkEncryption
{
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) is not (16 or 24 or 32))
            throw new ArgumentException("请配置加密密钥：UTF-8 长度必须为 16、24 或 32 字节，并与 iPhone Bark 的 AES-CBC 设置一致。");
    }

    internal static (string Ciphertext, string Iv) Encrypt(byte[] json, string key)
    {
        ValidateKey(key);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        try
        {
            // Bark's iv parameter is a UTF-8 string, not Base64-encoded raw IV bytes.
            var iv = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 16);
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            var encrypted = aes.EncryptCbc(json, Encoding.ASCII.GetBytes(iv), PaddingMode.PKCS7);
            return (Convert.ToBase64String(encrypted), iv);
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
    }
}
