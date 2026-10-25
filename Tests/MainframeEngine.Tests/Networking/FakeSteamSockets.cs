using System.Runtime.InteropServices;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// An in-memory Steam relay network for <see cref="SteamSocketsTransport"/>: one <see cref="FakeSteamSockets"/> per
/// Steam account, linked through this. Behaves like Steam Networking Sockets as the transport sees it: connection state
/// changes are queued per endpoint, the host accepts or closes, messages carry a reliable flag and a message number per
/// sending connection, and every received message lives in native memory until it is released.
/// </summary>
internal sealed unsafe class FakeSteamNetwork : IDisposable
{
    internal sealed class Connection
    {
        public required FakeSteamSockets Owner;
        public required int Endpoint;
        public required ulong RemoteSteamId;
        public uint Remote;
        public bool Open = true;
        public long NextNumber = 1;
        public long UserData;
        public int CloseReason = -1;
        public readonly Queue<nint> Inbox = new(64);
    }

    private readonly Dictionary<(ulong SteamId, int Port), (FakeSteamSockets Backend, int Endpoint)> _listeners = [];
    private readonly Dictionary<nint, (int Size, bool Reliable, long Number)> _messages = new(256);
    private uint _nextConnection = 100;
    private int _nextEndpoint;

    public readonly Dictionary<uint, Connection> Connections = [];
    public readonly Dictionary<int, Queue<SteamSocketEvent>> Events = [];

    /// <summary>Messages received but not released yet (or still undelivered).</summary>
    public int LiveMessages => _messages.Count;

    public int Released { get; private set; }

    public FakeSteamSockets Account(ulong steamId) => new(this, steamId);

    internal int NewEndpoint()
    {
        var endpoint = ++_nextEndpoint;
        Events[endpoint] = new Queue<SteamSocketEvent>(16);
        return endpoint;
    }

    internal void Listen(ulong steamId, int port, FakeSteamSockets backend, int endpoint) => _listeners[(steamId, port)] = (backend, endpoint);

    internal void CloseEndpoint(int endpoint)
    {
        Events.Remove(endpoint);
        foreach (var (key, value) in _listeners.ToArray())
        {
            if (value.Endpoint == endpoint)
                _listeners.Remove(key);
        }
    }

    internal uint Connect(FakeSteamSockets client, int clientEndpoint, ulong hostId, int port)
    {
        var c = new Connection { Owner = client, Endpoint = clientEndpoint, RemoteSteamId = hostId };
        var handle = _nextConnection++;
        Connections[handle] = c;
        if (_listeners.TryGetValue((hostId, port), out var host))
        {
            var s = new Connection { Owner = host.Backend, Endpoint = host.Endpoint, RemoteSteamId = client.LocalSteamId, Remote = handle };
            var serverHandle = _nextConnection++;
            Connections[serverHandle] = s;
            c.Remote = serverHandle;
            Events[host.Endpoint].Enqueue(new SteamSocketEvent(serverHandle, SteamSocketEventKind.Connecting, client.LocalSteamId, 0));
        }
        else
        {
            c.Open = false; // nobody listens: the attempt times out
            Events[clientEndpoint].Enqueue(new SteamSocketEvent(handle, SteamSocketEventKind.ProblemDetectedLocally, hostId, 4001));
        }

        return handle;
    }

    internal bool Accept(uint handle)
    {
        if (!Connections.TryGetValue(handle, out var s) || !s.Open)
            return false;
        var c = Connections[s.Remote];
        Enqueue(s.Endpoint, new SteamSocketEvent(handle, SteamSocketEventKind.Connected, s.RemoteSteamId, 0));
        Enqueue(c.Endpoint, new SteamSocketEvent(s.Remote, SteamSocketEventKind.Connected, c.RemoteSteamId, 0));
        return true;
    }

