# 71 — C/C++ header → C# bindings autogen (the AN_Audio instance of the FluidUI BindingsCompiler pattern)

- **Status:** DRAFT 2026-09-13. First wanted surface: ASIO (`70_Asio_Output.md`). Designed so spec 60 Phase B (`alsa/pcm.h`), Phase C
  (`AudioUnit/*.h`, `CoreAudio/AudioHardware.h`) and spec 40 capture (`audioclient.h`) can be added as further wanted surfaces without
  touching the tool. `WasapiInterop.cs` stays hand-written until a separate decision migrates it (§8).
- **Parent:** `00_AN_Audio_Overview.md` rules 1 (every native constant is an enum, layout tests) and 2 (read the SDK header). This spec makes
  both rules *mechanical* instead of *disciplinary*.
- **Sibling pattern (read these first):** `C:\PROJECTS\AN_FluidUI\_TASKS_LINUX\11_Wayland_Bindings_Autogen.md`,
  `src/AN.WaylandBindings/README.md`, `src/AN.OSXBindings/BindingsCompiler/README.md`. Same pipeline, same commit boundary, same
  "generated C# is committed and never hand-edited" rule.

## 1. Why (each item is a bug class we have already paid for, here or in FluidUI)

| Hand-transcription failure | Where it bites in AN_Audio |
|---|---|
| vtable slot off by one / method missing | every COM/C++ vtable we dispatch by index (`IAudioClient3` 15–20 today, `IASIO` 3–23 next) |
| wrong struct packing / field width | `long` is **32-bit on Windows** (`ASIOBool`, `ASIOError`, `ASIOSamples.hi/lo`); `snd_pcm_uframes_t` is `unsigned long` = 64-bit on Linux x64; `#pragma pack(push,4)` in `asio.h` makes `ASIOTime` 148 B, not 152 |
| callback convention wrong | `ASIOCallbacks` are **cdecl**, `IASIO` methods are **thiscall**, WASAPI is stdcall — one attribute typo is a silent stack corruption on x86 |
| constants copied from a blog | `kAsio*` selectors are an unnumbered enum (implicit `+1` sequence); `ASE_*` are negative implicit sequences |

## 2. Decisions

- **D1 — Same pipeline, same boundary.** `extract` (explicit, needs the SDK on disk) → `CodeGen/Declarations.ytdata.hjson` (committed, faithful
  record) + `CodeGen/Extraction.report.json` (SHA-256 of every header read, tool + grammar hash, skips). `normalize` (build-time) →
  `CodeGen/Bindings.ytdata.hjson` → YeetCode `*.cs.ytmpl` → `Generated/*.generated.cs` (committed). Headers are **never** copied into
  the repo; the SDK stays in `C:\PROJECTS\3P_ASIOSDK` (3P_ convention), exactly as the macOS SDK stays outside OSXBindings.
- **D2 — Lives inside `src/AN.Audio/`** (user decision 2026-09-13): `src/AN.Audio/BindingsCompiler/` (tool, `ReferenceOutputAssembly=false`,
  never linked, never packed), `src/AN.Audio/CodeGen/`, `src/AN.Audio/Generated/`. Packaging rule (spec 30 D27 / 50 D15) untouched — no new
  package, no new inter-project reference.
- **D3 — Preprocessor IS evaluated, against an authored define set only.** Departure from the ObjC compiler's "no evaluation" rule, forced by
  `asio.h` (`#if NATIVE_INT64`, `#if IEEE754_64FLOAT`, `#if defined(_MSC_VER)`). `Wanted.ytdata.hjson` declares `defines: { _WIN32: 1,
  _MSC_VER: 1900, NATIVE_INT64: 0, IEEE754_64FLOAT: 1, ... }`; `#include` is followed only for headers listed in the wanted set (asiosys.h
  supplies `NATIVE_INT64 0` — the extractor must process it first or the define must be authored; we author it AND check the header agrees).
  Any `#if` referencing an **undeclared** symbol fails extraction (report lists it). No guessing.
