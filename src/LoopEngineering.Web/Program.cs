using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Issues;
using LoopEngineering.Core.Loop;
using LoopEngineering.Web;
using LoopEngineering.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<ILoopAgentFactory, LoopAgentFactory>();
builder.Services.AddSingleton<IGitHubIssueFetcher>(_ => new GitHubIssueFetcher(new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{
    Timeout = TimeSpan.FromSeconds(30)
}));
builder.Services.AddSingleton<LoopRunner>();
builder.Services.AddSingleton<CopilotAccountSource>();
builder.Services.AddSingleton<IAccountQuotaSource>(sp => sp.GetRequiredService<CopilotAccountSource>());
builder.Services.AddSingleton<AccountQuotaService>();
builder.Services.AddSingleton<IModelCatalogSource>(sp => sp.GetRequiredService<CopilotAccountSource>());
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<LoopEngineering.Core.Antigravity.AntigravityModelCatalog>();
builder.Services.AddHostedService<QuotaRefreshWorker>();
builder.Services.AddSingleton(sp =>
    new CreditLedger(Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "credit-ledger.json")));
builder.Services.AddSingleton(sp =>
    new ConfigStore(Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "last-config.json")));

var app = builder.Build();

// Re-read the account quota after every round (throttled inside the service).
var quotaService = app.Services.GetRequiredService<AccountQuotaService>();
// The background refresh needs to know which variable holds the token before any page is opened.
quotaService.TokenVariable = app.Services.GetRequiredService<ConfigStore>().Load().TokenEnvironmentVariable;
var runner = app.Services.GetRequiredService<LoopRunner>();
runner.RoundCompleted += () => _ = quotaService.RefreshAsync(force: false);

// Keep a monthly total of credits spent by this app; the SDK reports 0 consumed for credit-billed accounts.
var ledger = app.Services.GetRequiredService<CreditLedger>();
runner.RoundUsageRecorded += usage => ledger.Add(usage.AiCredits ?? 0);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Stop the loop cleanly (abort the agent turn, finish the progress file) when the app shuts down.
app.Lifetime.ApplicationStopping.Register(() =>
{
    runner.Stop();
    runner.WaitForCompletionAsync().Wait(TimeSpan.FromSeconds(15));
});

app.Run();
