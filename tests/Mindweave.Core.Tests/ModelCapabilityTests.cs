using Mindweave.Core.Models;

namespace Mindweave.Core.Tests;

public class ModelCapabilityTests
{
    [Fact]
    public void Each_capability_reads_and_sets_its_own_flag_only()
    {
        foreach (var capability in Enum.GetValues<ModelCapability>())
        {
            var on = ModelCapabilities.None.With(capability, true);
            Assert.True(on.Has(capability));
            Assert.All(Enum.GetValues<ModelCapability>().Where(c => c != capability), other => Assert.False(on.Has(other)));
            Assert.Equal(ModelCapabilities.None, on.With(capability, false));
        }
    }

    [Fact]
    public void Nothing_is_on_until_declared_and_the_protocol_only_promises_streaming()
    {
        Assert.All(Enum.GetValues<ModelCapability>(), c => Assert.False(ModelCapabilities.None.Has(c)));
        var defaults = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions);
        Assert.True(defaults.Has(ModelCapability.Streaming));
        Assert.False(defaults.Has(ModelCapability.ToolCalls));
        Assert.False(defaults.Has(ModelCapability.ImageInput));
    }

    [Fact]
    public void Changing_a_flag_keeps_the_context_capacity()
    {
        var declared = ModelCapabilities.None with { ContextWindowTokens = 128_000 };
        Assert.Equal(128_000, declared.With(ModelCapability.ImageInput, true).ContextWindowTokens);
    }
}
