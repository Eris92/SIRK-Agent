using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SirkAgent.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SIRK Agent";
});
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<ManagementWorker>();
builder.Services.AddHostedService<ManagementStateReconciler>();
builder.Services.AddHostedService<RuntimeHealthWorker>();
builder.Services.AddHostedService<EnduranceWorker>();
builder.Services.AddHostedService<ControlFileWorker>();
builder.Services.AddHostedService<ManagementPlaneWorker>();
builder.Services.AddHostedService<ActivityCollectorWorker>();
builder.Services.AddHostedService<FileActivityWorker>();
builder.Services.AddHostedService<BrowserBridgeWorker>();
builder.Services.AddHostedService<RiskAnalyticsWorker>();
builder.Services.AddHostedService<AgentUpdateWorker>();
builder.Services.AddSingleton<PortalReconnectSignal>();
builder.Services.AddHostedService<NetworkChangeWorker>();
builder.Services.AddHostedService<DesktopStreamWorker>();

var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(InteractiveSessionPipe.TerminateAll);
lifetime.ApplicationStopped.Register(InteractiveSessionPipe.TerminateAll);
await host.RunAsync();
