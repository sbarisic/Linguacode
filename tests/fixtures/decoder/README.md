# Decoder fixture

`tone-vbr.mp3` is synthetic audio generated for this repository, licensed under
the repository license. It contains four seconds of a 440 Hz sine wave at
48 kHz, encoded as stereo VBR MP3. It contains no recorded speech.

Generated with FFmpeg 8.1, using:

```
ffmpeg -f lavfi -i sine=frequency=440:sample_rate=48000:duration=4 -ac 2 -c:a libmp3lame -q:a 4 tone-vbr.mp3
```

SHA-256: `53bd47d74b2a29577dc4a07442baeee233fd687761b83fe71c2b28335272828c`.
The generator is not needed to build or test. The regression verifies that
encoder delay is removed and clip hashes match ranges of the full decoded audio.
