using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Logs;
using ServerMonitor.Infrastructure.Logs;
using ServerMonitor.Application.Metrics;
using ServerMonitor.Infrastructure.Identity;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Infrastructure.Metrics;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Monitor";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
            }

            return new NpgsqlDataSourceBuilder(connectionString).Build();
        });

        services.AddDbContext<MonitorDbContext>((sp, options) =>
            ConfigureDbContext(options, sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<MonitorDbContext>());
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<IAgentCommandRepository, AgentCommandRepository>();
        services.AddSingleton<IServerPresence, PostgresServerPresence>();

        services.AddSingleton<IMetricWriter, TimescaleMetricWriter>();
        services.AddSingleton<IMetricReader, TimescaleMetricReader>();

        return services;
    }

    public static IServiceCollection AddKafka(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<IMessagePublisher, KafkaMessagePublisher>();
        services.AddSingleton<IMonitorEvents, KafkaMonitorEvents>();
        services.AddSingleton<ICommandOutbox, CommandOutbox>();
        services.AddSingleton<KafkaConsumerFactory>();
        services.AddSingleton<KafkaTopicProvisioner>();
        return services;
    }

    public static IServiceCollection AddOpenSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenSearchOptions>(configuration.GetSection(OpenSearchOptions.SectionName));

        static void Configure(IServiceProvider sp, HttpClient client)
        {
            var url = sp.GetRequiredService<IOptions<OpenSearchOptions>>().Value.Url;
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }

        services.AddHttpClient<OpenSearchLogStore>(Configure);
        services.AddHttpClient<OpenSearchProvisioner>(Configure);
        services.AddTransient<ILogWriter>(sp => sp.GetRequiredService<OpenSearchLogStore>());
        services.AddTransient<ILogReader>(sp => sp.GetRequiredService<OpenSearchLogStore>());
        return services;
    }

    public static IdentityBuilder AddIdentityStores(this IServiceCollection services) =>
        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<MonitorDbContext>();

    public static IServiceCollection AddDatabaseInitializer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention();

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
