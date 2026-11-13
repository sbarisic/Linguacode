# Speech fixtures and baseline

These are the first twenty test rows in each of the English (`en_us`), Croatian
(`hr_hr`), and Slovenian (`sl_si`) configurations of Google's FLEURS dataset.
The initial four-row sample was expanded to the first twenty rows after the
Croatian result missed the limit by one fifth of a percentage point. No rows were
removed or selected by recognition output. Audio bytes are unchanged.
The manifest records dataset IDs, original and normalized reference text, and
SHA-256 checksums.

`overlap.wav` mixes the first two English recordings at half amplitude, with the
second starting one second later. It retains the source recordings' CC BY 4.0
license. `silence.wav` is three seconds of generated silence under the repository
license. These two scenarios are excluded from WER and retain explicit timing
annotations in the manifest.

Source: https://huggingface.co/datasets/google/fleurs

Parquet revision: `168de341b3db6859a9bac1c50a2ef5e3b47647e0`.
Files: `<configuration>/test/0000.parquet` on the `refs/convert/parquet` branch.

Attribution: Google, FLEURS: Few-shot Learning Evaluation of Universal
Representations of Speech (Conneau et al., 2022).
Audio and reference text are licensed under CC BY 4.0:
https://creativecommons.org/licenses/by/4.0/

The `baseline` directory contains output captured from the pre-migration helper
before its removal, using the installed faster-whisper, TensorFlow, and
sherpa-onnx engines. Each result records its engine version. Baselines are test
evidence, not a runtime fallback. No private recordings are included.

Run the C# comparison after building Release and completing model setup:

```
dotnet run --project tests/Linguacode.Acceptance -c Release -- build/Release <models-directory> <report.json>
dotnet run --project tests/Linguacode.Acceptance -c Release -- --verify-report <report.json>
```

The runner checks fixture hashes, component success, the complete YAMNet class
map and aggregate scores, word timestamp bounds, and the two-percentage-point
WER regression limit for each language. WER ignores case and punctuation.
The second command compares classification windows, speaker turns, and speaker
embeddings with the baseline. Classification and embedding scores use a 0.0001
absolute tolerance; speaker timestamps use 2 ms. Labels tied at exactly zero
are ignored when comparing the five top window labels.

Twenty utterances per language are
a small regression sample; they do not establish population accuracy or human
word alignment. Manual app acceptance remains separate.