    internal void Close(uint handle, int reason)
    {
        if (!Connections.TryGetValue(handle, out var c))
            return;
        c.CloseReason = reason;
        var wasOpen = c.Open;
        c.Open = false;
        while (c.Inbox.TryDequeue(out var message))
            Free(message);
        if (wasOpen && c.Remote != 0 && Connections.TryGetValue(c.Remote, out var remote) && remote.Open)
        {
            remote.Open = false; // the remote side still has to Close its handle (Steam: ClosedByPeer)
            Enqueue(remote.Endpoint, new SteamSocketEvent(c.Remote, SteamSocketEventKind.ClosedByPeer, c.Owner.LocalSteamId, reason));
        }
    }

    internal bool Send(uint handle, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (!Connections.TryGetValue(handle, out var c) || !c.Open || c.Remote == 0)
            return false;
        var data = (nint)NativeMemory.Alloc((nuint)Math.Max(1, payload.Length));
        payload.CopyTo(new Span<byte>((void*)data, payload.Length));
        _messages[data] = (payload.Length, reliable, c.NextNumber++);
        Connections[c.Remote].Inbox.Enqueue(data);
        return true;
    }

    internal int Receive(uint handle, Span<nint> messages)
    {
        if (!Connections.TryGetValue(handle, out var c))
            return 0;
        var count = 0;
        while (count < messages.Length && c.Inbox.TryDequeue(out var message))
            messages[count++] = message;
        return count;
    }

    internal SteamSocketMessage Read(nint message)
    {
        var (size, reliable, number) = _messages[message];
        return new SteamSocketMessage(message, size, reliable, number);
    }

    internal void Release(nint message)
    {
        Free(message);
        Released++;
    }

    /// <summary>Delivers the waiting messages of a connection in reverse order (an out-of-order network).</summary>
    public void Reverse(uint handle)
    {
        var inbox = Connections[handle].Inbox;
        var items = inbox.ToArray();
        inbox.Clear();
        for (var i = items.Length - 1; i >= 0; i--)
            inbox.Enqueue(items[i]);
    }

    private void Enqueue(int endpoint, SteamSocketEvent e)
    {
        if (Events.TryGetValue(endpoint, out var queue))
            queue.Enqueue(e);
    }

    private void Free(nint message)
    {
        if (_messages.Remove(message))
            NativeMemory.Free((void*)message);
    }

    public void Dispose()
    {
        foreach (var message in _messages.Keys.ToArray())
            Free(message);
    }
}

/// <summary>One Steam account's view of a <see cref="FakeSteamNetwork"/>.</summary>
internal sealed class FakeSteamSockets(FakeSteamNetwork network, ulong steamId) : ISteamSocketsBackend
{
    public bool Running { get; set; } = true;

    public int Flushes { get; private set; }

    public bool IsRunning => Running;

    public ulong LocalSteamId => steamId;

    public int Listen(int virtualPort)
    {
        var endpoint = network.NewEndpoint();
        network.Listen(steamId, virtualPort, this, endpoint);
        return endpoint;
    }

    public int Connect(ulong remoteSteamId, int virtualPort, out uint connection)
    {
        var endpoint = network.NewEndpoint();
        connection = network.Connect(this, endpoint, remoteSteamId, virtualPort);
        return endpoint;
    }

    public void CloseEndpoint(int endpoint) => network.CloseEndpoint(endpoint);

    public bool TryDequeueEvent(int endpoint, out SteamSocketEvent socketEvent)
    {
        if (network.Events.TryGetValue(endpoint, out var queue) && queue.TryDequeue(out socketEvent))
            return true;
        socketEvent = default;
        return false;
    }

    public bool Accept(uint connection) => network.Accept(connection);

    public void Close(uint connection, int reasonCode, bool linger) => network.Close(connection, reasonCode);

    public void SetUserData(uint connection, long userData) => network.Connections[connection].UserData = userData;

    public bool Send(uint connection, ReadOnlySpan<byte> payload, bool reliable) => network.Send(connection, payload, reliable);

    public void Flush(uint connection) => Flushes++;

    public int Receive(uint connection, Span<nint> messages) => network.Receive(connection, messages);

    public SteamSocketMessage ReadMessage(nint message) => network.Read(message);

    public void ReleaseMessage(nint message) => network.Release(message);
}
