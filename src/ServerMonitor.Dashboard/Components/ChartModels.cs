namespace ServerMonitor.Dashboard.Components;

public sealed record ChartPoint(DateTime Time, decimal Value);

public sealed record ChartSeries(string Name, List<ChartPoint> Points);
