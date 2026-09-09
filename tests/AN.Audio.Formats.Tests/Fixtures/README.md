# Test fixtures (spec 50)

All derived from `AssetSource/cartesia_tts_test.wav` (ours; mono, 44100 Hz, 16-bit PCM, 280576 frames). Rendered ONCE with
ffmpeg (chocolatey build, 2026-09-08) and committed; the command lines are the provenance. WAV shapes for the tolerance
tests are synthesised in memory by `Support/Wav_TestWriter.cs` and are not stored here.

```
# 16-bit FLAC (+ VORBIS_COMMENT tags)
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a flac -metadata title="Cartesia TTS test" -metadata artist="AN.Audio" tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test.flac

# 24-bit FLAC (Salamander-shaped depth; ffmpeg pads the 16-bit source to 24 valid bits)
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a flac -sample_fmt s32 -bits_per_raw_sample 24 -metadata title="Cartesia TTS test 24" tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test_24.flac

# 24-bit WAV master for the 24-bit FLAC comparison
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a pcm_s24le tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test_24.wav

# 16-bit WAV master = the source, copied
copy AssetSource\cartesia_tts_test.wav tests\AN.Audio.Formats.Tests\Fixtures\cartesia_tts_test.wav

# MP3 (Phase 3, ffmpeg 7.1.1 / libmp3lame, 2026-09-09)
# CBR 128k + ID3v2.3 + Info (Xing) block with LAME gapless data
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a libmp3lame -b:a 128k -metadata title="Cartesia TTS test" -metadata artist="AN.Audio" -id3v2_version 3 -write_xing 1 tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test.mp3
# VBR (-q:a 4) + ID3v2.4 + Xing block
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a libmp3lame -q:a 4 -metadata title="Cartesia TTS test VBR" -id3v2_version 4 tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test_vbr.mp3
# CBR 64k, NO Xing/Info block, NO tags at all (TotalFrames == null on a forward-only stream); the ID3v1 test appends its own 128-byte tag
ffmpeg -y -i AssetSource/cartesia_tts_test.wav -c:a libmp3lame -b:a 64k -write_xing 0 -id3v2_version 0 tests/AN.Audio.Formats.Tests/Fixtures/cartesia_tts_test_noxing.mp3
# ID3v2 tag shapes (v2.2/2.3/2.4, unsync, footer, extended header, APIC) are synthesised in memory by Support/Mp3_TestTagWriter.cs
```

Files that are NOT here (licence): `clap-808.wav` (99Sounds) and the Salamander Grand Piano FLACs are exercised by
`LocalFileTests` (`[Trait("Category","Local")]`), which skip when the files are absent.