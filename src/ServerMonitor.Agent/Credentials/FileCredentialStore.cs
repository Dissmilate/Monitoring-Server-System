using System.Text.Json;

namespace ServerMonitor.Agent.Credentials;

internal sealed class FileCredentialStore(string directory, ISecretProtector protector) : ICredentialStore
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string FilePath { get; } = Path.Combine(directory, "credentials.dat");

    public async Task<AgentCredentials?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var bytes = protector.Unprotect(await File.ReadAllBytesAsync(FilePath, ct));
        return JsonSerializer.Deserialize(bytes, CredentialsJsonContext.Default.AgentCredentials);
    }

    public async Task SaveAsync(AgentCredentials credentials, CancellationToken ct)
    {
        CreateDirectory();

        var bytes = protector.Protect(
            JsonSerializer.SerializeToUtf8Bytes(credentials, CredentialsJsonContext.Default.AgentCredentials));

        // Write-then-rename: a crash never leaves a truncated file.
        var tempPath = FilePath + ".tmp";
        await using (var stream = new FileStream(tempPath, CreateFileOptions()))
        {
            await stream.WriteAsync(bytes, ct);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        File.Delete(FilePath);
        return Task.CompletedTask;
    }

    private void CreateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode ownerOnly = OwnerReadWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, ownerOnly);
            File.SetUnixFileMode(directory, ownerOnly);
        }
    }

    private static FileStreamOptions CreateFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        return options;
    }
}
