using Sirk.Agent.Protocol;

namespace Sirk.Agent.Commands;

internal interface ICommandHandler
{
    IReadOnlyCollection<string> MessageTypes { get; }

    ProtocolResponse Handle(ProtocolEnvelope command);
}
