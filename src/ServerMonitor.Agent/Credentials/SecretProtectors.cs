using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ServerMonitor.Agent.Credentials;

internal interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

internal static class SecretProtectors
{
    public static ISecretProtector ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new FilePermissionsProtector();
}

internal sealed class FilePermissionsProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => data;

    public byte[] Unprotect(byte[] data) => data;
}

[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "ServerMonitor.Agent.Credentials.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
