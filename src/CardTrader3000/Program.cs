using CardTrader3000.Components;
using CardTrader3000.Data;
using CardTrader3000.Services.Claude;
using CardTrader3000.Services.Import;
using CardTrader3000.Services.Inventory;
using CardTrader3000.Services.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---- Blazor (Interactive Server keeps the Claude API key on the server) ----
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---- Database ----
// A factory (not a scoped DbContext) because Blazor Server circuits are long-lived and the
// import pipeline will run in the background.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// ---- Pricing ----
builder.Services.Configure<FeeOptions>(builder.Configuration.GetSection(FeeOptions.SectionName));
builder.Services.AddSingleton<FeeCalculator>();

// ---- Claude ----
builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.SectionName));
builder.Services.AddSingleton<PromptTemplateProvider>();
builder.Services.AddHttpClient<IClaudeCardEvaluator, ClaudeCardEvaluator>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
});

// ---- Import pipeline ----
// Singletons: one queue and one progress feed shared by every browser tab and the worker.
builder.Services.AddSingleton<ImportQueue>();
builder.Services.AddSingleton<ImportProgressTracker>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<ImportProcessor>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddScoped<ImportHistoryService>();

// ---- Inventory ----
builder.Services.AddScoped<InventoryService>();

var app = builder.Build();

// Apply pending migrations on startup (creates cardtrader3000.db on first run).
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
