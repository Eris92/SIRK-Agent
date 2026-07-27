using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SirkAgent.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SIRK Agent";
});
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
