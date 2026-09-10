using AN.Audio.Midi.Internal;
using Xunit;

namespace AN.Audio.Midi.Tests;

/// <summary>
/// Exercises the raw MIDI 1.0 byte-stream parser used by the ALSA rawmidi backend (SPEC-30 D39): running status, interleaved
/// real-time, system common lengths, SysEx staging through the reassembler, and chunk-boundary independence.
/// Platform-neutral — no device needed.
/// </summary>
public class Midi_ByteStreamParserTests
{
    private sealed class Sink : IMidi_ByteStreamSink
    {
        public readonly List<(byte Status, byte D1, byte D2)> Short = new();
        public readonly List<byte[]> SysEx = new();
        public void OnShortMessage(byte status, byte data1, byte data2) => Short.Add((status, data1, data2));
        public void OnSysEx(ReadOnlySpan<byte> complete) => SysEx.Add(complete.ToArray());
    }

    private static (Midi_ByteStreamParser parser, Sink sink, Midi_SysExReassembler reasm) Make(int maxSysEx = 1024, int stage = 256)
    {
        var sink = new Sink();
        var reasm = new Midi_SysExReassembler(64, maxSysEx);
        return (new Midi_ByteStreamParser(sink, reasm, stage), sink, reasm);
    }

    [Fact]
    public void Explicit_status_messages_decode()
    {
        var (p, s, _) = Make();
        p.Feed(new byte[] { 0x90, 0x3C, 0x40, 0x80, 0x3C, 0x00, 0xE0, 0x00, 0x40, 0xC0, 0x05, 0xD1, 0x22 });
        Assert.Equal(new[] { ((byte)0x90, (byte)0x3C, (byte)0x40), ((byte)0x80, (byte)0x3C, (byte)0x00), ((byte)0xE0, (byte)0x00, (byte)0x40), ((byte)0xC0, (byte)0x05, (byte)0), ((byte)0xD1, (byte)0x22, (byte)0) }, s.Short);
    }

    [Fact]
    public void Running_status_repeats_the_channel_status()
    {
        var (p, s, _) = Make();
        p.Feed(new byte[] { 0x90, 0x3C, 0x40, 0x3E, 0x41, 0x3C, 0x00, 0xC2, 0x01, 0x02 });   // 3 notes under one 0x90; 2 program changes under one 0xC2
        Assert.Equal(5, s.Short.Count);
        Assert.All(s.Short.Take(3), m => Assert.Equal(0x90, m.Status));
        Assert.Equal(((byte)0x90, (byte)0x3C, (byte)0x00), s.Short[2]);   // velocity-0 NoteOn delivered as received (D15)
        Assert.Equal(((byte)0xC2, (byte)0x01, (byte)0), s.Short[3]);
        Assert.Equal(((byte)0xC2, (byte)0x02, (byte)0), s.Short[4]);
    }

    [Fact]
    public void Real_time_bytes_interleave_without_breaking_a_message_or_running_status()
    {
        var (p, s, _) = Make();
        p.Feed(new byte[] { 0x90, 0xF8, 0x3C, 0xFE, 0x40, 0xF8, 0x3E, 0x41 });
        Assert.Equal(new[] { ((byte)0xF8, (byte)0, (byte)0), ((byte)0xFE, (byte)0, (byte)0), ((byte)0x90, (byte)0x3C, (byte)0x40), ((byte)0xF8, (byte)0, (byte)0), ((byte)0x90, (byte)0x3E, (byte)0x41) }, s.Short);
    }

    [Fact]
    public void System_common_lengths_and_running_status_cancel()
    {
        var (p, s, _) = Make();
        p.Feed(new byte[] { 0x90, 0x3C, 0x40, 0xF2, 0x10, 0x20, 0xF3, 0x05, 0xF1, 0x33, 0xF6, 0x3C, 0x40 });
        // after F6 (tune request) running status is cancelled, so the trailing 3C 40 are stray and dropped
        Assert.Equal(new[] { ((byte)0x90, (byte)0x3C, (byte)0x40), ((byte)0xF2, (byte)0x10, (byte)0x20), ((byte)0xF3, (byte)0x05, (byte)0), ((byte)0xF1, (byte)0x33, (byte)0), ((byte)0xF6, (byte)0, (byte)0) }, s.Short);
    }

