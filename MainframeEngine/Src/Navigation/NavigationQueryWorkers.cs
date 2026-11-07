namespace MainframeEngine;

/// <summary>
/// A queued async path query (G3.10), returned by <see cref="NavigationMap2D.QueryPathAsync"/>. A value type: keeping or
/// copying it allocates nothing. Its result is written into <see cref="Result"/> at the map's next sync; until
/// <see cref="IsCompleted"/>, <see cref="Result"/> holds whatever it held before.
/// </summary>
public readonly struct NavigationPathRequest
{
    private readonly NavigationQueryWorkers? _owner;
    private readonly long _ticket;

    internal NavigationPathRequest(NavigationQueryWorkers owner, long ticket, NavigationPath2D result)
    {
        _owner = owner;
        _ticket = ticket;
        Result = result;
    }

    /// <summary>The path the result is delivered into (null for a default request).</summary>
    public NavigationPath2D? Result { get; }

    /// <summary>Whether a request was made (a default value is not one).</summary>
    public bool IsValid => _owner is not null;

    /// <summary>True once the result was delivered (at the map's next sync after the request), or the request cancelled.</summary>
    public bool IsCompleted => _owner is null || _owner.IsDone(_ticket);

    /// <summary>The delivered status (<see cref="GridPathStatus.NotFound"/> before delivery or when cancelled).</summary>
    public GridPathStatus Status => IsCompleted && Result is { } result && !WasCancelled ? result.Status : GridPathStatus.NotFound;

    /// <summary>Whether <see cref="Cancel"/> stopped the request before its delivery.</summary>
    public bool WasCancelled => _owner?.IsCancelled(_ticket) ?? false;

    /// <summary>Drops the request if it was not delivered yet: <see cref="Result"/> is left as it is. Main thread.</summary>
    public void Cancel() => _owner?.Cancel(_ticket);
}

/// <summary>A worker's search scratch: a grid search sized to the largest grid it searched, and a navmesh search.</summary>
internal sealed class NavigationQueryScratch
{
    private readonly GridSearch _grid = new();

    public NavMeshSearch Mesh { get; } = new();

    public GridSearch GridFor(AStarGrid2D grid)
    {
        _grid.EnsureCapacity(grid.Width * grid.Height);
        return _grid;
    }
}

/// <summary>
/// G3.10: the async path queries of a <see cref="NavigationServer2D"/>'s maps. <see cref="Submit"/> (main thread) puts a
/// request in a reused slot and wakes up to <see cref="WorkerCount"/> workers on the .NET thread pool
/// (<see cref="IThreadPoolWorkItem"/>s made once, each with its own <see cref="NavigationQueryScratch"/>); a worker runs the
/// slot's query into the slot's own path. <see cref="Complete"/>, called by every map sync before the map changes, waits
/// until every submitted request ran and copies each result into its caller's path in submission order. Nothing changes a
/// map between its syncs, so the workers read the live grids and mesh without snapshots, and every result depends only on
/// the map at submission and the query: delivery is always the next sync. With <see cref="WorkerCount"/> 0 the requests run
/// on the main thread inside <see cref="Complete"/> (the same results; tests, platforms without threads). Allocation-free
/// once the slots, paths and scratches have grown.
/// </summary>
internal sealed class NavigationQueryWorkers
{
    private readonly object _lock = new();
    private Slot[] _slots = new Slot[16];
    private int _count;          // slots submitted since the last Complete
    private int _next;           // the next slot a worker takes
    private int _running;        // workers that took the batch and have not finished
    private Worker[] _workers = [];
    private readonly NavigationQueryScratch _mainScratch = new();
    private long _nextTicket = 1;
    private long _deliveredThrough; // every ticket ≤ this was delivered or cancelled
    private int _workerCount = DefaultWorkerCount;

