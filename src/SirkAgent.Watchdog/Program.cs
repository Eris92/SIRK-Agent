using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SirkAgent.Watchdog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SIRK Agent Watchdog");
builder.Services.AddHostedService<WatchdogWorker>();
await builder.Build().RunAsync();
