# Linguacode

Windows desktop app for recording calls, local transcription, translation, and cited summaries. Extracted from AgentSmith; builds and runs independently of it.

## Build and run

Requires Windows x64 and Visual Studio 2026 (MSBuild 18 or later) with the **.NET desktop development** workload, or .NET SDK 10.0.400 or a later 10.0.4xx servicing patch for command-line builds. Visual Studio 2022 cannot build this .NET 10 solution. The built app includes the .NET runtime.

Open `Linguacode.slnx`, set **Linguacode.Desktop** as the startup project, select **Debug** or **Release**, and use **Build Solution** (Ctrl+Shift+B). F5 starts the desktop app with its matching audio service available beside it. Both executables, their dependencies, the runtime, `AudioWorker`, `LocalAudio`, and the user guide are assembled by the project files into `build\Debug` or `build\Release`.

The equivalent command-line build uses the same projects:

```powershell
dotnet build .\Linguacode.slnx -c Release
if ($?) { & .\build\Release\Linguacode.exe }
```

Each configuration reuses its output folder and overwrites build outputs. **Clean Solution** removes tracked outputs; **Rebuild Solution** cleans and builds them again. Close the running app before rebuilding it. Keep the entire output folder together, including `Linguacode.AudioHost.exe`, `AudioWorker`, and `LocalAudio`. No publish script or separate publish step is needed.

The GUI manages its audio service and automatically prepares missing local models. Setup downloads and verifies models, then runs offline native-engine self-tests before activating them. Python is not required. Recording and transcription do not require a model-provider account. Summaries and translations use the provider configured in **Settings**.

## Data

New installations use `%LOCALAPPDATA%\Linguacode`. Recordings, model installations, settings, and accounts are outside the application folder and survive rebuilds. Windows protects stored credentials for the current user.

Existing AgentSmith recordings are supported. Old Python analysis formats are unsupported; create a new analysis from the retained recording. The app does not migrate those analyses. To explicitly reuse existing recordings:

```powershell
& .\build\Release\Linguacode.exe --data-dir "$env:LOCALAPPDATA\AgentSmith"
```

Close the old recorder and stop its audio service first. Do not run both recorder versions against the same data directory. The new service has a separate Linguacode pipe identity and requires a matching Linguacode build. Alternatively, use the default Linguacode directory for an independent installation.

## Source layout

- `Linguacode.Desktop`: WPF GUI, playback, waveform, and headless UI checks; produces `Linguacode.exe`.
- `Linguacode.Audio`: Windows capture, service protocol, model setup, analysis, evidence, and exports.
- `Linguacode.AudioWorker`: isolated CPU inference and WAV/MP3 decoding through Whisper.net, sherpa-onnx, TensorFlow C, and FFmpeg. Its complete runtime ships in `AudioWorker`.
- `Linguacode.AudioHost`: dedicated service executable and diagnostic commands; no AgentSmith CLI dependency.
- `Linguacode.Application`: recorder orchestration, settings, translation, and summary requests.
- `Linguacode.Core`, `Storage`, `OpenAI`, `LmStudio`: in-repository contracts, persistence, authentication, and provider clients.

The coding-agent runtime, code-analysis tools, and AgentSmith CLI are not required or shipped. The audio library retains its existing programmatic evidence API. No service is installed system-wide and administrator privileges are not requested.

## Verification

```powershell
dotnet test .\Linguacode.slnx -c Release
```

Tests generate disposable synthetic data in C#. They cover native decoding and the assembled desktop/service build, including headless WPF previews. They do not show windows, record devices, download models, or send provider requests. Model acceptance is a separate C# runner described in [the speech fixtures](tests/fixtures/speech/README.md).

The real-model pipeline and lifecycle tests are opt-in. Run `Linguacode.AudioHost.exe audio setup --data-dir <isolated-test-directory>`, set `LINGUACODE_NATIVE_TEST_DATA` to that directory, then run the tests in the same build configuration. These tests process a licensed fixture through the actual service and worker and exercise engine cancellation and worker lifecycle. Without that environment variable, they are reported as skipped.

Builds restore pinned NuGet dependencies and acquire hash-checked TensorFlow and LGPL FFmpeg archives through a C# MSBuild task. Archives are cached under `obj/native-audio`. Keep the complete `AudioWorker` and `LocalAudio` directories with the executables. See [third-party notices](THIRD_PARTY_NOTICES.md). Build output includes app-local Visual C++ runtime DLLs.

See [the user guide](CALL_RECORDER.md) and [acceptance notes](CALL_RECORDER_ACCEPTANCE.md). Documents under `docs/history` describe pre-extraction AgentSmith behavior and are not current command references.
