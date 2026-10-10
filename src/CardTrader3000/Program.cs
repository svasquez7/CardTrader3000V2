using CardTrader3000.Components;
using CardTrader3000.Data;
using CardTrader3000.Services.Claude;
using CardTrader3000.Services.Ebay;
using CardTrader3000.Services.Import;
using CardTrader3000.Services.Inventory;
using CardTrader3000.Services.Pricing;
using Microsoft.AspNetCore.DataProtection;
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

// ---- eBay listing ----
// Data Protection encrypts the stored eBay tokens. Keys are kept with the app data so they
// survive restarts (losing them only means clicking Connect again).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")))
    .SetApplicationName("CardTrader3000");
builder.Services.Configure<EbayOptions>(builder.Configuration.GetSection(EbayOptions.SectionName));
builder.Services.AddHttpClient(EbayAuthService.HttpClientName);
builder.Services.AddSingleton<EbayAuthService>();
builder.Services.AddHttpClient<EbayApiClient>(http => http.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddSingleton<ListingImageStore>();
builder.Services.AddHttpClient<EbayMarketDataService>(http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<EbayListingService>();

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

// eBay redirects here after the user signs in (the RuName's "auth accepted URL").
app.MapGet("/ebay/callback", async (string? code, string? state, EbayAuthService auth, CancellationToken ct) =>
{
    var error = await auth.HandleCallbackAsync(code, state, ct);
    return Results.Redirect(error is null ? "/ebay?connected=1" : "/ebay?error=" + Uri.EscapeDataString(error));
});

// eBay sends the user here if they decline on the consent page (the RuName's "auth declined URL").
app.MapGet("/ebay/declined", () => Results.Redirect("/ebay?error=" + Uri.EscapeDataString("eBay sign-in was declined.")));

// Listing photo previews. File names are validated by ListingImageStore.
app.MapGet("/listing-images/{listingId:int}/{fileName}", (int listingId, string fileName, ListingImageStore store) =>
    store.Find(listingId, fileName) is { } file ? Results.File(file.Path, file.ContentType) : Results.NotFound());
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
