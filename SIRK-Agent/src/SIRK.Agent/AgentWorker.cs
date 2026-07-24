using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sirk.Agent.Ipc;

namespace Sirk.Agent;

internal sealed class AgentWorker(
    NamedPipeCommandServer server,
    ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SIRK-Agent starting. ProtocolVersion={ProtocolVersion}", 1);

        try
        {
            await server.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "SIRK-Agent stopped because of an unhandled error.");
            throw;
        }
        finally
        {
            logger.LogInformation("SIRK-Agent stopped.");
        }
    }
}