- **D4 — The grammar is C with the C++ subset ASIO needs:** `typedef`, `struct`, anonymous and named `enum` (implicit increment, hex,
  `1 << n`), fixed arrays, function-pointer fields, `#pragma pack(push,N)/pop`, and `interface|class X : public IUnknown { virtual T m(args) = 0; }`.
  Everything else (templates, inline bodies, macros with bodies) is skipped and logged. **As built (§9 A1):** a hand-written tokenizer +
  recursive-descent parser (`CHeader_Parser.cs`), not a YeetCode PEG grammar; YeetCode is the template engine only.
- **D5 — ABI model in the normalizer, for both x64 AND x86.** AN.Audio is AnyCPU. The normalizer computes `sizeof`/`offsetof` under the
  declared target ABI (`msvc-x64`, `msvc-x86`; later `sysv-x64`, `darwin-arm64`) honouring `pack` and pointer width, and emits BOTH into
  `AssertLayouts()`; the runtime checks the one matching `IntPtr.Size`. Type map: `long`→`int`(msvc)/`nint`(sysv), `unsigned long`→`uint`/`nuint`,
  `char[N]`→`fixed byte[N]`, `void*`→`nint`, `double`→`double`, `T*`→`T*`.
- **D6 — Generated dispatch idiom = today's `WasapiInterop` idiom, derived.** Per C++ class: `const int IASIO_start = 7;` and
  `static ASIOError IASIO_start(nint self)` using `delegate* unmanaged[Thiscall]<nint, ..., ASIOError>` (or `[Stdcall]` when the class is
  declared `stdcall`/COM in Wanted). Per function-pointer typedef in a callbacks struct: the `delegate* unmanaged[Cdecl]<...>` field type and a
  `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` trampoline stub signature the hand-written runtime fills in. Calling
  convention comes from the header (`__stdcall`) or, for plain `virtual`, from Wanted (`callConv: thiscall`).
- **D7 — Oracle: a compiled ABI probe.** `tests/AN.Audio.Tests/Interop/abi-probe.cpp` includes the real headers, prints `sizeof`/`offsetof` for
  every generated struct and the vtable index of every method (via a derived class + member-pointer trick or `static_assert` table). The test
  compiles it with `cl.exe` (found via `vswhere`/known VS paths) or `clang++`, runs it, and diffs against `AssertLayouts()` constants. Skips
  (not fails) when neither compiler or the SDK is present — same as FluidUI's gcc probe. This is the independent check that D5's model is right.
- **D8 — Linguistic keying survives generation.** Generated names are scope-prefixed exactly as hand-written ones would be:
  `Asio_Error`, `Asio_SampleType`, `Asio_MessageSelector`, `Asio_ChannelInfo`, `Asio_BufferInfo`, `Asio_Callbacks`, `Asio_Time`. Wanted
  declares the prefix per surface (`csPrefix: "Asio_"`) and per-enum names for anonymous enums (`ASIOSTInt16MSB..` → `Asio_SampleType`).

## 3. Layout

