# Linguacode

Windows desktop app for recording calls, local transcription, translation, and cited summaries. Extracted from AgentSmith; builds and runs independently of it.

## Build and run

Requires Windows x64 and the .NET 10 SDK to build. The published app includes the .NET runtime.

```powershell
.\publish.ps1
if ($?) { & .\artifacts\linguacode-win-x64\Linguacode.exe }
```

Use the same `artifacts\linguacode-win-x64` output folder for subsequent builds. Close the running app before updating it. Keep the entire published folder together, including `Linguacode.AudioHost.exe` and `LocalAudio`.

The GUI manages its audio service and automatically prepares missing local transcription engines. First setup downloads the private Python runtime and models; model weights are not in Git or the published package. Recording and transcription do not require a model-provider account. Summaries and translations use the provider configured in **Settings**.

## Data

New installations use `%LOCALAPPDATA%\Linguacode`. Recordings, model installations, settings, and accounts are outside the application folder and survive rebuilds. Windows protects stored credentials for the current user.

Existing AgentSmith recordings and analysis formats are supported. This source migration does not move, delete, or rewrite user data. To explicitly reuse the existing installation and recordings:

```powershell
& .\artifacts\linguacode-win-x64\Linguacode.exe --data-dir "$env:LOCALAPPDATA\AgentSmith"
```

Close the old recorder and stop its audio service first. Do not run both recorder versions against the same data directory. The new service has a separate Linguacode pipe identity and requires a matching Linguacode build. Alternatively, use the default Linguacode directory for an independent installation.

## Source layout

- `Linguacode.Desktop`: WPF GUI, playback, waveform, and headless UI checks; produces `Linguacode.exe`.
- `Linguacode.Audio`: Windows capture, service protocol, normalization, local engines, analysis, evidence, and exports, including the managed Python helper sources.
- `Linguacode.AudioHost`: dedicated service executable and diagnostic commands; no AgentSmith CLI dependency.
- `Linguacode.Application`: recorder orchestration, settings, translation, and summary requests.
- `Linguacode.Core`, `Storage`, `OpenAI`, `LmStudio`: in-repository contracts, persistence, authentication, and provider clients.

The coding-agent runtime, code-analysis tools, and AgentSmith CLI are not required or shipped. The audio library retains its existing programmatic evidence API. No service is installed system-wide and administrator privileges are not requested.

## Verification

```powershell
dotnet test .\Linguacode.slnx
python .\tests\prepare_recorder_ui_fixture.py .\tests\Linguacode.Tests\TestResults\recorder-ui
.\tests\recorder_publish_smoke.ps1 -Package .\artifacts\linguacode-win-x64 -Data .\tests\Linguacode.Tests\TestResults\recorder-ui
```

Generate the synthetic fixture in an empty test-data directory. The package check uses headless WPF previews and synthetic audio; it does not show windows, record devices, download models, or send provider requests. Python decoding tests use the managed audio Python environment: `python -I tests/local_audio_test.py` and `python -I tests/recording_decode_test.py`.

See [the user guide](CALL_RECORDER.md) and [acceptance notes](CALL_RECORDER_ACCEPTANCE.md). Documents under `docs/history` describe pre-extraction AgentSmith behavior and are not current command references.