    [Fact]
    public void Stray_data_bytes_before_any_status_are_dropped()
    {
        var (p, s, _) = Make();
        p.Feed(new byte[] { 0x40, 0x40, 0x7F, 0xB0, 0x07, 0x7F });
        Assert.Single(s.Short);
        Assert.Equal(((byte)0xB0, (byte)0x07, (byte)0x7F), s.Short[0]);
    }

    [Fact]
    public void SysEx_with_interleaved_clock_completes_once_and_resumes_channel_messages()
    {
        var (p, s, _) = Make();
        var reply = new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x47, 0x5D, 0x00, 0x19, 0x00, 0x01, 0x42, 0x00, 0x00, 0xF7 };
        var stream = new List<byte>(reply);
        stream.Insert(5, 0xF8);   // clock inside the SysEx
        stream.AddRange(new byte[] { 0x90, 0x3C, 0x40 });
        p.Feed(stream.ToArray());
        Assert.Single(s.SysEx);
        Assert.Equal(reply, s.SysEx[0]);
        Assert.Equal(new[] { ((byte)0xF8, (byte)0, (byte)0), ((byte)0x90, (byte)0x3C, (byte)0x40) }, s.Short);
        Assert.False(p.InSysEx);
    }

    [Fact]
    public void Chunk_boundaries_are_irrelevant()
    {
        // Same stream fed 1 byte at a time must equal one-shot feeding, including a SysEx that spans chunks.
        var stream = new byte[] { 0x90, 0x3C, 0x40, 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7, 0x3E, 0x41, 0xB0, 0x01, 0x02, 0x03, 0x04 };
        var (pAll, sAll, _) = Make();
        pAll.Feed(stream);
        var (pOne, sOne, _) = Make();
        foreach (var b in stream) pOne.Feed(new[] { b });
        Assert.Equal(sAll.Short, sOne.Short);
        Assert.Equal(sAll.SysEx, sOne.SysEx);
        // F0 cancelled running status, so 3E 41 after the SysEx are stray; CC then runs status for the 2nd pair
        Assert.Equal(new[] { ((byte)0x90, (byte)0x3C, (byte)0x40), ((byte)0xB0, (byte)0x01, (byte)0x02), ((byte)0xB0, (byte)0x03, (byte)0x04) }, sAll.Short);
        Assert.Single(sAll.SysEx);
    }

    [Fact]
    public void SysEx_longer_than_the_stage_is_flushed_in_fragments_and_reassembled()
    {
        var (p, s, _) = Make(maxSysEx: 4096, stage: 16);
        var msg = new byte[100];
        msg[0] = 0xF0; for (int i = 1; i < 99; i++) msg[i] = (byte)(i & 0x7F); msg[99] = 0xF7;
        p.Feed(msg);
        Assert.Single(s.SysEx);
        Assert.Equal(msg, s.SysEx[0]);
    }

    [Fact]
    public void SysEx_over_cap_is_discarded_and_counted_then_parser_recovers()
    {
        var (p, s, reasm) = Make(maxSysEx: 64, stage: 16);
        var big = new byte[200];
        big[0] = 0xF0; big[199] = 0xF7;
        p.Feed(big);
        Assert.Empty(s.SysEx);
        Assert.Equal(1, reasm.DiscardedCount);
        p.Feed(new byte[] { 0xF0, 0x7D, 0x01, 0xF7 });
        Assert.Single(s.SysEx);
    }

    [Fact]
    public void Status_byte_inside_SysEx_aborts_it_and_is_processed_normally()
    {
        var (p, s, reasm) = Make();
        p.Feed(new byte[] { 0xF0, 0x7D, 0x01, 0x90, 0x3C, 0x40 });
        Assert.Empty(s.SysEx);
        Assert.Equal(1, reasm.DiscardedCount);
        Assert.Single(s.Short);
        Assert.Equal(((byte)0x90, (byte)0x3C, (byte)0x40), s.Short[0]);
    }

    [Fact]
    public void Reset_forgets_running_status_and_partial_SysEx()
    {
        var (p, s, reasm) = Make();
        p.Feed(new byte[] { 0xF0, 0x7D });
        p.Reset();
        Assert.Equal(1, reasm.DiscardedCount);
        p.Feed(new byte[] { 0x3C, 0x40 });   // no status in hand → dropped
        Assert.Empty(s.Short);
        p.Feed(new byte[] { 0x90, 0x3C, 0x40 });
        Assert.Single(s.Short);
    }
}