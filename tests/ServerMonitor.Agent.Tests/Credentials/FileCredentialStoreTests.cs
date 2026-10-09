using System.Runtime.Versioning;
using System.Text;
using ServerMonitor.Agent.Credentials;

namespace ServerMonitor.Agent.Tests.Credentials;

public sealed class FileCredentialStoreTests : IDisposable
{
    private static readonly AgentCredentials Sample = new(Guid.NewGuid(), "smk_0123456789ab_super-secret-value");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sm-agent-tests-{Guid.NewGuid():N}");
    private readonly FileCredentialStore _store;

    public FileCredentialStoreTests() =>
        _store = new FileCredentialStore(_directory, SecretProtectors.ForCurrentPlatform());

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_file_means_not_enrolled() =>
        (await _store.LoadAsync(Ct)).ShouldBeNull();

    [Fact]
    public async Task Credentials_round_trip_and_can_be_deleted()
    {
        await _store.SaveAsync(Sample, Ct);

        (await _store.LoadAsync(Ct)).ShouldBe(Sample);

        await _store.DeleteAsync(Ct);
        (await _store.LoadAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task On_unix_only_the_owner_can_access_the_credentials()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");

        await _store.SaveAsync(Sample, Ct);

        File.GetUnixFileMode(_store.FilePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task On_unix_a_pre_existing_data_directory_is_restricted_to_the_owner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        await _store.SaveAsync(Sample, Ct);

        File.GetUnixFileMode(_directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task On_windows_the_key_is_encrypted_at_rest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is available on Windows only.");

        await _store.SaveAsync(Sample, Ct);

        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_store.FilePath, Ct));
        raw.ShouldNotContain("super-secret-value");
        (await _store.LoadAsync(Ct)).ShouldBe(Sample);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
