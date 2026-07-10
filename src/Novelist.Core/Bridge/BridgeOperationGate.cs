namespace Novelist.Core.Bridge;

public enum BridgeOperationAccess
{
    Shared,
    Exclusive
}

public sealed class BridgeOperationGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _activeReaders;
    private bool _writerActive;
    private int _waitingWriters;

    public ValueTask<IAsyncDisposable> EnterAsync(
    BridgeOperationAccess access,
    CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (CanEnterImmediately(access))
            {
                MarkEntered(access);
                return ValueTask.FromResult<IAsyncDisposable>(new Releaser(this, access));
            }

            var waiter = new Waiter(access, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            if (access == BridgeOperationAccess.Exclusive)
            {
                _waitingWriters++;
            }

            waiter.RegisterCancellation(this);
            return AwaitWaiterAsync(waiter);
        }
    }

    private static async ValueTask<IAsyncDisposable> AwaitWaiterAsync(Waiter waiter)
    {
        try
        {
            await waiter.Completion.Task.ConfigureAwait(false);
            return waiter.Releaser!;
        }
        finally
        {
            waiter.DisposeCancellation();
        }
    }

    private bool CanEnterImmediately(BridgeOperationAccess access) => access switch
    {
        BridgeOperationAccess.Shared => !_writerActive && _waitingWriters == 0,
        BridgeOperationAccess.Exclusive => !_writerActive && _activeReaders == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(access))
    };

    private void MarkEntered(BridgeOperationAccess access)
    {
        if (access == BridgeOperationAccess.Exclusive)
        {
            _writerActive = true;
        }
        else
        {
            _activeReaders++;
        }
    }

    private void Release(BridgeOperationAccess access)
    {
        lock (_sync)
        {
            if (access == BridgeOperationAccess.Exclusive)
            {
                _writerActive = false;
            }
            else
            {
                _activeReaders--;
            }

            GrantWaiters();
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            if (waiter.Access == BridgeOperationAccess.Exclusive)
            {
                _waitingWriters--;
            }

            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            GrantWaiters();
        }
    }

    private void GrantWaiters()
    {
        if (_writerActive || _waiters.First is null)
        {
            return;
        }

        if (_waiters.First.Value.Access == BridgeOperationAccess.Exclusive)
        {
            if (_activeReaders != 0)
            {
                return;
            }

            var writer = RemoveFirst();
            _waitingWriters--;
            _writerActive = true;
            Complete(writer);
            return;
        }

        while (_waiters.First is { Value.Access: BridgeOperationAccess.Shared })
        {
            var reader = RemoveFirst();
            _activeReaders++;
            Complete(reader);
        }
    }

    private Waiter RemoveFirst()
    {
        var waiter = _waiters.First!.Value;
        _waiters.RemoveFirst();
        waiter.Node = null;
        return waiter;
    }

    private void Complete(Waiter waiter)
    {
        waiter.Releaser = new Releaser(this, waiter.Access);
        waiter.Completion.TrySetResult();
    }

    private sealed class Releaser(BridgeOperationGate owner, BridgeOperationAccess access) : IAsyncDisposable
    {
        private BridgeOperationGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(access);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Waiter(BridgeOperationAccess access, CancellationToken cancellationToken)
    {
        private CancellationTokenRegistration _cancellationRegistration;

        public BridgeOperationAccess Access { get; } = access;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public Releaser? Releaser { get; set; }

        public void RegisterCancellation(BridgeOperationGate owner)
        {
            _cancellationRegistration = CancellationToken.Register(
            static state =>
            {
                var (gate, waiter) = ((BridgeOperationGate, Waiter))state!;
                gate.Cancel(waiter);
            },
            (owner, this));
        }

        public void DisposeCancellation() => _cancellationRegistration.Dispose();
    }
}
