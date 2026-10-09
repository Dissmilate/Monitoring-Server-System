using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServerMonitor.Infrastructure;
using ServerMonitor.Infrastructure.Logs;
using ServerMonitor.Infrastructure.Messaging;
using ServerMonitor.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure();
builder.Services.AddIdentityStores();
builder.Services.AddDatabaseInitializer(builder.Configuration);
builder.Services.AddKafka(builder.Configuration);
builder.Services.AddOpenSearch(builder.Configuration);

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
await scope.ServiceProvider.GetRequiredService<KafkaTopicProvisioner>().EnsureTopicsAsync(CancellationToken.None);
await scope.ServiceProvider.GetRequiredService<OpenSearchProvisioner>().EnsureAsync(CancellationToken.None);
