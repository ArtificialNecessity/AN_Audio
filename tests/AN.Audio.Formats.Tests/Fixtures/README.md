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
```

Files that are NOT here (licence): `clap-808.wav` (99Sounds) and the Salamander Grand Piano FLACs are exercised by
`LocalFileTests` (`[Trait("Category","Local")]`), which skip when the files are absent.