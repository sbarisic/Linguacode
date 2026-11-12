using Linguacode.Application;
using Linguacode.Core;
using Linguacode.Audio;
using Xunit;

namespace Linguacode.Tests;

public sealed class RecorderTranscriptionSettingsTests
{
    [Fact]
    public void InvalidOrUnpreparedRetranscriptionKeepsPriorWorkAndNames()
    {
        using var directory = new TestDirectory();
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"));
        var capture = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(recorder.CaptureDirectory(capture));
        var audio = Path.Combine(recorder.CaptureDirectory(capture), "fixture.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        var job = new RecordingAnalysisJob();
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        recorder.SaveMetadata(capture, new() { Title = "Saved call", AnalysisId = job.Id, SpeakerNames = new() { ["speaker1"] = "Old name" } });
        Assert.Throws<UserFacingException>(() => recorder.ResetTranscription(capture, "klingon"));
        Assert.Equal(job.Id, recorder.Metadata(capture).AnalysisId);
        Assert.True(File.Exists(recorder.Analysis.Store.FileFor(job.Id, "job.json")));
        Assert.Throws<UserFacingException>(() => recorder.ResetTranscription(capture, "hr"));
        var metadata = recorder.Metadata(capture);
        Assert.Equal(job.Id, metadata.AnalysisId);
        Assert.Equal("Old name", metadata.SpeakerNames["speaker1"]);
        Assert.Equal("auto", metadata.Language);
        Assert.Equal("Saved call", metadata.Title);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(audio));
        Assert.True(File.Exists(recorder.Analysis.Store.FileFor(job.Id, "job.json")));
    }

    [Fact]
    public void SourceLanguageIsRecordingLocalAndLockedForResume()
    {
        using var directory = new TestDirectory();
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"));
        var capture = Guid.NewGuid().ToString("N");
        recorder.SaveMetadata(capture, new() { Title = "Fixture", Language = "auto" });
        recorder.SetTranscriptionLanguage(capture, "hr");
        Assert.Equal("hr", recorder.Metadata(capture).Language);
        Assert.Equal("auto", recorder.Preferences.Language);
        Assert.Throws<UserFacingException>(() => recorder.SetTranscriptionLanguage(capture, "klingon"));
        Assert.Equal("hr", recorder.Metadata(capture).Language);
        var job = new RecordingAnalysisJob { Options = new(Language: "hr") };
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        var meta = recorder.Metadata(capture); meta.AnalysisId = job.Id; recorder.SaveMetadata(capture, meta);
        Assert.Throws<UserFacingException>(() => recorder.SetTranscriptionLanguage(capture, "en"));
        Assert.Equal("hr", recorder.Analysis.Store.Read(job.Id).Options.Language);
    }

    [Fact]
    public void DetectedLanguagesComeFromSuccessfulSpeechEvidenceAndNeverFromForcedLanguageScores()
    {
        using var directory = new TestDirectory();
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"));
        var capture = Guid.NewGuid().ToString("N");
        var job = new RecordingAnalysisJob { Chunks = [new() { Index = 0, TrackId = "call" }, new() { Index = 1, TrackId = "mic" }, new() { Index = 2, TrackId = "mic" }, new() { Index = 3 }, new() { Index = 4 }] };
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        recorder.SaveMetadata(capture, new() { AnalysisId = job.Id });
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "evidence-000000.json"), """{"transcription":{"status":"succeeded","language":"en","languageScore":0.78,"noSpeech":false}}""");
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "evidence-000001.json"), """{"transcription":{"status":"succeeded","language":"hr","languageScore":0.45,"noSpeech":true}}""");
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "evidence-000002.json"), """{"transcription":{"status":"succeeded","language":"sl","noSpeech":false}}""");
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "evidence-000003.json"), "{unfinished");
        var observations = recorder.TranscriptionLanguages(capture);
        Assert.Equal(2, observations.Length);
        Assert.Equal(new("call", "en", .78), observations[0]);
        Assert.Equal(new TranscriptLanguageObservation("mic", "sl", null), observations[1]);
        job.Options = job.Options with { Language = "en" }; recorder.Analysis.Store.Save(job);
        Assert.Empty(recorder.TranscriptionLanguages(capture));
    }
}
