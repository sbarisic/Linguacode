# Linguacode

Windows desktop app for recording calls, local transcription, translation, and cited summaries. Extracted from AgentSmith; builds and runs independently of it.

## Build and run

Requires Windows x64 and Visual Studio with .NET 10 support and the **.NET desktop development** workload. The built app includes the .NET runtime.

Open `Linguacode.slnx`, set **Linguacode.Desktop** as the startup project, select **Debug** or **Release**, and use **Build Solution** (Ctrl+Shift+B). F5 starts the desktop app with its matching audio service available beside it. Both executables, their dependencies, the runtime, `LocalAudio`, and the user guide are assembled by the project files into `build\Debug` or `build\Release`.

The equivalent command-line build uses the same projects:

```powershell
dotnet build .\Linguacode.slnx -c Release
if ($?) { & .\build\Release\Linguacode.exe }
```

Each configuration reuses its output folder and overwrites build outputs. **Clean Solution** removes tracked outputs; **Rebuild Solution** cleans and builds them again. Close the running app before rebuilding it. Keep the entire output folder together, including `Linguacode.AudioHost.exe` and `LocalAudio`. No publish script or separate publish step is needed.

The GUI manages its audio service and automatically prepares missing local transcription engines. First setup downloads the private Python runtime and models; model weights are not in Git or the published package. Recording and transcription do not require a model-provider account. Summaries and translations use the provider configured in **Settings**.

## Data

New installations use `%LOCALAPPDATA%\Linguacode`. Recordings, model installations, settings, and accounts are outside the application folder and survive rebuilds. Windows protects stored credentials for the current user.

Existing AgentSmith recordings and analysis formats are supported. This source migration does not move, delete, or rewrite user data. To explicitly reuse the existing installation and recordings:

```powershell
& .\build\Release\Linguacode.exe --data-dir "$env:LOCALAPPDATA\AgentSmith"
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
python .\tests\recorder_build_smoke.py --build .\build\Release --data .\tests\Linguacode.Tests\TestResults\recorder-ui
```

Generate the synthetic fixture in an empty test-data directory. The package check uses headless WPF previews and synthetic audio; it does not show windows, record devices, download models, or send provider requests. Python decoding tests use the managed audio Python environment: `python -I tests/local_audio_test.py` and `python -I tests/recording_decode_test.py`.

See [the user guide](CALL_RECORDER.md) and [acceptance notes](CALL_RECORDER_ACCEPTANCE.md). Documents under `docs/history` describe pre-extraction AgentSmith behavior and are not current command references.
