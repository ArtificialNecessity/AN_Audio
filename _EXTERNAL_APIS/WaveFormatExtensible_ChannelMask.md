# WAVEFORMATEXTENSIBLE.dwChannelMask — `SPEAKER_*` constants (ksmedia.h)

Ground truth for `AN.Audio.AudioChannelMask` (spec 50 D15). Values from the Windows SDK `ksmedia.h`;
the same numbering is used by WAVE_FORMAT_EXTENSIBLE files on every platform and by the FLAC/Ogg
channel-mask conventions that borrow it. Interleaved channel order = ascending bit order.

| Constant | Value |
|---|---|
| SPEAKER_FRONT_LEFT | 0x1 |
| SPEAKER_FRONT_RIGHT | 0x2 |
| SPEAKER_FRONT_CENTER | 0x4 |
| SPEAKER_LOW_FREQUENCY | 0x8 |
| SPEAKER_BACK_LEFT | 0x10 |
| SPEAKER_BACK_RIGHT | 0x20 |
| SPEAKER_FRONT_LEFT_OF_CENTER | 0x40 |
| SPEAKER_FRONT_RIGHT_OF_CENTER | 0x80 |
| SPEAKER_BACK_CENTER | 0x100 |
| SPEAKER_SIDE_LEFT | 0x200 |
| SPEAKER_SIDE_RIGHT | 0x400 |
| SPEAKER_TOP_CENTER | 0x800 |
| SPEAKER_TOP_FRONT_LEFT | 0x1000 |
| SPEAKER_TOP_FRONT_CENTER | 0x2000 |
| SPEAKER_TOP_FRONT_RIGHT | 0x4000 |
| SPEAKER_TOP_BACK_LEFT | 0x8000 |
| SPEAKER_TOP_BACK_CENTER | 0x10000 |
| SPEAKER_TOP_BACK_RIGHT | 0x20000 |
| SPEAKER_RESERVED | 0x7FFC0000 (bits 18–30) |
| SPEAKER_ALL | 0x80000000 |

Common layouts (`KSAUDIO_SPEAKER_*`):

| Layout | Mask |
|---|---|
| MONO | FRONT_CENTER |
| STEREO | FRONT_LEFT \| FRONT_RIGHT |
| QUAD | FL \| FR \| BL \| BR |
| SURROUND | FL \| FR \| FC \| BC |
| 5POINT1 | FL \| FR \| FC \| LFE \| BL \| BR |
| 5POINT1_SURROUND | FL \| FR \| FC \| LFE \| SL \| SR |
| 7POINT1 | 5POINT1 \| FLC \| FRC |
| 7POINT1_SURROUND | 5POINT1 \| SL \| SR |

SubFormat GUIDs (`KSDATAFORMAT_SUBTYPE_*`): PCM = `00000001-0000-0010-8000-00aa00389b71`,
IEEE_FLOAT = `00000003-0000-0010-8000-00aa00389b71`. The first 4 bytes equal the legacy `wFormatTag`.