using RemoteAgent.Core.Abstractions;

namespace RemoteAgent.Core.Input;

/// <summary>What one connection was holding down when it was asked to let go.</summary>
/// <param name="Keys">Virtual keys, most recently pressed first — the order to release them in.</param>
/// <param name="Buttons">Mouse buttons.</param>
public sealed record HeldInput(IReadOnlyList<int> Keys, IReadOnlyList<MouseButton> Buttons)
{
    /// <summary>Nothing held.</summary>
    public static HeldInput None { get; } = new([], []);

    /// <summary>Whether there is nothing to release.</summary>
    public bool IsEmpty => Keys.Count == 0 && Buttons.Count == 0;
}

/// <summary>
/// Remembers which keys and mouse buttons each control connection is holding down.
/// </summary>
/// <remarks>
/// <para>This is the bookkeeping behind the dead-man release (§4). A drag is a button-down, some
/// moves, and a button-up — three separate commands — and a phone can vanish between any two of
/// them: Wi-Fi drops, the app is swiped away, the battery dies. Without a record of what was left
/// pressed, the PC is stuck with a held mouse button or a held Ctrl until someone works out why
/// every click selects text.</para>
///
/// <para>Tracked per connection, not globally, so that one phone disconnecting releases its own
/// keys and leaves another phone's drag alone. Only the state changes that commands split across
/// messages can cause are recorded: a click or a shortcut presses and releases inside one atomic
/// batch and never leaves anything behind.</para>
///
/// <para>Key codes held here never leave the process: callers log how many keys were released,
/// never which (§7.5).</para>
/// </remarks>
public sealed class HeldInputTracker
{
    private readonly Dictionary<string, Holding> _byConnection = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>Whether no connection is holding anything.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _byConnection.Count == 0;
            }
        }
    }

    /// <summary>Records a key going down or coming up.</summary>
    public void SetKey(string connectionId, int virtualKey, bool down)
    {
        lock (_sync)
        {
            if (down)
            {
                Holding holding = GetOrAdd(connectionId);

                // Auto-repeat sends the same key-down again; it is still one held key.
                if (!holding.Keys.Contains(virtualKey))
                {
                    holding.Keys.Add(virtualKey);
                }

                return;
            }

            if (_byConnection.TryGetValue(connectionId, out Holding? existing))
            {
                existing.Keys.Remove(virtualKey);
                RemoveIfEmpty(connectionId, existing);
            }
        }
    }

    /// <summary>Records a mouse button going down or coming up.</summary>
    public void SetButton(string connectionId, MouseButton button, bool down)
    {
        lock (_sync)
        {
            if (down)
            {
                Holding holding = GetOrAdd(connectionId);
                if (!holding.Buttons.Contains(button))
                {
                    holding.Buttons.Add(button);
                }

                return;
            }

            if (_byConnection.TryGetValue(connectionId, out Holding? existing))
            {
                existing.Buttons.Remove(button);
                RemoveIfEmpty(connectionId, existing);
            }
        }
    }

    /// <summary>What a connection is holding right now, without forgetting it.</summary>
    public HeldInput Peek(string connectionId)
    {
        lock (_sync)
        {
            return _byConnection.TryGetValue(connectionId, out Holding? holding)
                ? Snapshot(holding)
                : HeldInput.None;
        }
    }

    /// <summary>
    /// Returns everything a connection is holding and forgets it. The caller releases what is
    /// returned.
    /// </summary>
    public HeldInput Take(string connectionId)
    {
        lock (_sync)
        {
            return _byConnection.Remove(connectionId, out Holding? holding)
                ? Snapshot(holding)
                : HeldInput.None;
        }
    }

    /// <summary>
    /// Returns everything every connection is holding, per connection, and forgets all of it.
    /// </summary>
    public IReadOnlyList<(string ConnectionId, HeldInput Held)> TakeAll()
    {
        lock (_sync)
        {
            var all = new List<(string, HeldInput)>(_byConnection.Count);
            foreach (KeyValuePair<string, Holding> entry in _byConnection)
            {
                all.Add((entry.Key, Snapshot(entry.Value)));
            }

            _byConnection.Clear();
            return all;
        }
    }

    private Holding GetOrAdd(string connectionId)
    {
        if (!_byConnection.TryGetValue(connectionId, out Holding? holding))
        {
            holding = new Holding();
            _byConnection[connectionId] = holding;
        }

        return holding;
    }

    private void RemoveIfEmpty(string connectionId, Holding holding)
    {
        if (holding.Keys.Count == 0 && holding.Buttons.Count == 0)
        {
            _byConnection.Remove(connectionId);
        }
    }

    private static HeldInput Snapshot(Holding holding)
    {
        // Released in the reverse of the order they were pressed, the way a hand lets go of a
        // chord: the letter before the modifier, so no application sees a bare letter.
        int[] keys = holding.Keys.ToArray();
        Array.Reverse(keys);
        return new HeldInput(keys, holding.Buttons.ToArray());
    }

    private sealed class Holding
    {
        public List<int> Keys { get; } = [];

        public List<MouseButton> Buttons { get; } = [];
    }
}
