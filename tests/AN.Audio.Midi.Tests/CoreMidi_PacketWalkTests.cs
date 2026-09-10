using AN.Audio.Midi.Platforms.MacOS;
using Xunit;

namespace AN.Audio.Midi.Tests;

/// <summary>
/// Exercises the MIDIPacketList walker + byte parser (SPEC-30 D38) with hand-built packet-list images on the running architecture.
/// Runs only on macOS: the port must be created against a real CoreMIDI client to obtain a callable ReadProc path.
/// </summary>
public unsafe class CoreMidi_PacketWalkTests
{
    private static bool OnMac => OperatingSystem.IsMacOS();

    /// <summary>Build a MIDIPacketList image with the header's layout and the CURRENT arch's MIDIPacketNext padding.</summary>
    private static byte[] BuildPacketList(params (ulong ts, byte[] data)[] packets)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((uint)packets.Length));
        foreach (var (ts, data) in packets)
        {
            int start = bytes.Count;
            bytes.AddRange(BitConverter.GetBytes(ts));
            bytes.AddRange(BitConverter.GetBytes((ushort)data.Length));
            bytes.AddRange(data);
            // pad exactly as PacketNext would advance
            fixed (byte* dummy = new byte[1])
            {
                byte* p0 = (byte*)0x1000 + start;
                long advance = CoreMidi_Interop.PacketNext(p0, (ushort)data.Length) - p0;
                while (bytes.Count - start < advance) bytes.Add(0);
            }
        }
        return bytes.ToArray();
    }

    private static (CoreMidi_MidiInput input, CoreMidi_MidiInPort port, List<MidiInput_Message> messages, List<byte[]> sysex) Harness()
    {
        var input = new CoreMidi_MidiInput(new MidiInput_Options { OpenPolicy = MidiInput_OpenPolicy.None, HotPlugSource = MidiInput_HotPlugSource.OsNotification });
        var messages = new List<MidiInput_Message>();
        var sysex = new List<byte[]>();
        input.SysExReceived += m => { lock (sysex) sysex.Add(m.Bytes.ToArray()); };
        input.Start((in MidiInput_Message m) => { lock (messages) messages.Add(m); });
        var port = new CoreMidi_MidiInPort(input, new MidiInput_Options());
        Assert.Equal(CoreMidi_Status.NoError, port.Create());
        return (input, port, messages, sysex);
    }

    [Fact]
    public void Multi_message_packet_and_two_packets_decode_in_order()
    {
        if (!OnMac) return;
        var (input, port, messages, _) = Harness();
        try
        {
            var entry = FakeEntry(1);
            var src = port.AttachForTest(0, entry);
            var image = BuildPacketList(
                (1000, new byte[] { 0x90, 0x3C, 0x40, 0x80, 0x3C, 0x00, 0xF8 }),   // NoteOn, NoteOff, Clock in one packet
                (2000, new byte[] { 0xE0, 0x00, 0x40, 0xC0, 0x05 }));             // PitchBend (3), ProgramChange (2)
            fixed (byte* p = image) port.WalkPacketListForTest(p, src, 123);

            Assert.Equal(5, messages.Count);
            Assert.True(messages[0].IsNoteOn); Assert.Equal(60, messages[0].Note.Number); Assert.Equal(64, messages[0].Velocity7.Value);
            Assert.True(messages[1].IsNoteOff);
            Assert.Equal(Midi_Status.TimingClock, messages[2].SystemStatus);
            Assert.Equal(Midi_MessageKind.PitchBend, messages[3].Kind); Assert.Equal(8192, messages[3].PitchBend14);
            Assert.Equal(Midi_MessageKind.ProgramChange, messages[4].Kind); Assert.Equal(5, messages[4].Program);
            Assert.All(messages, m => Assert.Equal(123, m.ArrivalTicks));
            // D36: DriverTimestamp = MIDITimeStamp scaled into Stopwatch (ns) ticks — exact, no anchor (41666 for 1000 mach ticks on Apple silicon)
            Assert.Equal(CoreMidi_Client.Instance.MachToStopwatchTicks(1000), messages[0].DriverTimestamp);
            Assert.Equal(CoreMidi_Client.Instance.MachToStopwatchTicks(2000), messages[3].DriverTimestamp);
        }
        finally { port.Dispose(); input.Dispose(); }
    }

    [Fact]
    public void SysEx_split_across_three_packets_with_interleaved_clock_reassembles_once()
    {
        if (!OnMac) return;
        var (input, port, messages, sysex) = Harness();
        try
        {
            var src = port.AttachForTest(0, FakeEntry(2));
            var image = BuildPacketList(
                (1, new byte[] { 0xF0, 0x7E, 0x7F }),
                (2, new byte[] { 0x06, 0xF8, 0x02, 0x47 }),   // real-time byte inside the SysEx stream
                (3, new byte[] { 0x5D, 0x00, 0x19, 0x00, 0x01, 0x42, 0x00, 0x00, 0xF7 }));
            fixed (byte* p = image) port.WalkPacketListForTest(p, src, 1);
            SpinWait.SpinUntil(() => { lock (sysex) return sysex.Count == 1; }, 2000);

            Assert.Single(messages); Assert.Equal(Midi_Status.TimingClock, messages[0].SystemStatus);
            Assert.Single(sysex);
            Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x47, 0x5D, 0x00, 0x19, 0x00, 0x01, 0x42, 0x00, 0x00, 0xF7 }, sysex[0]);
        }
        finally { port.Dispose(); input.Dispose(); }
    }

    [Fact]
    public void Truncated_packet_drops_tail_and_orphan_data_is_ignored()
    {
        if (!OnMac) return;
        var (input, port, messages, _) = Harness();
        try
        {
            var src = port.AttachForTest(0, FakeEntry(3));
            var image = BuildPacketList((1, new byte[] { 0x40, 0x40, 0xB0, 0x07, 0x7F, 0x90, 0x3C }));   // orphans, full CC, truncated NoteOn
            fixed (byte* p = image) port.WalkPacketListForTest(p, src, 1);
            Assert.Single(messages);
            Assert.Equal(Midi_Controller.Volume, messages[0].Controller); Assert.Equal(127, messages[0].ControllerValue7);
        }
        finally { port.Dispose(); input.Dispose(); }
    }

    private static CoreMidi_MidiInDeviceEntry FakeEntry(int uid) => new(
        Source: new CoreMidi_EndpointRef(0), UniqueId: new CoreMidi_UniqueId(uid), Name: $"fake {uid}", Manufacturer: null, Model: null,
        Key: new MidiInput_DeviceKey($"coremidi-uid:{uid}"), FallbackTypeId: MidiInput_DeviceTypeId.UnknownFromDriverStrings($"fake {uid}", null, null),
        PairedDestination: new CoreMidi_EndpointRef(0));
}