```
src/AN.Audio/
├── BindingsCompiler/                      build-only tool (Exe, net10.0): extract | normalize
│   ├── AN.Audio.BindingsCompiler.csproj     refs ArtificialNecessity.YeetCode + YeetJson (same versions FluidUI pins)
│   ├── BindingsCompiler_Program.cs
│   ├── CHeader_Model.cs                     wanted-list, declarations and report records (the committed JSON shapes)
│   ├── CHeader_Preprocessor.cs              D3: comments → #if/#ifdef/#elif/#else/#endif/#define/#undef/#pragma pack; #include reported, not followed
│   ├── CHeader_Parser.cs                    D4 as built (§9 A1): tokenizer + recursive descent → declaration records (1:1, source line kept)
│   ├── CHeader_Extractor.cs                 wanted list × parsed headers → declarations + report; exactly-once rule; HJSON via YeetJson
│   ├── CHeader_AbiModel.cs                  D5: sizeof/offsetof per target ABI, C → C# type map
│   └── CHeader_DeclarationCompiler.cs       normalize: validate, name (D8), layouts for every ABI, vtable slots, delegate* signatures → render model
├── CodeGen/
│   ├── Wanted.ytdata.hjson                  §4
│   ├── Declarations.ytdata.hjson            committed boundary
│   ├── Extraction.report.json               hashes, skips, undeclared-symbol failures
│   ├── Bindings.ytdata.hjson                render model (build output, committed like FluidUI)
│   ├── Enums.cs.ytmpl  Structs.cs.ytmpl  Vtables.cs.ytmpl  Layouts.cs.ytmpl      (callbacks are fn-ptr struct fields, §9 A3)
├── Generated/
│   ├── Bindings.Enums.generated.cs  Bindings.Structs.generated.cs  Bindings.Vtables.generated.cs  Bindings.Layouts.generated.cs
└── Platforms/Windows/Asio/                hand-written runtime (spec 70)
```

`AN.Audio.csproj` gains the FluidUI targets verbatim in shape: `Compile Remove="CodeGen/**;BindingsCompiler/**"`, explicit
`Compile Include="Generated/*.generated.cs"`, `NormalizeAudioDeclarations` (Inputs Declarations → Outputs Bindings) and one
`YeetCodeTemplateTask` target per template, all `Condition="'$(DesignTimeBuild)' != 'true'"`. Windows-only? **No** — normalize/generate run on
every OS (they read committed hjson, not the SDK); only `extract` and the D7 probe need the SDK/compiler.

## 4. `Wanted.ytdata.hjson` (ASIO surface, illustrative)

```hjson
{
  surfaces: [{
    name: Asio, csPrefix: Asio_, csNamespace: AN.Audio.Platforms.Windows.Asio, sdkRoot: C:/PROJECTS/3P_ASIOSDK   // extract-time only; report stores hashes, not the path
    abis: [msvc-x64, msvc-x86]
    defines: { _WIN32: 1, _MSC_VER: 1930, WINDOWS: 1, NATIVE_INT64: 0, IEEE754_64FLOAT: 1 }
    headers: [common/asiosys.h, common/asio.h, common/iasiodrv.h]        // processed in order; #include of a listed header is followed, others ignored
    typedefs: [ASIOBool, ASIOError, ASIOSampleType, ASIOSampleRate, ASIOSamples, ASIOTimeStamp]
    structs:  [ASIOSamples, ASIOTimeStamp, ASIOClockSource, ASIOChannelInfo, ASIOBufferInfo, ASIOTimeCode, AsioTimeInfo, ASIOTime, ASIOCallbacks]
    enums: [
      { anonymousAfter: "typedef long ASIOSampleType", csName: Asio_SampleType }
      { anonymousAfter: "typedef long ASIOError",      csName: Asio_Error }
      { anonymousAfter: "// asioMessage selectors",     csName: Asio_MessageSelector }
      { anonymousAfter: "ASIOError ASIOFuture(long selector, void *params);", csName: Asio_FutureSelector }
      { name: ASIOTimeCodeFlags, csName: Asio_TimeCodeFlags, flags: true }, { name: AsioTimeInfoFlags, csName: Asio_TimeInfoFlags, flags: true }
    ]
    classes: [{ name: IASIO, base: IUnknown, baseSlots: 3, callConv: thiscall, csName: Asio_Driver }]
  }]
}
```

Anonymous enums are the ASIO norm; `anonymousAfter` anchors on the preceding declaration/comment line text, and extraction fails if the anchor
matches zero or several times (same "exactly once" rule as OSX selectors).

## 5. What is generated (ASIO), with the numbers the probe must confirm

