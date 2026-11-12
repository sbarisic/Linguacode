# Linguacode extraction acceptance — 2026-11-12

Migrated the current AgentSmith recorder working tree, including uncommitted fixes, into Linguacode. The source baseline was AgentSmith `16a9cbe`; this migration is not a checkout of only that commit.

Verified locally on Windows x64:

- Independent solution builds without an AgentSmith project reference or coding-agent runtime.
- 232 migrated application/audio/provider tests plus 2 standalone identity/dependency tests pass.
- Managed Python helper tests: 8 passed; streaming recording decoder tests: 5 passed. Synthetic fixtures only; no model download or real inference.
- Self-contained `artifacts/linguacode-win-x64` package contains `Linguacode.exe`, `Linguacode.AudioHost.exe`, matching audio assemblies, and helper source.
- Headless package checks pass with no window shown: device refresh, setup recovery, explicit transcription, formatted summaries, transcript search, waveform/zoom/accessibility/cancellation, replay, close, and export.
- Headless light/dark/high-contrast previews and normal/minimum size/scaling checks pass. Evidence is in ignored `tests/Linguacode.Tests/TestResults/recorder-ui`.
- Matching packaged service starts, reads synthetic paired recordings, exports both tracks, and stops. The check ran without elevation (service protocol 4).
- AgentSmith's remaining 490 unit tests and 368 integration tests pass, plus the new audio-removal regression test. One cancellation test failed in the first concurrent run and passed both individually and in the complete unit-suite rerun; no unrelated runtime fix was made.

Reproduce with the commands in [README](README.md). Close Linguacode before overwriting its published binaries.

No live microphone capture, Bluetooth/Teams/VM call, language-accuracy assessment, cloud-provider request, installer, or automatic-update test was performed for this extraction. Historical acceptance is retained under `docs/history`; it is not a claim that those scenarios were rerun here.