    /// <summary>The automatic worker count: a quarter of the processors, at least 1 and at most 4.</summary>
    public static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    /// <summary>Threads that run requests (0: the main thread runs them at the next sync).</summary>
    public int WorkerCount
    {
        get => _workerCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Complete(); // never change the set while workers run
            _workerCount = value;
        }
    }

    /// <summary>Requests submitted and not delivered yet.</summary>
    public int Pending => _count;

    public NavigationPathRequest Submit(NavigationMap2D map, in NavigationPathQuery2D query, NavigationPath2D result)
    {
        var ticket = _nextTicket++;
        Worker? wake = null;
        lock (_lock)
        {
            if (_count == _slots.Length)
                Array.Resize(ref _slots, _count * 2);
            var slot = _slots[_count] ??= new Slot();
            slot.Map = map;
            slot.Query = query;
            slot.Target = result;
            slot.Ticket = ticket;
            slot.Cancelled = false;
            _count++;
            if (_workerCount > 0 && _running < _workerCount && _next < _count)
            {
                if (_workers.Length < _workerCount)
                {
                    var workers = new Worker[_workerCount];
                    _workers.CopyTo(workers, 0);
                    for (var i = _workers.Length; i < workers.Length; i++)
                        workers[i] = new Worker(this);
                    _workers = workers;
                }

                for (var i = 0; i < _workers.Length; i++)
                {
                    if (_workers[i].Busy)
                        continue;
                    wake = _workers[i];
                    wake.Busy = true;
                    _running++;
                    break;
                }
            }
        }

        if (wake is not null)
            ThreadPool.UnsafeQueueUserWorkItem(wake, preferLocal: false);
        return new NavigationPathRequest(this, ticket, result);
    }

    /// <summary>Waits for every submitted request, then delivers them in submission order. Main thread.</summary>
    public void Complete()
    {
        if (_count == 0)
            return;
        using (NavigationMarkers.AsyncWait.Auto())
        {
            lock (_lock)
            {
                // The main thread helps: whatever no worker took yet runs here (all of it with no workers).
                while (_next < _count)
                {
                    var slot = _slots[_next++];
                    Monitor.Exit(_lock);
                    try
                    {
                        Run(slot, _mainScratch);
                    }
                    finally
                    {
                        Monitor.Enter(_lock);
                    }
                }

                while (_running > 0)
                    Monitor.Wait(_lock);
            }
        }

        for (var i = 0; i < _count; i++)
        {
            var slot = _slots[i];
            if (!slot.Cancelled)
                slot.Target!.CopyFrom(slot.Path);
            slot.Map = null;
            slot.Target = null;
        }

        _deliveredThrough = _slots[_count - 1].Ticket;
        _count = 0;
        _next = 0;
    }

    public bool IsDone(long ticket) => ticket <= _deliveredThrough || IsCancelled(ticket);

    public bool IsCancelled(long ticket)
    {
        for (var i = 0; i < _count; i++)
            if (_slots[i].Ticket == ticket)
                return _slots[i].Cancelled;
        return false;
    }

    public void Cancel(long ticket)
    {
        for (var i = 0; i < _count; i++)
            if (_slots[i].Ticket == ticket)
                _slots[i].Cancelled = true; // the worker may still run it; delivery skips it
    }

    private static void Run(Slot slot, NavigationQueryScratch scratch)
    {
        if (slot.Cancelled)
            return;
        try
        {
            slot.Map!.Query(slot.Query, slot.Path, scratch);
        }
        catch (Exception e)
        {
            slot.Path.Clear();
            slot.Path.Status = GridPathStatus.InvalidPoint;
            Log.Error($"[Navigation] An async path query failed: {e.Message}");
        }
    }

    private void WorkLoop(Worker worker)
    {
        lock (_lock)
        {
            while (_next < _count)
            {
                var slot = _slots[_next++];
                Monitor.Exit(_lock);
                try
                {
                    Run(slot, worker.Scratch);
                }
                finally
                {
                    Monitor.Enter(_lock);
                }
            }

            worker.Busy = false;
            _running--;
            Monitor.PulseAll(_lock);
        }
    }

    private sealed class Slot
    {
        public readonly NavigationPath2D Path = new(64);
        public NavigationMap2D? Map;
        public NavigationPathQuery2D Query;
        public NavigationPath2D? Target;
        public long Ticket;
        public volatile bool Cancelled;
    }

    private sealed class Worker(NavigationQueryWorkers owner) : IThreadPoolWorkItem
    {
        public readonly NavigationQueryScratch Scratch = new();
        public bool Busy;

        public void Execute() => owner.WorkLoop(this);
    }
}
