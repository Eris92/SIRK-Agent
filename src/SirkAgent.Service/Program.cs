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
builder.Services.AddSingleton<PortalReconnectSignal>();
builder.Services.AddHostedService<NetworkChangeWorker>();

await builder.Build().RunAsync();
