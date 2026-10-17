using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class QuotaTests
{
    private static readonly AccountQuota Premium = new("premium_interactions", 120, 300, false, 60, 0, null);

    [Fact]
    public async Task Refresh_stores_quota_and_throttles_automatic_refreshes()
    {
        var source = new FakeSource(() => [Premium]);
        var service = new AccountQuotaService(source) { TokenVariable = "X" };

        await service.RefreshAsync();
        await service.RefreshAsync(force: false);

        Assert.Equal(1, source.Calls);
        Assert.Equal(120, service.Current.Primary!.Used);
        Assert.False(service.Current.IsRefreshing);

        await service.RefreshAsync(force: true);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Failed_refresh_keeps_last_numbers_and_reports_error()
    {
        var fail = false;
        var service = new AccountQuotaService(new FakeSource(() => fail ? throw new InvalidOperationException("boom") : [Premium]))
        {
            TokenVariable = "X"
        };

        await service.RefreshAsync();
        fail = true;
        await service.RefreshAsync();

        Assert.Equal("boom", service.Current.Error);
        Assert.Equal(120, service.Current.Primary!.Used);
    }

    [Fact]
    public async Task No_token_variable_means_no_call()
    {
        var source = new FakeSource(() => [Premium]);
        await new AccountQuotaService(source).RefreshAsync();
        Assert.Equal(0, source.Calls);
    }

#pragma warning disable GHCP001
    [Fact]
    public void Maps_session_snapshots_and_detects_credit_billing()
    {
        var mapped = CopilotAccountSource.Map(null, new Dictionary<string, GitHub.Copilot.Rpc.SessionQuotaSnapshot>
        {
            ["chat"] = new() { IsUnlimitedEntitlement = true, UsedRequests = 5, RemainingPercentage = 100 },
            ["premium_interactions"] = new() { EntitlementRequests = 1500, UsedRequests = 19.1, RemainingPercentage = 98.7, TokenBasedBilling = true }
        });

        Assert.Equal("premium_interactions", mapped[0].Name);
        Assert.True(mapped[0].IsCredits);
        Assert.Equal("Credits", mapped[0].DisplayName);
        Assert.Equal(19.1, mapped[0].Used);
        Assert.True(mapped[1].IsUnlimited);
    }
#pragma warning restore GHCP001

    [Fact]
    public void Extracts_reason_from_runtime_error()
    {
        const string message = "Request models.list failed with message: GenericFailure, {\"kind\":\"http\",\"status\":401,\"body\":\"checking third-party user token: unauthorized: Personal Access Token does not have \\\"Copilot Requests\\\" permission\n\",\"hasRequestId\":true}";
        Assert.Equal("unauthorized: Personal Access Token does not have \"Copilot Requests\" permission",
            CopilotClients.ExtractReason(message));
        Assert.Equal("plain error", CopilotClients.ExtractReason("plain error"));
    }

    private sealed class FakeSource(Func<IReadOnlyList<AccountQuota>> fetch) : IAccountQuotaSource
    {
        public int Calls;

        public Task<IReadOnlyList<AccountQuota>> FetchAsync(string tokenVariable, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(fetch());
        }
    }
}
