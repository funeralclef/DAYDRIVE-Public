using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class LocalModelCatalogTests
{
    [Fact]
    public void EveryModelComesInQ5Only()
    {
        Assert.Equal(3, LocalModelCatalog.Models.Count);
        Assert.All(LocalModelCatalog.Models, model =>
        {
            Assert.Equal("Q5_K_M", model.Quant.Tag);
            Assert.True(model.Quant.ApproximateBytes > 1_000_000_000);
            Assert.Contains("Q5_K_M", model.FileName, StringComparison.Ordinal);
            // The tuner sizes the model from its shape until the file itself can be read.
            Assert.Equal(model.Quant.ApproximateBytes, model.Shape.WeightsBytes);
        });
        Assert.DoesNotContain(LocalModelCatalog.QuantTag, LocalModelCatalog.RetiredQuantTags);
    }

    [Fact]
    public void QwenIsTheRecommendedDefault()
    {
        Assert.Equal("qwen35-9b", LocalModelCatalog.DefaultModelId);
        var model = LocalModelCatalog.Find(null);
        Assert.True(model.IsRecommended);
        // A hybrid: one block in four keeps a cache, so a token costs 32 KiB rather than 144.
        Assert.Equal(32_768, model.Shape.KvBytesPerToken);
    }

    [Fact]
    public void TheDownloadIsTheQ5File()
    {
        var model = LocalModelCatalog.Find("qwen35-9b");

        Assert.Equal("Qwen3.5-9B-Q5_K_M.gguf", model.FileName);
        var uri = model.ModelDownloadUri;
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("huggingface.co", uri.Host);
        Assert.Contains("unsloth/Qwen3.5-9B-GGUF", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("Qwen3.5-9B-Q5_K_M.gguf", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyModelsWithAProjectorAdvertiseVision()
    {
        Assert.True(LocalModelCatalog.Find("qwen35-9b").SupportsVision);
        Assert.True(LocalModelCatalog.Find("ministral-8b-reasoning").SupportsVision);
        // The Granite repository ships no mmproj file, so vision must not be offered for it.
        var granite = LocalModelCatalog.Find("granite-4-8b");
        Assert.False(granite.SupportsVision);
        Assert.Null(granite.ProjectorDownloadUri);
        Assert.True(granite.IsExperimental);
    }

    [Theory]
    [InlineData("qwen35-9b", "qwen35-9b")]
    [InlineData("ministral-8b-reasoning", "ministral-8b-reasoning")]
    [InlineData("granite-4-8b", "granite-4-8b")]
    // Ids written by earlier releases must land on something real rather than throwing.
    [InlineData("lite", "qwen35-9b")]
    [InlineData("balanced", "qwen35-9b")]
    [InlineData("smart", "qwen35-9b")]
    [InlineData("Qwen3.5-4B", "qwen35-9b")]
    [InlineData("some-ministral-build", "ministral-8b-reasoning")]
    [InlineData("granite", "granite-4-8b")]
    [InlineData(null, "qwen35-9b")]
    [InlineData("", "qwen35-9b")]
    [InlineData("unknown_id", "qwen35-9b")]
    public void FindResolvesAliasesAndDefaults(string? input, string expectedId)
        => Assert.Equal(expectedId, LocalModelCatalog.Find(input).Id);

    [Fact]
    public void CustomEntryIsRecognisedAndNeverDownloaded()
    {
        Assert.True(LocalModelCatalog.IsCustom("custom"));
        Assert.Equal(LocalModelCatalog.CustomModelId, LocalModelCatalog.Find("custom").Id);
        Assert.True(new LocalModelSelection("custom").IsCustom);
        Assert.False(new LocalModelSelection("qwen35-9b").IsCustom);
    }
}
