using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Linguacode.Application;
using Linguacode.Core;
using Linguacode.Audio;
using Xunit;

namespace Linguacode.Tests;

public sealed class RecorderTranslationTests
{
    [Fact]
    public async Task ChosenTranslationLanguageIsIndependentOfSummaryAndHasItsOwnCache()
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer();
        using var recorder = Fixture(directory, handler, "Good morning", out var capture, out _);
        Assert.Equal("en", recorder.Preferences.SummaryLanguage);
        Assert.StartsWith("Translation · Croatian:", await recorder.Translate(capture, "one", "hr", default));
        Assert.Contains("Croatian", handler.Body);
        Assert.StartsWith("Translation · Slovenian:", await recorder.Translate(capture, "one", "sl", default));
        Assert.Contains("Slovenian", handler.Body);
        Assert.Equal(2, handler.Generations);
        await recorder.Translate(capture, "one", "hr", default);
        Assert.Equal(2, handler.Generations);
        Assert.Equal("en", recorder.Preferences.SummaryLanguage);
        await Assert.ThrowsAsync<UserFacingException>(() => recorder.Translate(capture, "one", "auto", default));
    }

    [Fact]
    public async Task TranslationUsesOnlySelectedPassageAndCachesBySourceLanguageAndAgent()
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer();
        using var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"), handler);
        recorder.SavePreferences(new() { SummaryAgent = new("fixture", ModelProvider.Local, "http://localhost:8080/v1", "", 8192), SummaryLanguage = "hr" });
        var job = new RecordingAnalysisJob();
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        var capture = Guid.NewGuid().ToString("N"); recorder.SaveMetadata(capture, new() { AnalysisId = job.Id });
        var path = recorder.Analysis.Store.FileFor(job.Id, "transcript.jsonl");
        const string evidence = """
            {"id":"one","startSeconds":1.125,"endSeconds":2.25,"speaker":"speaker","text":"Good morning"}
            {"id":"two","startSeconds":3,"endSeconds":4,"speaker":"speaker","text":"PRIVATE OTHER PASSAGE"}
            """;
        File.WriteAllText(path, evidence);
        Assert.Equal("Translation · Croatian:\nDobro jutro", await recorder.Translate(capture, "one", default));
        Assert.Contains("Good morning", handler.Body);
        Assert.Contains("hr", handler.Body);
        Assert.DoesNotContain("PRIVATE OTHER PASSAGE", handler.Body);
        var request = JsonNode.Parse(handler.Body)!;
        Assert.True(request["tools"] is null || request["tools"]!.AsArray().Count == 0);
        Assert.Equal("Translation · Croatian:\nDobro jutro", await recorder.Translate(capture, "one", default));
        Assert.Equal(1, handler.Generations);
        recorder.Preferences.SummaryLanguage = "sl";
        handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => recorder.Translate(capture, "one", default));
        Assert.Single(Directory.GetFiles(recorder.Analysis.Store.DirectoryFor(job.Id), "translation-*.json"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.Translate(capture, "one", cancellation.Token));
        Assert.Equal(evidence, File.ReadAllText(path));
        var row = recorder.Transcript(capture)[0];
        Assert.NotEqual(CallRecorder.TranslationKey(row, "hr", "agent-a"), CallRecorder.TranslationKey(row, "hr", "agent-b"));
        Assert.NotEqual(CallRecorder.TranslationKey(row, "hr", "agent-a"), CallRecorder.TranslationKey(row with { Text = "Changed" }, "hr", "agent-a"));
    }

    [Theory]
    [InlineData("{\"status\":\"unable\",\"translation\":\"\"}", "could not reliably")]
    [InlineData("{\"status\":\"translated\",\"translation\":\"GOOD MORNING!\"}", "repeated the original")]
    [InlineData("{\"status\":\"translated\",\"translation\":\"\"}", "no translation")]
    [InlineData("Good morning", "invalid translation response")]
    [InlineData("{\"status\":\"guessed\",\"translation\":\"hello\"}", "invalid translation response")]
    [InlineData("{\"status\":\"translated\",\"translation\":23}", "invalid translation response")]
    public async Task InvalidOrUntranslatableResponsesAreNotCached(string response, string message)
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer { Response = response };
        using var recorder = Fixture(directory, handler, "Good morning", out var capture, out var job);
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<UserFacingException>(() => recorder.Translate(capture, "one", default));
            Assert.Contains(message, error.Message);
        }
        Assert.Equal(2, handler.Generations);
        Assert.Empty(Directory.GetFiles(recorder.Analysis.Store.DirectoryFor(job.Id), "translation-*.json"));
    }

    [Fact]
    public async Task AlreadyTargetIsExplicitAndCachedWithoutDuplicatingPassage()
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer { Response = """{"status":"already_target","translation":""}""" };
        using var recorder = Fixture(directory, handler, "Good morning", out var capture, out _);
        Assert.Equal("The model reports this passage is already in English.", await recorder.Translate(capture, "one", default));
        Assert.Equal("The model reports this passage is already in English.", await recorder.Translate(capture, "one", default));
        Assert.Equal(1, handler.Generations);
    }

    [Fact]
    public async Task NearCopyIsClearlyFlaggedAndCanBeRetried()
    {
        using var directory = new TestDirectory();
        const string original = "Lord Nehma, ye too, re-nech, chul-madz, kov-dak-setsh, ken-weh-ma, hot-tah, divy, yot-weh, re-nech, da-lech, di-kalech.";
        var response = original.Replace("ye too", "you too");
        var handler = new TranslationServer { Response = new JsonObject { ["status"] = "translated", ["translation"] = response }.ToJsonString() };
        using var recorder = Fixture(directory, handler, original, out var capture, out var job);
        for (var i = 0; i < 2; i++)
        {
            var result = await recorder.Translate(capture, "one", default);
            Assert.StartsWith("Result is mostly unchanged", result);
            Assert.Contains(response, result);
        }
        Assert.Equal(2, handler.Generations);
        Assert.Empty(Directory.GetFiles(recorder.Analysis.Store.DirectoryFor(job.Id), "translation-*.json"));
        Assert.Equal(original, recorder.Transcript(capture)[0].Text);
    }

    [Fact]
    public async Task OldPlainTextCacheIsBypassedAndFencedJsonIsAccepted()
    {
        using var directory = new TestDirectory();
        var handler = new TranslationServer { Response = "```json\n{\"status\":\"translated\",\"translation\":\"Good morning\"}\n```" };
        using var recorder = Fixture(directory, handler, "Dobro jutro", out var capture, out var job);
        var row = recorder.Transcript(capture)[0];
        var agent = CallRecorder.AgentIdentity(recorder.SummaryAgent());
        var oldKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { row.Id, row.Text, language = "en", agent }))));
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "translation-" + oldKey + ".json"), "\"Dobro jutro\"");
        Assert.Equal("Translation · English:\nGood morning", await recorder.Translate(capture, "one", default));
        Assert.Equal(1, handler.Generations);
    }

    private static CallRecorder Fixture(TestDirectory directory, TranslationServer handler, string text, out string capture, out RecordingAnalysisJob job)
    {
        var recorder = new CallRecorder(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"), handler);
        recorder.SavePreferences(new() { SummaryAgent = new("fixture", ModelProvider.Local, "http://localhost:8080/v1", "", 8192), SummaryLanguage = "en" });
        job = new();
        Directory.CreateDirectory(recorder.Analysis.Store.DirectoryFor(job.Id)); recorder.Analysis.Store.Save(job);
        capture = Guid.NewGuid().ToString("N"); recorder.SaveMetadata(capture, new() { AnalysisId = job.Id });
        File.WriteAllText(recorder.Analysis.Store.FileFor(job.Id, "transcript.jsonl"), new JsonObject {
            ["id"] = "one", ["startSeconds"] = 1, ["endSeconds"] = 3, ["speaker"] = "speaker", ["text"] = text }.ToJsonString());
        return recorder;
    }

    private sealed class TranslationServer : HttpMessageHandler
    {
        public string Body = "";
        public int Generations;
        public bool Fail;
        public string Response = """{"status":"translated","translation":"Dobro jutro"}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("Fixture offline");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\",\"meta\":{\"n_ctx\":32768}}]}") };
            if (path != "/v1/chat/completions") return new(HttpStatusCode.NotFound);
            Body = await request.Content!.ReadAsStringAsync(ct); Generations++;
            var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = Response }, ["finish_reason"] = null }) };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + chunk.ToJsonString() + "\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
