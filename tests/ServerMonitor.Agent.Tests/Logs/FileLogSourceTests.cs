using System.Text;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.Agent.Logs;
using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Tests.Logs;

public sealed class FileLogSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string LogPath => Path.Combine(_directory.Path, "app.log");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_complete_lines_then_continues_after_the_stored_position()
    {
        var source = Source();
        Append("2026-10-07T11:59:00Z INFO first\n2026-10-07T11:59:01Z ERROR second\n");

        var first = (await source.ReadAsync(null, 100, Ct)).ShouldNotBeNull();

        first.Entries.Select(e => e.Message).ShouldBe(["2026-10-07T11:59:00Z INFO first", "2026-10-07T11:59:01Z ERROR second"]);
        first.Entries[1].Level.ShouldBe(LogSeverity.Error);
        first.Entries[1].Time.ToDateTimeOffset().ShouldBe(new DateTimeOffset(2026, 10, 7, 11, 59, 1, TimeSpan.Zero));
        first.Entries[1].Source.ShouldBe(LogPath);

        (await source.ReadAsync(first.Position, 100, Ct)).ShouldBeNull();

        Append("third\n");
        var next = (await source.ReadAsync(first.Position, 100, Ct)).ShouldNotBeNull();
        next.Entries.ShouldHaveSingleItem().Message.ShouldBe("third");
        next.Entries[0].Time.ToDateTimeOffset().ShouldBe(Now);
    }

    [Fact]
    public async Task An_incomplete_last_line_waits_for_its_newline()
    {
        var source = Source();
        Append("done\nhalf a li");

        var first = (await source.ReadAsync(null, 100, Ct)).ShouldNotBeNull();
        first.Entries.Select(e => e.Message).ShouldBe(["done"]);

        Append("ne\r\n");
        var second = (await source.ReadAsync(first.Position, 100, Ct)).ShouldNotBeNull();
        second.Entries.Select(e => e.Message).ShouldBe(["half a line"]);
    }

    [Fact]
    public async Task Reads_at_most_the_requested_number_of_entries()
    {
        var source = Source();
        Append("a\nb\nc\n");

        var first = (await source.ReadAsync(null, 2, Ct)).ShouldNotBeNull();
        var rest = (await source.ReadAsync(first.Position, 2, Ct)).ShouldNotBeNull();

        first.Entries.Select(e => e.Message).ShouldBe(["a", "b"]);
        rest.Entries.Select(e => e.Message).ShouldBe(["c"]);
    }

    [Fact]
    public async Task A_truncated_file_is_read_from_the_start()
    {
        var source = Source();
        Append("old line one\nold line two\n");
        var position = (await source.ReadAsync(null, 100, Ct))!.Position;

        File.WriteAllText(LogPath, "new\n");

        (await source.ReadAsync(position, 100, Ct))!.Entries.Select(e => e.Message).ShouldBe(["new"]);
    }

    [Fact]
    public async Task A_rotated_file_is_read_from_the_start_even_when_it_is_longer()
    {
        var source = Source();
        Append("first file\n");
        var position = (await source.ReadAsync(null, 100, Ct))!.Position;

        File.Move(LogPath, LogPath + ".1");
        Append("second file, already longer than the first\n");

        (await source.ReadAsync(position, 100, Ct))!.Entries.Select(e => e.Message)
            .ShouldBe(["second file, already longer than the first"]);
    }

    [Fact]
    public async Task The_first_run_ships_only_the_tail_of_a_large_file()
    {
        var source = Source();
        var line = new string('x', 99) + "\n";
        Append(string.Concat(Enumerable.Repeat(line, 2000)) + "last\n");

        var result = (await source.ReadAsync(null, 10_000, Ct)).ShouldNotBeNull();

        result.Entries.Count.ShouldBeLessThanOrEqualTo((FileLogSource.InitialTailBytes / line.Length) + 1);
        result.Entries.ShouldAllBe(e => e.Message.Length == 99 || e.Message == "last");
        result.Entries[^1].Message.ShouldBe("last");
    }

    [Fact]
    public async Task Entry_ids_are_deterministic_and_unique_per_line()
    {
        Append("same\nsame\n");

        var first = (await Source().ReadAsync(null, 100, Ct))!.Entries;
        var again = (await Source().ReadAsync(null, 100, Ct))!.Entries;

        first.Select(e => e.Id).ShouldBe(again.Select(e => e.Id));
        first.Select(e => e.Id).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_missing_file_yields_nothing()
    {
        (await Source().ReadAsync(null, 100, Ct)).ShouldBeNull();
    }

    public void Dispose() => _directory.Dispose();

    private FileLogSource Source() => new(LogPath, _time);

    private void Append(string text) => File.AppendAllText(LogPath, text, new UTF8Encoding(false));
}
