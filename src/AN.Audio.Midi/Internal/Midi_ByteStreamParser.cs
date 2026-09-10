namespace AN.Audio.Midi.Internal;

/// <summary>Receives what <see cref="Midi_ByteStreamParser"/> decodes. Called synchronously from <see cref="Midi_ByteStreamParser.Feed"/>.</summary>
internal interface IMidi_ByteStreamSink
{
    /// <summary>One complete short message (channel voice, system common, or 1-byte real-time). Unused data bytes are 0.</summary>
    void OnShortMessage(byte status, byte data1, byte data2);

    /// <summary>One complete SysEx (F0..F7 inclusive), already reassembled and cap-checked. Valid for the duration of the call.</summary>
    void OnSysEx(ReadOnlySpan<byte> complete);
}

/// <summary>
/// Stateful MIDI 1.0 byte-stream parser for backends that deliver raw serial bytes (ALSA rawmidi). Handles running status,
/// System Real-Time bytes interleaved anywhere (including inside SysEx), system common with 0/1/2 data bytes, and SysEx
/// staged through a <see cref="Midi_SysExReassembler"/> so the D20 cap applies. Stray data bytes with no status in hand are
/// dropped (joined mid-message). Not thread-safe: one instance per port, fed from that port's reader thread only.
/// Allocation-free after construction.
/// </summary>
internal sealed class Midi_ByteStreamParser
{
    private readonly IMidi_ByteStreamSink _sink;
    private readonly Midi_SysExReassembler _reassembler;
    private readonly byte[] _sysExStage;
    private int _sysExStageLength;

    private byte _runningStatus;        // channel status in effect; 0 = none
    private byte _pendingSystemCommon;  // F1/F2/F3 awaiting data; 0 = none
    private int _pendingSystemNeed;
    private byte _data1;
    private int _dataCount;
    private bool _inSysEx;

    public Midi_ByteStreamParser(IMidi_ByteStreamSink sink, Midi_SysExReassembler reassembler, int sysExStageBytes = 256)
    {
        _sink = sink;
        _reassembler = reassembler;
        _sysExStage = new byte[Math.Max(16, sysExStageBytes)];
    }

    /// <summary>True between an F0 and its F7.</summary>
    public bool InSysEx => _inSysEx;

    /// <summary>Forget all state (port reset/close). A partial SysEx counts as discarded in the reassembler.</summary>
    public void Reset()
    {
        _runningStatus = 0;
        _pendingSystemCommon = 0;
        _dataCount = 0;
        AbortSysEx();
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= (byte)Midi_Status.TimingClock)   // F8..FF: real-time, legal anywhere, never disturbs other state
            {
                _sink.OnShortMessage(b, 0, 0);
                continue;
            }

            if (_inSysEx)
            {
                if (b < 0x80) { StageSysExByte(b); continue; }
                if (b == (byte)Midi_Status.SysExEnd)
                {
                    StageSysExByte(b);
                    FlushSysExStage();
                    _inSysEx = false;
                    continue;
                }
                // Any other status byte aborts the SysEx; fall through and treat it normally.
                AbortSysEx();
            }

            if (b >= 0x80)
            {
                if (b == (byte)Midi_Status.SysExStart)
                {
                    _runningStatus = 0;
                    _pendingSystemCommon = 0;
                    _inSysEx = true;
                    _sysExStageLength = 0;
                    StageSysExByte(b);
                    continue;
                }
                if (b >= 0xF0)   // system common F1..F7 cancels running status
                {
                    _runningStatus = 0;
                    _dataCount = 0;
                    int need = SystemCommonDataBytes(b);
                    if (need == 0) { _pendingSystemCommon = 0; _sink.OnShortMessage(b, 0, 0); }
                    else { _pendingSystemCommon = b; _pendingSystemNeed = need; }
                    continue;
                }
                _runningStatus = b;
                _pendingSystemCommon = 0;
                _dataCount = 0;
                continue;
            }

            // data byte
            if (_pendingSystemCommon != 0)
            {
                if (_pendingSystemNeed == 1) { _sink.OnShortMessage(_pendingSystemCommon, b, 0); _pendingSystemCommon = 0; }
                else if (_dataCount == 0) { _data1 = b; _dataCount = 1; }
                else { _sink.OnShortMessage(_pendingSystemCommon, _data1, b); _pendingSystemCommon = 0; _dataCount = 0; }
                continue;
            }
            if (_runningStatus == 0) continue;   // stray data byte

            if (ChannelDataBytes(_runningStatus) == 1) { _sink.OnShortMessage(_runningStatus, b, 0); }
            else if (_dataCount == 0) { _data1 = b; _dataCount = 1; }
            else { _sink.OnShortMessage(_runningStatus, _data1, b); _dataCount = 0; }   // running status persists
        }
    }

    private static int ChannelDataBytes(byte status) => (status & 0xF0) is (byte)Midi_Status.ProgramChange or (byte)Midi_Status.ChannelPressure ? 1 : 2;

    private static int SystemCommonDataBytes(byte status) => (Midi_Status)status switch
    {
        Midi_Status.MtcQuarterFrame or Midi_Status.SongSelect => 1,
        Midi_Status.SongPosition => 2,
        _ => 0,   // TuneRequest, undefined F4/F5, stray SysExEnd
    };

    private void StageSysExByte(byte b)
    {
        if (_sysExStageLength == _sysExStage.Length) FlushSysExStage();
        _sysExStage[_sysExStageLength++] = b;
    }

    /// <summary>Drop a SysEx in progress so the reassembler counts it (D20): bytes still in the stage are handed over first,
    /// otherwise a message aborted before its first flush would vanish uncounted.</summary>
    private void AbortSysEx()
    {
        if (_sysExStageLength > 0) { _reassembler.Append(_sysExStage.AsSpan(0, _sysExStageLength), out _); _sysExStageLength = 0; }
        _reassembler.Reset();
        _inSysEx = false;
    }

    /// <summary>Hand the staged fragment to the reassembler; it reports completion only when the fragment ends in F7.</summary>
    private void FlushSysExStage()
    {
        if (_sysExStageLength == 0) return;
        if (_reassembler.Append(_sysExStage.AsSpan(0, _sysExStageLength), out var whole))
            _sink.OnSysEx(whole);
        _sysExStageLength = 0;
    }
}