| C | C# (Generated/) | msvc-x64 | msvc-x86 |
|---|---|---|---|
| `typedef long ASIOBool/ASIOError/ASIOSampleType` | `int`-backed enums `Asio_Bool`, `Asio_Error`, `Asio_SampleType` | 4 | 4 |
| `ASIOSamples {unsigned long hi, lo}` (NATIVE_INT64 0) | `struct Asio_Samples { uint Hi, Lo; long ToInt64() }` | 8 | 8 |
| `ASIOSampleRate` (IEEE754_64FLOAT 1) | `double` | 8 | 8 |
| `ASIOChannelInfo` | `Asio_ChannelInfo` (`fixed byte Name[32]`) | 52 | 52 |
| `ASIOBufferInfo` | `Asio_BufferInfo { Asio_Bool IsInput; int ChannelNum; nint Buffer0, Buffer1 }` | 24 | 16 |
| `ASIOClockSource` | `Asio_ClockSource` | 48 | 48 |
| `ASIOTimeCode` (pack 4!) | `Asio_TimeCode` | 84 | 84 |
| `AsioTimeInfo` | `Asio_TimeInfo` | 48 | 48 |
| `ASIOTime` | `Asio_Time` | 148 | 148 |
| `ASIOCallbacks` (4 cdecl fn ptrs) | `Asio_Callbacks` with `delegate* unmanaged[Cdecl]` fields | 32 | 16 |
| `IASIO` 21 virtuals after IUnknown | `Asio_Driver.Vtbl_init = 3 … Vtbl_outputReady = 23` + typed thiscall wrappers | — | — |

(`ASIODriverInfo` is deliberately NOT wanted: `IASIO::init(void*)` takes the HWND directly; the struct is the C-API layer's. Note for the
record: under pack(4) its `void* sysRef` lands at offset 164 → sizeof 172 on x64 — a fine probe case if we ever want it.)

## 6. Phases

- [ ] **P0 — Tool skeleton**: csproj, YeetCode/YeetJson refs (pin `ANYeetCodeVersion`/`ANYeetJsonVersion` in `AN.Audio.Build.props` to what
      FluidUI uses), `extract`/`normalize` CLI, report writer (deterministic, no absolute paths — replace `sdkRoot` with `<sdkRoot>`).
- [ ] **P1 — Preprocessor (D3) + grammar (D4)** with fixture tests in `tests/AN.Audio.Tests/BindingsCompiler/` on synthetic headers: pack
      push/pop, implicit enum sequences (`ASE_NotPresent=-1000, ASE_HWMalfunction…` → −999…), `1 << n`, hex, `char[N]`, fn-ptr fields, `interface X : public IUnknown`.
