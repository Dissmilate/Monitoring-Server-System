using ServerMonitor.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.AddWorkerServices();

var host = builder.Build();

host.Run();
