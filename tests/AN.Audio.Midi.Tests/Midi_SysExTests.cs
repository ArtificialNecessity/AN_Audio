using AN.Audio.Midi;
using AN.Audio.Midi.Internal;
using Xunit;

namespace AN.Audio.Midi.Tests;

public class Midi_SysExTests
{
    // Fixtures
    private static readonly byte[] ArturiaReply = [0xF0, 0x7E, 0x00, 0x06, 0x02, 0x00, 0x20, 0x6B, 0x02, 0x00, 0x05, 0x00, 0x01, 0x00, 0x02, 0x00, 0xF7];
    private static readonly byte[] RolandReply   = [0xF0, 0x7E, 0x10, 0x06, 0x02, 0x41, 0x0B, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF7];
    // Captured from an M-Audio Oxygen 49 MKV 2026-09-08: 3-byte manufacturer id but only TWO revision bytes (non-spec, must still parse).
    private static readonly byte[] OxygenReply   = [0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x00, 0x01, 0x05, 0x00, 0x02, 0x30, 0x30, 0x35, 0x30, 0xF7];

    [Fact]
    public void Request_bytes_match_spec()
    {
        Span<byte> req = stackalloc byte[Midi_IdentityReplyParser.RequestLength];
        Midi_IdentityReplyParser.WriteRequest(req);
        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 }, req.ToArray());
    }

    [Fact]
    public void Parses_three_byte_manufacturer_id()
    {
        Assert.True(Midi_IdentityReplyParser.TryParse(ArturiaReply, out var id));
        Assert.True(id.Manufacturer.IsExtended);
        Assert.Equal(0x206B, id.Manufacturer.Value);
        Assert.Equal(2, id.Family);
        Assert.Equal(5, id.Member);
        Assert.Equal((uint)(0x01 | (0x00 << 7) | (0x02 << 14)), id.SoftwareRevision);
    }

    [Fact]
    public void Parses_one_byte_manufacturer_id()
    {
        Assert.True(Midi_IdentityReplyParser.TryParse(RolandReply, out var id));
        Assert.False(id.Manufacturer.IsExtended);
        Assert.Equal(0x41, id.Manufacturer.Value);
        Assert.Equal(0x0B | (0x01 << 7), id.Family);
        Assert.Equal(3, id.Member);
    }

    [Fact]
    public void Rejects_non_identity_sysex()
    {
        Assert.False(Midi_IdentityReplyParser.TryParse([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7], out _));
        Assert.False(Midi_IdentityReplyParser.TryParse([0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7], out _));   // the request itself
        Assert.False(Midi_IdentityReplyParser.TryParse(ArturiaReply.AsSpan(0, 10), out _));             // truncated (no F7)
    }

    [Fact]
    public void Parses_MAudio_Oxygen_reply_with_short_revision()
    {
        Assert.True(Midi_IdentityReplyParser.TryParse(OxygenReply, out var id));
        Assert.True(id.Manufacturer.IsExtended);
        Assert.Equal(0x0105, id.Manufacturer.Value);
        Assert.Equal(0x00 | (0x02 << 7), id.Family);
        Assert.Equal(0x30 | (0x30 << 7), id.Member);
        Assert.Equal((uint)(0x35 | (0x30 << 7)), id.SoftwareRevision);
    }

    [Fact]
    public void Rejects_reply_with_no_revision_byte()
    {
        // 1-byte id, family, member, F7 — zero revision bytes
        Assert.False(Midi_IdentityReplyParser.TryParse([0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x41, 0x01, 0x00, 0x02, 0x00, 0xF7], out _));
    }

    [Fact]
    public void Bytes_after_the_fourth_revision_byte_become_the_extension()
    {
        // 1-byte id with 4 revision bytes + 1 vendor byte: length-agnostic prefix parse, the 5th byte is NOT a revision byte.
        Assert.True(Midi_IdentityReplyParser.TryParse([0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x41, 0x01, 0x00, 0x02, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0xF7], out var id));
        Assert.Equal((uint)(0x01 | (0x02 << 7) | (0x03 << 14) | (0x04 << 21)), id.SoftwareRevision);
        Assert.Equal(new byte[] { 0x05 }, id.Extension);
        Assert.Null(id.SerialNumber);   // Roland: no known extension layout
        // Spec-exact replies carry no extension.
        Assert.True(Midi_IdentityReplyParser.TryParse(RolandReply, out var roland));
        Assert.Empty(roland.Extension);
    }

    // Captured from an Akai MPK mini IV 2026-09-09 (all four of its ports answer identically): 1-byte id 0x47, family 93, member 25,
    // revision 01 04 01 00, then 00 00 00 00 + ASCII serial "E82605267968110" + 00 — 35 bytes. The old parser rejected it outright.
    private static readonly byte[] AkaiMpkMiniIvReply = Convert.FromHexString("F07E7F0602475D001900010401000000000045383236303532363739363831313000F7");

    [Fact]
    public void Parses_Akai_MPK_mini_IV_reply_with_serial_extension()
    {
        Assert.True(Midi_IdentityReplyParser.TryParse(AkaiMpkMiniIvReply, out var id));
        Assert.False(id.Manufacturer.IsExtended);
        Assert.Equal(0x47, id.Manufacturer.Value);
        Assert.Equal(93, id.Family);
        Assert.Equal(25, id.Member);
        Assert.Equal((uint)(0x01 | (0x04 << 7) | (0x01 << 14)), id.SoftwareRevision);
        Assert.Equal(20, id.Extension.Length);
        Assert.Equal("E82605267968110", id.SerialNumber);
        // The type id is the PREFIX only: a second unit (different serial) is the same model.
        var other = id with { Extension = [], SerialNumber = "OTHER" };
        Assert.Equal(MidiInput_DeviceTypeId.FromIdentity(id), MidiInput_DeviceTypeId.FromIdentity(other));
        Assert.Equal(id, other);
    }

    [Fact]
    public void TypeId_is_deterministic_and_differs_by_identity()
    {
        Midi_IdentityReplyParser.TryParse(ArturiaReply, out var a);
        Midi_IdentityReplyParser.TryParse(RolandReply, out var r);
        var ta1 = MidiInput_DeviceTypeId.FromIdentity(a);
        var ta2 = MidiInput_DeviceTypeId.FromIdentity(a);
        var tr = MidiInput_DeviceTypeId.FromIdentity(r);
        Assert.Equal(ta1, ta2);
        Assert.NotEqual(ta1, tr);
        Assert.False(ta1.IsUnknownType);
        var unknown = MidiInput_DeviceTypeId.UnknownFromDriverCaps("Foo MIDI", 1, 2);
        Assert.True(unknown.IsUnknownType);
        Assert.Equal(unknown, MidiInput_DeviceTypeId.UnknownFromDriverCaps("Foo MIDI", 1, 2));
    }

    [Fact]
    public void Reassembler_single_buffer()
    {
        var r = new Midi_SysExReassembler(16, 1024);
        Assert.True(r.Append(RolandReply, out var complete));
        Assert.Equal(RolandReply, complete.ToArray());
        Assert.Equal(0, r.DiscardedCount);
    }

    [Fact]
    public void Reassembler_joins_three_fragments()
    {
        var r = new Midi_SysExReassembler(4, 1024);
        Assert.False(r.Append(ArturiaReply.AsSpan(0, 6), out _));
        Assert.False(r.Append(ArturiaReply.AsSpan(6, 6), out _));
        Assert.True(r.Append(ArturiaReply.AsSpan(12), out var complete));
        Assert.Equal(ArturiaReply, complete.ToArray());
    }

    [Fact]
    public void Reassembler_aborted_message_is_discarded_and_new_one_parses()
    {
        var r = new Midi_SysExReassembler(16, 1024);
        Assert.False(r.Append(ArturiaReply.AsSpan(0, 8), out _));   // no F7
        Assert.True(r.Append(RolandReply, out var complete));         // fresh F0
        Assert.Equal(RolandReply, complete.ToArray());
        Assert.Equal(1, r.DiscardedCount);
    }

    [Fact]
    public void Reassembler_enforces_max_bytes()
    {
        var r = new Midi_SysExReassembler(16, 32);
        var big = new byte[40]; big[0] = 0xF0; big[^1] = 0xF7;
        Assert.False(r.Append(big.AsSpan(0, 20), out _));
        Assert.False(r.Append(big.AsSpan(20), out _));   // over cap → swallowed
        Assert.Equal(1, r.DiscardedCount);
        Assert.True(r.Append(RolandReply, out var ok));   // recovers
        Assert.Equal(RolandReply.Length, ok.Length);
    }

    [Fact]
    public void Reassembler_ignores_continuation_without_start()
    {
        var r = new Midi_SysExReassembler(16, 1024);
        Assert.False(r.Append([0x01, 0x02, 0xF7], out _));
        Assert.Equal(0, r.DiscardedCount);
    }
}