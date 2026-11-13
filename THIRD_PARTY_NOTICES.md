# Native audio dependencies

The app runs native audio processing locally on Windows x64. Model files download
at setup; native libraries ship in `AudioWorker`.

| Component | Version | License / source |
|---|---|---|
| Whisper.net and CPU runtime | 1.9.1 | MIT — https://github.com/sandrohanea/whisper.net |
| whisper.cpp and GGML | bundled by Whisper.net 1.9.1 | MIT — https://github.com/ggml-org/whisper.cpp |
| Whisper small multilingual Q8_0 | pinned in downloads.json | MIT — https://huggingface.co/ggerganov/whisper.cpp |
| Silero VAD 6.2.0 | pinned in downloads.json | MIT — https://github.com/snakers4/silero-vad |
| sherpa-onnx | 1.13.8 | Apache-2.0 — https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime | bundled by sherpa-onnx | MIT — https://github.com/microsoft/onnxruntime |
| pyannote segmentation 3.0 | existing pinned model | MIT; license included in the model archive |
| NVIDIA NeMo TitaNet small | existing pinned model | Apache-2.0 — https://catalog.ngc.nvidia.com/orgs/nvidia/nemo/models/titanet_small |
| TensorFlow C | 2.18.1 | Apache-2.0; bundled archive includes LICENSE and THIRD_PARTY_TF_C_LICENSES |
| Google YAMNet | Hub version 1 | Apache-2.0 — https://tfhub.dev/google/yamnet/1 |
| FFmpeg.AutoGen | 8.1.0 | MIT — https://github.com/Ruslan-B/FFmpeg.AutoGen |
| FFmpeg shared libraries | n8.1.3-35-gf1acb0a266 | LGPL build — https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-11-12-15-40 |
| SharpCompress | 0.50.9 | MIT — https://github.com/adamhathcock/sharpcompress |
| Microsoft Visual C++ v14 runtime | 14.51.36247.0 | Microsoft redistributable terms; supplied by ThinkGeo.Dependency.MicrosoftVisualCRunTime140 15.0.1 |

FFmpeg is dynamically linked; its DLLs can be replaced with ABI-compatible
libraries. Source and build scripts for the exact distribution are available
at the release above. This app does not prohibit reverse engineering needed to
debug modifications to LGPL components. Library licenses are retained under
`AudioWorker/licenses`.

Microsoft runtime terms:
https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/

Speech-test fixture attribution is in `tests/fixtures/speech/README.md`.
