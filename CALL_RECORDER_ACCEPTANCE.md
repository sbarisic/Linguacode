# Native audio implementation acceptance — 2026-11-13

Implemented on `main`. The application now uses a bundled C# worker with native CPU engines. Setup downloads and verifies models only. Maintained build, test, setup, and runtime paths no longer invoke Python. The original extraction report is preserved in `docs/history/LINGUACODE_EXTRACTION_ACCEPTANCE.md`.

## Automated validation

- All 258 xUnit tests passed in both Debug and Release, with real-model tests enabled and no skips.
- Debug and Release Build/Clean/Rebuild succeeded on Windows x64 with .NET SDK 10.0.400.
- Application and audio-service package smoke checks run in C# through xUnit, covering existing desktop workflows and paired recording exports.
- Native setup self-tests passed for Whisper, YAMNet, and speaker diarization in isolated Debug and Release data directories.
- Real-model tests exercise recording preparation and analysis through the audio service, cached results, cancellation of each engine, worker reuse, restart after termination, and idle unloading.
- Decoder tests cover integer PCM and floating-point WAV, stereo/resampling, VBR MP3 encoder delay and range consistency, long recordings, gaps, malformed inputs, bounds, storage budgets, and atomic output ownership.
- Old analysis versions are explicitly rejected. Saved Python decoders are never executed. Service protocol is 5; worker protocol is 3; analysis format is 4.
- The complete worker is copied under `AudioWorker` and removed by Clean. Release output is approximately 1.29 GiB, including TensorFlow and app-local Visual C++ runtime libraries. Keep the complete output directory together.

## Speech and model comparisons

The committed fixtures contain the first twenty FLEURS test utterances for each language, with reference transcripts, checksums, attribution, and CC BY 4.0 licensing. Baselines were captured with the previous implementation before its removal. The initial four-utterance sample was expanded after Croatian narrowly missed the regression limit; no utterances were removed or selected by recognition quality. See `tests/fixtures/speech/README.md` for provenance and reproduction commands.

| Language | Reference words | Previous WER | Native WER | Change |
|---|---:|---:|---:|---:|
| English | 440 | 6.82% | 7.05% | +0.23 percentage points |
| Croatian | 454 | 32.38% | 31.28% | -1.10 percentage points |
| Slovenian | 349 | 49.28% | 49.28% | 0.00 percentage points |

All three pass the two-percentage-point regression limit. These are small regression samples, and the Croatian and Slovenian absolute error rates remain substantial.

For all sixty utterances, generated silence, and controlled overlapping speech, the complete YAMNet label map and aggregate/window scores passed comparison. Speaker turns and embeddings matched the baseline within 2 ms and 0.0001 respectively; score tolerance is 0.0001. Source-relative word bounds and decoder offsets were checked. Human-annotated word alignment has **not** been validated: the fixtures have reference text but no human word timestamps. The overlap fixture supplies controlled source timing rather than human alignment annotations.

The C# acceptance runner is `tests/Linguacode.Acceptance`. Full local reports and build/test logs are retained under ignored `tests/Linguacode.Tests/TestResults/native`.

## Remaining user acceptance

No VM is required or used. The user will test the application. Physical microphone/loopback capture, listening and word-alignment review, real meeting speaker quality, and live cloud-provider behavior remain unverified in this implementation run.

Launch `build/Release/Linguacode.exe`, complete model setup, and test offline recording, transcription, speaker separation, sound classification, playback, and export. Initial setup needs network access; inference is local afterward. Existing Python-era analyses must be recreated.

Visual Studio solution builds require Visual Studio 2026/MSBuild 18 with .NET 10 support. The installed Visual Studio 2022 cannot build .NET 10 projects, so full IDE/F5 validation remains pending. The native-download inline MSBuild task was separately exercised successfully under both dotnet MSBuild and the installed full-framework MSBuild.
