using Sirk.Agent.Commands;

namespace Sirk.Agent.Protocol;

internal sealed class CommandDispatcher
{
    private readonly IReadOnlyDictionary<string, ICommandHandler> handlers;

    public CommandDispatcher(IEnumerable<ICommandHandler> commandHandlers)
    {
        var map = new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);

        foreach (ICommandHandler handler in commandHandlers)
        {
            foreach (string messageType in handler.MessageTypes)
            {
                if (!map.TryAdd(messageType, handler))
                {
                    throw new InvalidOperationException($"Duplicate command handler registration for '{messageType}'.");
                }
            }
        }

        handlers = map;
    }

    public ProtocolResponse Dispatch(ProtocolEnvelope command)
    {
        return handlers.TryGetValue(command.MessageType, out ICommandHandler? handler)
            ? handler.Handle(command)
            : new ProtocolResponse(
                1,
                command.RequestId,
                false,
                null,
                new ProtocolError("unsupported_message", "The requested messageType is not enabled."));
    }
}
