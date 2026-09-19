using AN.Audio.AppMeter.Internal;
using Xunit;

namespace AN.Audio.AppMeter.Tests;

/// <summary>Spec 80 D3 (max-since-read, reset on read) and D5 (two-strike expiry, coalesced membership change) without any OS.</summary>
public class AppMeter_SessionTableTests
{
    private static readonly AudioAppMeter_SessionId S1 = new(1);
    private static readonly AudioAppMeter_SessionId S2 = new(2);
    private static readonly AudioAppMeter_EndpointId E1 = new(100);
    private static readonly AudioAppMeter_ProcessId P1 = new(4242);

    private static void Poll(AppMeter_SessionTable t, params (AudioAppMeter_SessionId id, float peak, AudioAppMeter_SessionState state)[] seen)
    {
        t.BeginPoll();
        foreach (var (id, peak, state) in seen) t.Observe(id, E1, P1, peak, state, AudioAppMeter_SessionFlags.None);
        t.EndPoll();
    }

    [Fact]
    public void Peak_is_max_since_last_read_and_resets_on_read()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0.2f, AudioAppMeter_SessionState.Active));
        Poll(t, (S1, 0.9f, AudioAppMeter_SessionState.Active));
        Poll(t, (S1, 0.1f, AudioAppMeter_SessionState.Active));

        var rows = new AudioAppMeter_Sample[4];
        Assert.Equal(1, t.ReadAndReset(rows));
        Assert.Equal(0.9f, rows[0].Peak.Value);
        Assert.Equal(S1, rows[0].SessionId);
        Assert.Equal(E1, rows[0].EndpointId);
        Assert.Equal(P1, rows[0].ProcessId);

        Assert.Equal(1, t.ReadAndReset(rows));
        Assert.True(rows[0].Peak.IsSilent);

        Poll(t, (S1, 0.3f, AudioAppMeter_SessionState.Active));
        Assert.Equal(1, t.ReadAndReset(rows));
        Assert.Equal(0.3f, rows[0].Peak.Value);
    }

    [Fact]
    public void Too_small_span_returns_needed_count_and_writes_nothing()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0.5f, AudioAppMeter_SessionState.Active), (S2, 0.6f, AudioAppMeter_SessionState.Active));

        var one = new AudioAppMeter_Sample[1];
        Assert.Equal(2, t.ReadAndReset(one));
        Assert.Equal(default, one[0]);

        var two = new AudioAppMeter_Sample[2];
        Assert.Equal(2, t.ReadAndReset(two));
        Assert.Equal(0.5f + 0.6f, two[0].Peak.Value + two[1].Peak.Value, 5);   // max was NOT reset by the failed read
    }

    [Fact]
    public void Absent_session_is_marked_expired_then_dropped_after_two_strikes()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0.5f, AudioAppMeter_SessionState.Active));
        Poll(t);   // strike 1 — still listed, Expired
        var rows = new AudioAppMeter_Sample[4];
        Assert.Equal(1, t.ReadAndReset(rows));
        Assert.Equal(AudioAppMeter_SessionState.Expired, rows[0].State);

        Poll(t);   // strike 2 — gone
        Assert.Equal(0, t.ReadAndReset(rows));
        Assert.False(t.Contains(S1));
    }

    [Fact]
    public void Reappearing_before_second_strike_clears_strikes()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0.5f, AudioAppMeter_SessionState.Active));
        Poll(t);
        Poll(t, (S1, 0.5f, AudioAppMeter_SessionState.Active));
        Poll(t);
        Assert.True(t.Contains(S1));   // only one strike since it came back
        Poll(t);
        Assert.False(t.Contains(S1));
    }

    [Fact]
    public void OS_reported_expired_counts_as_a_strike()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0.5f, AudioAppMeter_SessionState.Active));
        Poll(t, (S1, 0.0f, AudioAppMeter_SessionState.Expired));
        Assert.True(t.Contains(S1));
        Poll(t, (S1, 0.0f, AudioAppMeter_SessionState.Expired));
        Assert.False(t.Contains(S1));
    }

    [Fact]
    public void EndPoll_reports_membership_change_once_per_change()
    {
        var t = new AppMeter_SessionTable();
        t.BeginPoll(); t.Observe(S1, E1, P1, 0f, AudioAppMeter_SessionState.Active, AudioAppMeter_SessionFlags.None);
        Assert.True(t.EndPoll());                       // added
        t.BeginPoll(); t.Observe(S1, E1, P1, 0.4f, AudioAppMeter_SessionState.Active, AudioAppMeter_SessionFlags.None);
        Assert.False(t.EndPoll());                      // same set, peak change is not membership
        t.BeginPoll(); Assert.False(t.EndPoll());       // strike 1: still a member
        t.BeginPoll(); Assert.True(t.EndPoll());        // dropped
        t.BeginPoll(); Assert.False(t.EndPoll());
    }

    [Fact]
    public void Add_and_drop_in_same_poll_coalesce_to_one_change()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0f, AudioAppMeter_SessionState.Active));
        Poll(t);
        t.BeginPoll();
        t.Observe(S2, E1, P1, 0f, AudioAppMeter_SessionState.Active, AudioAppMeter_SessionFlags.None);   // S2 arrives, S1 strikes out
        Assert.True(t.EndPoll());
        Assert.False(t.Contains(S1));
        Assert.True(t.Contains(S2));
    }

    [Fact]
    public void Flags_and_pid_are_overwritten_each_observe()
    {
        var t = new AppMeter_SessionTable();
        t.BeginPoll(); t.Observe(S1, E1, new(0), 0f, AudioAppMeter_SessionState.Active, AudioAppMeter_SessionFlags.Unattributed); t.EndPoll();
        t.BeginPoll(); t.Observe(S1, E1, P1, 0f, AudioAppMeter_SessionState.Inactive, AudioAppMeter_SessionFlags.Muted); t.EndPoll();
        var rows = new AudioAppMeter_Sample[1];
        t.ReadAndReset(rows);
        Assert.Equal(P1, rows[0].ProcessId);
        Assert.Equal(AudioAppMeter_SessionFlags.Muted, rows[0].Flags);
        Assert.Equal(AudioAppMeter_SessionState.Inactive, rows[0].State);
    }

    [Fact]
    public void Clear_drops_everything_and_reports_change()
    {
        var t = new AppMeter_SessionTable();
        Poll(t, (S1, 0f, AudioAppMeter_SessionState.Active));
        t.Clear();
        Assert.Equal(0, t.Count);
        t.BeginPoll(); Assert.True(t.EndPoll());
    }

    [Fact]
    public void Fnv1a64_matches_reference_vectors()
    {
        // FNV-1a 64 over UTF-16LE code units. "" → offset basis; "a" (0x61 0x00) computed by hand against the reference algorithm.
        Assert.Equal(14695981039346656037UL, AppMeter_SessionTable.Fnv1a64(""));
        ulong h = 14695981039346656037UL;
        h ^= 0x61; h *= 1099511628211UL;
        h ^= 0x00; h *= 1099511628211UL;
        Assert.Equal(h, AppMeter_SessionTable.Fnv1a64("a"));
        Assert.NotEqual(AppMeter_SessionTable.Fnv1a64("{0.0.0.00000000}.{a}|x%b{1}"), AppMeter_SessionTable.Fnv1a64("{0.0.0.00000000}.{a}|x%b{2}"));
    }
}