using Linguacode.Core;
using Linguacode.Audio;
using Linguacode.Application;
using Xunit;

namespace Linguacode.Tests;

public sealed class StandaloneTests
{
    [Fact]
    public void DefaultDataAndServiceIdentityBelongToLinguacode()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linguacode"), LinguacodePaths.DataDirectory(null));
        using var data = new TestDirectory();
        Assert.Equal(Path.GetFullPath(data.Path), LinguacodePaths.DataDirectory(data.Path));
        Assert.StartsWith("Linguacode.Audio.", AudioEndpoint.Resolve(data.Path).Pipe);
    }

    [Fact]
    public void ApplicationAndAudioDoNotReferenceAgentSmithOrCodingRuntime()
    {
        foreach (var assembly in new[] { typeof(CallRecorder).Assembly, typeof(AudioServiceHost).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name!.StartsWith("AgentSmith", StringComparison.Ordinal) || reference.Name.EndsWith(".Runtime", StringComparison.Ordinal) && reference.Name != "System.Runtime");
    }
}
