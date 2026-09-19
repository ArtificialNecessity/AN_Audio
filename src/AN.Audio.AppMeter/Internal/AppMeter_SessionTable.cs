namespace AN.Audio.AppMeter.Internal;

/// <summary>
/// Platform-neutral bookkeeping behind every backend (spec 80 D3/D5): session id → (max-peak-since-read, state, flags, pid, endpoint).
/// The backend's poll thread calls <see cref="BeginPoll"/>, <see cref="Observe"/> per session it still sees, then <see cref="EndPoll"/>;
/// the consumer calls <see cref="ReadAndReset"/> at any cadence. All methods take the table's own lock; nothing here touches the OS.
/// </summary>
internal sealed class AppMeter_SessionTable
{
    /// <summary>D5: a session must be seen Expired (or not seen at all) on this many consecutive polls before it is dropped.</summary>
    public const int ExpiryStrikes = 2;

    private sealed class Row
    {
        public AudioAppMeter_EndpointId EndpointId;
        public AudioAppMeter_ProcessId ProcessId;
        public AudioAppMeter_SessionState State;
        public AudioAppMeter_SessionFlags Flags;
        public float MaxPeakSinceRead;
        public int Strikes;          // consecutive polls seen Expired / absent
        public bool SeenThisPoll;
    }

    private readonly object _lock = new();
    private readonly Dictionary<AudioAppMeter_SessionId, Row> _rows = new();

    /// <summary>Set (never cleared by the table) when a session was added or dropped. Backend reads-and-clears to fire <c>SessionsChanged</c> coalesced.</summary>
    private bool _membershipChanged;

    public int Count { get { lock (_lock) return _rows.Count; } }

    /// <summary>Marks every row unseen. Pair with <see cref="EndPoll"/>.</summary>
    public void BeginPoll()
    {
        lock (_lock)
        {
            foreach (var row in _rows.Values) row.SeenThisPoll = false;
        }
    }

    /// <summary>
    /// One session as seen by this poll. Peak folds into the max; state/flags/pid are overwritten (pid can resolve late on Windows).
    /// A row seen Expired accrues a strike; any other state clears strikes. New sessions set the membership-changed flag.
    /// </summary>
    public void Observe(AudioAppMeter_SessionId id, AudioAppMeter_EndpointId endpoint, AudioAppMeter_ProcessId pid,
                        float peak, AudioAppMeter_SessionState state, AudioAppMeter_SessionFlags flags)
    {
        lock (_lock)
        {
            if (!_rows.TryGetValue(id, out var row))
            {
                row = new Row();
                _rows[id] = row;
                _membershipChanged = true;
            }
            row.EndpointId = endpoint;
            row.ProcessId = pid;
            row.State = state;
            row.Flags = flags;
            row.SeenThisPoll = true;
            if (peak > row.MaxPeakSinceRead) row.MaxPeakSinceRead = peak;
            row.Strikes = state == AudioAppMeter_SessionState.Expired ? row.Strikes + 1 : 0;
        }
    }

    /// <summary>
    /// Rows not observed this poll accrue a strike and are marked Expired. Rows at <see cref="ExpiryStrikes"/> are dropped.
    /// Returns true if membership changed since the last <see cref="EndPoll"/> (added OR dropped) — the backend fires <c>SessionsChanged</c> once.
    /// </summary>
    public bool EndPoll()
    {
        lock (_lock)
        {
            List<AudioAppMeter_SessionId>? drop = null;
            foreach (var (id, row) in _rows)
            {
                if (!row.SeenThisPoll)
                {
                    row.State = AudioAppMeter_SessionState.Expired;
                    row.Strikes++;
                }
                if (row.Strikes >= ExpiryStrikes)
                    (drop ??= new()).Add(id);
            }
            if (drop is not null)
            {
                foreach (var id in drop) _rows.Remove(id);
                _membershipChanged = true;
            }
            bool changed = _membershipChanged;
            _membershipChanged = false;
            return changed;
        }
    }

    /// <summary>Drops every row (backend teardown / hard re-enumerate). Sets membership-changed if anything was there.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_rows.Count > 0) _membershipChanged = true;
            _rows.Clear();
        }
    }

    /// <summary>
    /// <see cref="IAudioAppMeter.ReadSessions"/> semantics: writes one row per session and resets each row's max to 0.
    /// If <paramref name="into"/> is too small returns the needed count and writes (and resets) nothing.
    /// </summary>
    public int ReadAndReset(Span<AudioAppMeter_Sample> into)
    {
        lock (_lock)
        {
            if (_rows.Count > into.Length) return _rows.Count;
            int i = 0;
            foreach (var (id, row) in _rows)
            {
                into[i++] = new AudioAppMeter_Sample(id, row.EndpointId, row.ProcessId, new AudioAppMeter_Peak(row.MaxPeakSinceRead), row.State, row.Flags);
                row.MaxPeakSinceRead = 0f;
            }
            return i;
        }
    }

    public bool Contains(AudioAppMeter_SessionId id)
    {
        lock (_lock) return _rows.ContainsKey(id);
    }

    /// <summary>FNV-1a 64 over UTF-16 code units — the branded-id hash for Windows session-instance and endpoint id strings (D11, §4).</summary>
    public static ulong Fnv1a64(ReadOnlySpan<char> text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (char c in text)
        {
            h ^= (byte)c;         h *= prime;
            h ^= (byte)(c >> 8);  h *= prime;
        }
        return h;
    }
}