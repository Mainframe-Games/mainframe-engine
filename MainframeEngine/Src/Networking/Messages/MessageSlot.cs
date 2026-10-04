namespace MainframeEngine.Networking;

/// <summary>Per-message-type dispatch entry of a <see cref="MessageBus"/>: decodes the payload and invokes the handlers.</summary>
internal abstract class MessageSlot
{
    public abstract bool HasHandlers { get; }

    /// <summary>Decodes one message from <paramref name="reader"/> and invokes the handlers (if any).</summary>
    public abstract void Dispatch(in MessageContext context, NetBufferReader reader);
}

internal sealed class MessageSlot<T> : MessageSlot where T : struct, INetworkTransferable
{
    public MessageHandler<T>? Handlers;

    public override bool HasHandlers => Handlers is not null;

    public override void Dispatch(in MessageContext context, NetBufferReader reader)
    {
        var handlers = Handlers;
        if (handlers is null)
            return;

        var message = default(T);
        message.NetworkRead(reader);
        handlers(in context, in message);
    }
}
