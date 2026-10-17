using GitHub.Copilot;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class ModelCatalogTests
{
    private static ModelOption Option(string id, string? policy = "enabled") =>
        new(id, id.ToUpperInvariant(), policy, ["low", "high"], null, 200, 10, 1000);

    [Fact]
    public async Task Caches_list_per_token_variable()
    {
        var source = new FakeSource(() => [Option("claude-sonnet-5.5"), Option("gpt-5-mini")]);
        var catalog = new ModelCatalogService(source);

        await catalog.RefreshAsync("A");
        await catalog.RefreshAsync("A");
        Assert.Equal(1, source.Calls);
        Assert.Equal("GPT-5-MINI", catalog.Find("GPT-5-mini")!.Name); // lookup ignores case

        await catalog.RefreshAsync("B"); // different token variable → reload
        await catalog.RefreshAsync("B", force: true);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Failed_reload_keeps_previous_list()
    {
        var fail = false;
        var catalog = new ModelCatalogService(new FakeSource(() => fail ? throw new InvalidOperationException("offline") : [Option("m1")]));

        await catalog.RefreshAsync("A");
        fail = true;
        await catalog.RefreshAsync("A", force: true);

        Assert.Equal("offline", catalog.Error);
        Assert.Single(catalog.Models);
        Assert.False(catalog.IsLoading);
    }

    [Fact]
    public void Policy_state_controls_availability()
    {
        Assert.True(Option("a").IsEnabled);
        Assert.True(Option("b", policy: null).IsEnabled);
        Assert.False(Option("c", policy: "disabled").IsEnabled);
        Assert.Equal("200 / 1,000", Option("a").PriceLabel);
    }

#pragma warning disable GHCP001
    [Theory]
    [InlineData("ai_credits", 200d)]
    [InlineData("nano_aiu", 0.0000002d)]
    [InlineData(null, null)]
    public void Normalizes_prices_to_credits_per_million(string? unit, double? expectedInput)
    {
        var info = new ModelInfo
        {
            Id = "claude-sonnet-5.5",
            Name = "Claude Sonnet 5.5",
            SupportedReasoningEfforts = ["low", "max"],
            Billing = new ModelBilling
            {
                TokenPrices = new GitHub.Copilot.Rpc.ModelBillingTokenPrices { BatchSize = 1_000_000, InputPrice = 200, OutputPrice = 1000, CacheReadPrice = 10 }
            }
        };

        var option = CopilotAccountSource.ToOption(info, unit);

        Assert.Equal(expectedInput, option.InputCreditsPerMillion);
        Assert.Equal(["low", "max"], option.ReasoningEfforts);
    }
#pragma warning restore GHCP001

    private sealed class FakeSource(Func<IReadOnlyList<ModelOption>> list) : IModelCatalogSource
    {
        public int Calls;

        public Task<IReadOnlyList<ModelOption>> ListModelsAsync(string tokenVariable, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(list());
        }
    }
}
