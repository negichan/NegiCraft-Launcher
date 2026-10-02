using System;
using System.Security.Cryptography;
using System.Text;

namespace NegiCraftLauncher.Core.Auth;

/// <summary>
/// 离线账户认证与 UUID 生成器。
/// 算法与 Java 原生 UUID.nameUUIDFromBytes(("OfflinePlayer:" + name).getBytes(UTF_8)) 严格一致。
/// </summary>
public static class OfflineAuthenticator
{
    public static Guid GenerateOfflineUuid(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be null or empty", nameof(username));
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + username);
        byte[] md5Bytes = MD5.HashData(nameBytes);

        // Version 3 UUID: 清空高 4 位并设为 0011 (3)
        md5Bytes[6] = (byte)((md5Bytes[6] & 0x0f) | 0x30);
        // Variant: 清空最高 2 位并设为 10
        md5Bytes[8] = (byte)((md5Bytes[8] & 0x3f) | 0x80);

        // .NET Guid 构造函数接收小端字节序，而 Java UUID 采用大端字节序，需调整前三个字段
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(md5Bytes, 0, 4);
            Array.Reverse(md5Bytes, 4, 2);
            Array.Reverse(md5Bytes, 6, 2);
        }

        return new Guid(md5Bytes);
    }
}
