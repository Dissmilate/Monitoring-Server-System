namespace ServerMonitor.Agent.Metrics.Linux;

internal sealed record CgroupMemory(long UsedBytes, long? LimitBytes, long SwapUsedBytes);

internal abstract class CgroupStats(string root)
{
    public abstract string Version { get; }

    public abstract long ReadCpuUsageMicroseconds();

    public abstract CgroupMemory ReadMemory();

    public static CgroupStats? Detect(string root)
    {
        if (Exists(root, "cgroup.controllers") && Exists(root, "cpu.stat") && Exists(root, "memory.current"))
        {
            return new CgroupV2Stats(root);
        }

        if (Exists(root, "cpuacct/cpuacct.usage") && Exists(root, "memory/memory.usage_in_bytes"))
        {
            return new CgroupV1Stats(root);
        }

        return null;
    }

    protected string Read(string relativePath) => File.ReadAllText(PathOf(root, relativePath));

    protected bool Exists(string relativePath) => Exists(root, relativePath);

    private static bool Exists(string root, string relativePath) => File.Exists(PathOf(root, relativePath));

    private static string PathOf(string root, string relativePath) => Path.Combine(root, "sys/fs/cgroup", relativePath);
}

internal sealed class CgroupV2Stats(string root) : CgroupStats(root)
{
    public override string Version => "v2";

    public override long ReadCpuUsageMicroseconds() => ProcParsers.ParseCgroupCpuUsageMicroseconds(Read("cpu.stat"));

    // Like `docker stats`: usage minus page cache.
    public override CgroupMemory ReadMemory()
    {
        var usage = ProcParsers.ParseCgroupLimit(Read("memory.current")) ?? 0;
        var inactiveFile = ProcParsers.ParseCgroupMemoryStat(Read("memory.stat"), "inactive_file");
        var swap = Exists("memory.swap.current") ? ProcParsers.ParseCgroupLimit(Read("memory.swap.current")) ?? 0 : 0;

        return new CgroupMemory(Math.Max(0, usage - inactiveFile), ProcParsers.ParseCgroupLimit(Read("memory.max")), swap);
    }
}

internal sealed class CgroupV1Stats(string root) : CgroupStats(root)
{
    public override string Version => "v1";

    public override long ReadCpuUsageMicroseconds() => ProcParsers.ParseCgroupV1Value(Read("cpuacct/cpuacct.usage")) / 1000;

    public override CgroupMemory ReadMemory()
    {
        var usage = ProcParsers.ParseCgroupV1Value(Read("memory/memory.usage_in_bytes"));
        var stat = Read("memory/memory.stat");

        var inactiveFile = ProcParsers.ParseCgroupMemoryStat(stat, "total_inactive_file");
        if (inactiveFile == 0)
        {
            inactiveFile = ProcParsers.ParseCgroupMemoryStat(stat, "inactive_file");
        }

        var swap = Exists("memory/memory.memsw.usage_in_bytes")
            ? Math.Max(0, ProcParsers.ParseCgroupV1Value(Read("memory/memory.memsw.usage_in_bytes")) - usage)
            : 0;

        var limit = ProcParsers.ParseCgroupV1Limit(Read("memory/memory.limit_in_bytes"));
        return new CgroupMemory(Math.Max(0, usage - inactiveFile), limit, swap);
    }
}
