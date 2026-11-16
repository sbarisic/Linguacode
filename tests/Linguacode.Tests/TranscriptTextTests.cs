using System.Text;
using System.Text.Json.Nodes;
using Linguacode.Audio;
using Linguacode.Core;
using Xunit;

namespace Linguacode.Tests;

public sealed class TranscriptTextTests
{
    private static JsonArray Words(params string[] text) => new(text.Select((value, i) => (JsonNode)new JsonObject
        { ["text"] = value, ["startSeconds"] = i + .25, ["endSeconds"] = i + .75 }).ToArray());

    [Theory]
    [InlineData("Čekaj, dođi.", " ��ekaj,", " do��i.")]
    [InlineData("čćđšž ČĆĐŠŽ", " ��ćđšž", " ČĆĐŠ��")]
    [InlineData("你好 世界", " ���好", " 世���")]
    [InlineData("Hello 😀!", " Hello", " ����!")]
    [InlineData("café déjà vu", " cafe�", "� déjà vu")]
    public void RestoresOriginalUnicodeWithoutChangingWordTimes(string original, string first, string second)
    {
        // The combining accent case crosses a fragment boundary that is not a word
        // boundary. The caller must first join subword tokens, as SpeechEngines does.
        var words = original.StartsWith("cafe", StringComparison.Ordinal) ? Words(first + second) : Words(first, second);
        var times = words.Select(w => (w!["startSeconds"]!.ToString(), w["endSeconds"]!.ToString())).ToArray();
        Assert.True(TranscriptText.RestoreWords(original, words));
        Assert.Equal(original, string.Concat(words.Select(w => w!["text"]!.ToString())).Trim());
        Assert.Equal(times, words.Select(w => (w!["startSeconds"]!.ToString(), w["endSeconds"]!.ToString())).ToArray());
        Assert.True(TranscriptText.RestoreWords(original, words));
    }

    [Theory]
    [InlineData("Čekaj")]
    [InlineData("dođi")]
    [InlineData("你好")]
    [InlineData("😀")]
    public void RecoversEveryUtf8ByteSplit(string original)
    {
        var bytes = Encoding.UTF8.GetBytes(original);
        for (var split = 1; split < bytes.Length; split++)
        {
            var separatelyDecoded = Encoding.UTF8.GetString(bytes[..split]) + Encoding.UTF8.GetString(bytes[split..]);
            var words = Words(" " + separatelyDecoded);
            Assert.True(TranscriptText.RestoreWords(original, words));
            Assert.Equal(" " + original, words[0]!["text"]!.ToString());
        }
    }

    [Fact]
    public void PreservesWhitespacePunctuationAndMultipleWordsInOneFragment()
    {
        var words = Words("\t��ekaj, [do��i]", "  sada!\n");
        Assert.True(TranscriptText.RestoreWords("Čekaj, [dođi]  sada!", words));
        Assert.Equal("\tČekaj, [dođi]", words[0]!["text"]!.ToString());
        Assert.Equal("  sada!\n", words[1]!["text"]!.ToString());
    }

    [Theory]
    [InlineData("Čekaj sada.", " ��ekaj", " kasnije.")]
    [InlineData("Cekaj sada.", " ��ekaj", " sada.")]
    [InlineData("Čekaj �.", " ��ekaj", " �.")]
    public void UnverifiableFragmentsAreNotGuessedOrPartiallyModified(string original, string first, string second)
    {
        var words = Words(first, second); var before = words.ToJsonString();
        Assert.False(TranscriptText.RestoreWords(original, words));
        Assert.Equal(before, words.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedEvidenceIsCorrectedBeforeAlignmentEvenWithoutDiarization(bool speakers)
    {
        var result = new JsonObject { ["transcription"] = new JsonObject { ["segments"] = new JsonArray(
            new JsonObject { ["text"] = "Čekaj, dođi.", ["words"] = Words(" ��ekaj,", " do��i.") }) } };
        if (speakers) result["diarization"] = new JsonObject { ["status"] = "succeeded", ["turns"] = new JsonArray(
            new JsonObject { ["startSeconds"] = 0d, ["endSeconds"] = 2d, ["speaker"] = "Speaker 1" }) };
        AudioReuse.Align(result);
        Assert.DoesNotContain("\\uFFFD", result.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        if (speakers) Assert.Equal(" Čekaj, dođi.", result["transcription"]!["speakerSegments"]![0]!["text"]!.ToString());
    }

    [Fact]
    public void IncompatibleWordTextFallsBackToIntactSegment()
    {
        var result = new JsonObject { ["transcription"] = new JsonObject { ["segments"] = new JsonArray(
            new JsonObject { ["text"] = "Čekaj sada.", ["startSeconds"] = 0, ["endSeconds"] = 2,
                ["words"] = Words(" ��ekaj", " kasnije.") }) } };
        AudioReuse.Align(result);
        var segment = result["transcription"]!["segments"]![0]!;
        Assert.Null(segment["words"]);
        Assert.Equal("Čekaj sada.", segment["text"]!.ToString());
        Assert.Equal(2, segment["endSeconds"]!.GetValue<int>());
    }
}
