using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sirk.Agent;
using Sirk.Agent.Ipc;
using Sirk.Agent.Protocol;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SIRK Agent";
});

builder.Services.AddSingleton<ProtocolValidator>();
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<NamedPipeCommandServer>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
