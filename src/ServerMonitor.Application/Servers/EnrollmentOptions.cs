namespace ServerMonitor.Application.Servers;

public sealed class EnrollmentOptions
{
    public const string SectionName = "Enrollment";

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(24);
}
