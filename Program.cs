using System.Threading.RateLimiting;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using WhiteListApi.Configuration;
using WhiteListApi.Core;
using WhiteListApi.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Kestrel & Bezpieczeństwo wejścia
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 0;
    options.Limits.MaxRequestHeadersTotalSize = 8192;
    options.Limits.MaxConcurrentConnections = 200;
});

// 2. Cache dla zapytań Fallback MF (limit 10 000 wpisów, ochrona RAM i limitu 5000 zapytań MF)
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 10_000;
});

// 3. HttpClient do Fallbacku na oficjalne API MF
string mfApiUrl = builder.Configuration["MF_API_BASE_URL"] ?? "https://wykazpodatnikowvat.mf.gov.pl/";
builder.Services.AddHttpClient("MfApi", client =>
{
    client.BaseAddress = new Uri(mfApiUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WhiteListProxy/1.0");
});

// 4. HttpClient do pobierania plików .7z z MF
builder.Services.AddHttpClient("MfFlatFile", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

// 5. Azure Blob & Table Storage Client (Managed Identity)
string? blobServiceUri = builder.Configuration["BLOB_STORAGE_URI"] ?? Environment.GetEnvironmentVariable("BLOB_STORAGE_URI");
if (!string.IsNullOrEmpty(blobServiceUri))
{
    builder.Services.AddSingleton(_ => new BlobContainerClient(new Uri(blobServiceUri), new DefaultAzureCredential()));
}

string? tableServiceUri = builder.Configuration["TABLE_STORAGE_URI"] ?? Environment.GetEnvironmentVariable("TABLE_STORAGE_URI");
if (!string.IsNullOrEmpty(tableServiceUri))
{
    string tableName = builder.Configuration["AUDIT_TABLE_NAME"] ?? "whitelist_audit";
    builder.Services.AddSingleton(_ => new TableClient(new Uri(tableServiceUri), tableName, new DefaultAzureCredential()));
}

// 6. Usługi w tle i menedżer danych
builder.Services.AddSingleton<AuditLogChannelService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogChannelService>());
builder.Services.AddSingleton<MultiDayWhiteListManager>();
builder.Services.AddHostedService<WhiteListSyncBackgroundService>();

// 7. Rate Limiter (Ochrona CPU/RAM + dzienne limity per klucz ładowane z konfiguracji / env vars)
var clientQuotas = ClientQuotasConfig.LoadClientQuotas(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            code = "RATE-LIMIT",
            message = "Przekroczono dzienny limit zapytań lub serwer jest chwilowo przeciążony."
        }, token);
    };

    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            RateLimitPartition.GetConcurrencyLimiter("CpuRamGuard", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 16,
                QueueLimit = 0
            })),

        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            string clientKey = ResolveClientKey(httpContext);
            string rawKeyPart = clientKey.StartsWith("key:") ? clientKey[4..] : string.Empty;

            if (string.IsNullOrEmpty(rawKeyPart) || !clientQuotas.TryGetValue(rawKeyPart, out int dailyLimit))
            {
                return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromDays(1),
                    QueueLimit = 0
                });
            }

            return RateLimitPartition.GetFixedWindowLimiter(rawKeyPart, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = dailyLimit,
                Window = TimeSpan.FromDays(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        })
    );
});

var app = builder.Build();

app.UseRateLimiter();

// Endpoint diagnostyczny /healthz
app.MapGet("/healthz", (MultiDayWhiteListManager manager) => Results.Ok(new
{
    status = "Healthy",
    loadedDates = manager.GetLoadedDates(),
    memoryAllocatedMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2),
    machineTime = DateTime.UtcNow.ToString("o")
}));

// Wspólny handler dla NIP i REGON
Func<string, string, string?, string, MultiDayWhiteListManager, CancellationToken, Task<IResult>> checkHandler =
    async (identifier, bankAccount, date, idType, manager, ct) =>
    {
        if (idType == "nip" && !PolishIdentifiersValidator.IsValidNip(identifier))
            return Results.BadRequest(new { code = "WL-115", message = "Niepoprawny numer NIP (błędna suma kontrolna)." });

        if (idType == "regon" && !PolishIdentifiersValidator.IsValidRegon(identifier))
            return Results.BadRequest(new { code = "WL-115", message = "Niepoprawny numer REGON (błędna suma kontrolna)." });

        if (!PolishIdentifiersValidator.IsValidNrb(bankAccount))
            return Results.BadRequest(new { code = "WL-116", message = "Niepoprawny numer rachunku bankowego NRB (błędna suma kontrolna)." });

        string targetDateIso = string.IsNullOrEmpty(date)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local).ToString("yyyy-MM-dd")
            : date;

        if (!DateOnly.TryParseExact(targetDateIso, "yyyy-MM-dd", out _))
            return Results.BadRequest(new { code = "WL-117", message = "Data musi być w formacie YYYY-MM-DD." });

        return await manager.CheckAccountAsync(identifier, bankAccount, targetDateIso, idType, ct);
    };

// Trasy NIP
app.MapGet("/k/{apiKey}/api/check/nip/{nip}/bank-account/{bankAccount}",
    (string apiKey, string nip, string bankAccount, [FromQuery] string? date, MultiDayWhiteListManager mgr, CancellationToken ct)
        => checkHandler(nip, bankAccount, date, "nip", mgr, ct));

app.MapGet("/api/check/nip/{nip}/bank-account/{bankAccount}",
    (string nip, string bankAccount, [FromQuery] string? date, MultiDayWhiteListManager mgr, CancellationToken ct)
        => checkHandler(nip, bankAccount, date, "nip", mgr, ct));

// Trasy REGON
app.MapGet("/k/{apiKey}/api/check/regon/{regon}/bank-account/{bankAccount}",
    (string apiKey, string regon, string bankAccount, [FromQuery] string? date, MultiDayWhiteListManager mgr, CancellationToken ct)
        => checkHandler(regon, bankAccount, date, "regon", mgr, ct));

app.MapGet("/api/check/regon/{regon}/bank-account/{bankAccount}",
    (string regon, string bankAccount, [FromQuery] string? date, MultiDayWhiteListManager mgr, CancellationToken ct)
        => checkHandler(regon, bankAccount, date, "regon", mgr, ct));

app.Run();

static string ResolveClientKey(HttpContext ctx)
{
    if (ctx.Request.RouteValues.TryGetValue("apiKey", out var rk) && rk is string s && !string.IsNullOrEmpty(s))
        return $"key:{s}";
    if (ctx.Request.Query.TryGetValue("apiKey", out var qk) && !string.IsNullOrEmpty(qk))
        return $"key:{qk}";
    return $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}

public partial class Program { }
