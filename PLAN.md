# Replace Python with native audio processing

## Summary

Keep transcription, speaker separation, sound classification, and WAV/MP3 support. Implement orchestration in C# with bundled native libraries. Remove Python from runtime, automatic setup, builds, fixtures, and tests.

Target Windows x64 with CPU inference. Preserve current user features; exact transcription output may change. No migration or compatibility layer for old Python analyses.

This document defines the native replacement's scope and acceptance criteria. Implementation and validation results are recorded in `CALL_RECORDER_ACCEPTANCE.md`. Final app acceptance belongs to the user; no VM testing is required.

## 1. Save the current build changes

- Commit this plan together with the current Visual Studio build changes on `main`.
- Include the script removals, Python smoke-check replacement, project changes, documentation, and ignore rules. Exclude generated output.
- Push to `origin/main` without force and verify the remote commit.
- Implement all changes directly on `main`. Push only to `origin/main`; do not use a separate implementation branch.

The pre-migration build changes passed Debug/Release builds, Clean/Rebuild, 234 tests, and the desktop/audio-service smoke check. See the acceptance notes for native implementation results.

## 2. Native engine choices

| Capability | Implementation |
|---|---|
| Transcription | **Whisper.net 1.9.1 + its CPU runtime**, backed by whisper.cpp, using multilingual Whisper small Q8_0 weights. |
| Voice activity detection | Whisper.net's **Silero VAD** support, with a pinned model. |
| Speaker separation | **sherpa-onnx 1.13.8 C# API**, retaining the current pyannote segmentation and TitaNet embedding models. |
| Sound classification | Existing **YAMNet SavedModel**, loaded through **TensorFlow C API 2.18.1** using a small C# interop layer. |
| Decoding/resampling | **FFmpeg.AutoGen 8.1.0** with matching Windows x64 LGPL shared libraries, replacing PyAV. |

These choices have native interfaces: [Whisper.net](https://github.com/sandrohanea/whisper.net/tree/1.9.1), [sherpa-onnx C# diarization](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/csharp.html), [TensorFlow C API](https://www.tensorflow.org/install/lang_c), and [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen).

- Preserve language selection/detection, language scores, spelling hints, source-relative word timestamps, and silence handling. Use beam size 5, four CPU threads, and no previous-text context. Map spelling hints to Whisper's initial prompt; reconstruct words from timed tokens and restore offsets after VAD.
- Retain speaker thresholds, anonymous naming, embedding aggregation, cross-chunk reconciliation, and microphone identity.
- Retain YAMNet's 521 labels, aggregate scores, 0.96-second windows, 0.48-second hops, and padding metadata. Direct SavedModel execution avoids a Python conversion pipeline.
- Use safe native-handle ownership and validate tensor shapes, timestamps, and finite values before accepting results.

## 3. Worker, decoding, and interfaces

- Add a self-contained **`Linguacode.AudioWorker`** C# executable to the solution. It owns native inference and decoding; the desktop and recording service remain outside that process.
- Replace Python launches with worker commands for inference, source preparation, and self-tests. Keep bounded request/result messages, progress reporting, component failures, and request identities.
- Retain serialized inference, model reuse, five-minute idle unloading, deadlines, and Windows job containment. Cancellation terminates the worker when necessary; the next request starts a fresh worker. Native crashes must not terminate recording.
- Keep `ILocalAudioBackend` and the existing audio operations. Replace helper-script fingerprints with engine/model/decoder identities; update status wording and increment the service protocol and analysis format.
- Consolidate clip decoding and full-recording preparation behind one C# decoder. Preserve 16 kHz mono float32 output, MP3 encoder-delay correction, resampler flushing, capture-format validation, timeline gaps, source mappings, storage limits, and atomic output promotion.
- Restrict decoding to local WAV/MP3 files with explicit demuxers. Preserve bounded-memory streaming for recordings longer than 120 seconds.
- Remove Python scripts, lockfiles, environment installation, Python-specific fallbacks, and saved `decoder.py` execution. Old analysis formats are unsupported; no migration work is included.

## 4. Setup and Visual Studio packaging

- Keep **Build Solution/F5** and `build\Debug` / `build\Release`.
- Package the worker and its dependencies in an `AudioWorker` subdirectory. MSBuild must copy and clean that complete directory correctly.
- Restore packaged dependencies through NuGet. Acquire remaining native distributions through a C# MSBuild task using immutable URLs and SHA-256 checks; cache them under `obj`. Do not introduce PowerShell or Python build steps.
- Bundle native DLLs, required app-local Visual C++ runtime dependencies, and third-party notices. Load libraries from controlled application paths.
- Change automatic setup to **model download and verification only**. Preserve progress, cancellation, retry, checksummed caches, installation locks, and activation only after native self-tests pass.
- Keep models and application data under `%LOCALAPPDATA%\Linguacode`, honoring `--data-dir`. New native generations must never execute or reuse Python environments.
- Replace all Python fixture generators and smoke checks with C# test utilities runnable through Visual Studio or `dotnet test`.

## 5. Verification and completion criteria

- Port existing Python decoding/helper tests to xUnit and retain the current application/service coverage.
- Exercise PCM formats, stereo downmixing, resampling, VBR MP3 delay, selected ranges, long recordings, missing chunks, invalid manifests, malformed inputs, cancellation, and disk-budget exhaustion.
- Verify transcription on fixed, licensed English, Croatian, and Slovenian speech fixtures, including names, silence, and overlapping speakers. Before removing the old implementation, capture baseline results for comparison.
- Require no more than a two-percentage-point absolute increase in word error rate per language. Verify word timestamps against annotated speech and preserve source alignment across VAD and chunk boundaries.
- Check YAMNet's complete label map and score/window outputs against baseline fixtures; check diarization turns and embedding reconciliation with the same models and settings.
- Test worker crashes, cancellation during each native engine, warm reuse, idle unloading, and recording while inference fails.
- Run automated tests, Build/Clean/Rebuild checks, and local native-engine smoke checks. No VM testing is required.
- The user will perform final app acceptance testing: launch Release output, complete setup, then verify offline recording, transcription, speaker separation, classification, playback, and export. Report this manual testing as pending until the user supplies results.
- Complete implementation when automated checks pass and no maintained build, test, setup, or runtime path invokes Python. Keep the user acceptance status separate from implementation completion.

**Defaults:** CPU inference only; models download on first setup; no GPU work, old-analysis migration, or Python fallback. Keeping native TensorFlow preserves classification behavior but means the application still includes a substantial native runtime.