- [ ] **P2 — ABI model (D5)** with the §5 table as unit tests for both ABIs.
- [ ] **P3 — Templates + Generated/** for the ASIO surface; `AssertLayouts()`; `dotnet build` clean on Windows/macOS/Linux (generation is
      OS-independent).
- [ ] **P4 — Probe oracle (D7)**: `abi-probe.cpp`, compiler discovery, test diff; run on this box (VS 18 `cl.exe` present).
- [ ] **P5 — Docs**: `_EXTERNAL_APIS/ASIO_IASIO.md` is now *generated evidence*: the extraction report + this table; keep a short hand-written
      page that points at them and records SDK version/licence notes. `_PROJECT_STRUCTURE.md` gains the three folders.

## 7. Verification

- Unit: fixture headers → expected declarations (golden hjson); ABI model vs §5; normalizer rejects undeclared `#if` symbols, duplicate wanted
  names, unsupported constructs on the wanted list.
- Integration: `extract` against `3P_ASIOSDK` reproduces the committed `Declarations.ytdata.hjson` byte-for-byte (CI-style drift check when the
  SDK is present; skip otherwise). Probe oracle (D7) agrees with `AssertLayouts()` on x64; x86 via `cl.exe` Hostx64\x86 when available.
- `AssertLayouts()` runs once in `AsioAudioOutput`'s static constructor — mismatch throws before any driver call (FluidUI: at connect time).

## 8. Open questions / later

- Migrate `WasapiInterop.cs` (`audioclient.h`, `mmdeviceapi.h`) onto the generator? **Decided 2026-09-13: deferred until after ASIO ships.**
  Value: proves the COM/stdcall path and removes 500 hand lines; cost: churn in a working, hardware-validated backend.
- Windows SDK headers use `MIDL_INTERFACE("guid") IAudioClient3 : public IAudioClient2` + `STDMETHODCALLTYPE` macros — the grammar will
  need macro *recognition* (not expansion) for those two when that day comes.
- `sysv-x64`/`darwin-arm64` ABIs for spec 60 B/C: same model, different `long`/pack rules; add when those phases start.
- Steinberg ASIO SDK licence: we generate our own declarations from a locally-installed SDK and ship no SDK file; the README carries the
  required "ASIO is a trademark and software of Steinberg Media Technologies GmbH" line (spec 70 §7).

## 9. As built — deviations from the draft (2026-09-13)

| # | Draft said | Built | Why |
|---|---|---|---|
| A1 | D4: YeetCode PEG grammar `CHeader.grammar.yeet` | Hand-written tokenizer + recursive-descent (`CHeader_Parser.cs`); YeetCode remains the **template** engine | Same call the Wayland compiler made (XLinq over PEG): input is small and schema-stable, a hand parser is directly unit-testable with fixture headers, and no second grammar dialect to learn. |
| A2 | §4: anonymous enums anchored by `anonymousAfter` (preceding line text) | `firstEntry` (first enumerator name), exactly-once rule | The "preceding line" of `kAsioEnableTimeCodeRead`'s enum is `*/`; the first enumerator is unambiguous by construction. |
| A3 | §3: `Generated/Asio.*.generated.cs`, five templates incl. `Callbacks` | `Generated/Bindings.{Enums,Structs,Vtables,Layouts}.generated.cs`; callbacks are fn-ptr **fields** of `Asio_Callbacks` (`delegate* unmanaged[Cdecl]<…>`), so no separate template | One template run renders every surface into one file; the fn-ptr struct field IS the callback declaration. |
| A4 | §3: tool wired via `ProjectReference ReferenceOutputAssembly=false` | `Exec dotnet run --project BindingsCompiler -- normalize …`, once per build in the OUTER multi-TFM build (`_BindingsGenOnce`); `YeetCode.MSBuild` targets imported explicitly there (exact pin `ANYeetCodeMsBuildVersion`) | A net10.0 Exe referenced from net8.0/net9.0 inner builds trips restore; NuGet only imports package `build/*.targets` per TFM, never in the outer build. |
| A5 | D7: probe for x64 AND x86 | x64 only (the `Shadow` vtable trick relies on caller-cleanup; x86 thiscall stubs would need exact signatures) | The ABI model's x86 numbers are covered by unit tests against the §5 table; an x86 probe is a follow-up if x86 hardware ever matters. |
| A6 | Pointer arrays (`void* buffers[2]`) | Expanded to `Buffers0`, `Buffers1` (`nint`) with per-element offsets in `AssertLayouts` | C# fixed buffers allow primitives only. |

Probe evidence (this box, VS 18 `cl.exe`, SDK 2.3.x): `ASIOChannelInfo` 52, `ASIOBufferInfo` 24 (`buffers`@8), `ASIOTimeCode` 84 (`flags`@16, `future`@20),
`AsioTimeInfo` 48 (`sampleRate`@24, `reserved`@36), `ASIOTime` **148** (`timeInfo`@16, `timeCode`@64), `ASIOCallbacks` 32, `IASIO` slots `init`=3 … `outputReady`=23 —
identical to the model and to the hand-typed slots the spec-70 live probe used.