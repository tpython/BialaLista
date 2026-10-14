namespace WhiteListApi.Services;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WhiteListApi.Core;
using WhiteListApi.Storage;

public sealed record DaySnapshot(
    string DateYyyyMmDd,
    WhiteListBinaryStore BinaryStore,
    VirtualAccountMaskMatcher MaskMatcher,
    string BinaryFilePath) : IDisposable
{
    public void Dispose()
    {
        BinaryStore.Dispose();
        try { if (File.Exists(BinaryFilePath)) File.Delete(BinaryFilePath); } catch { }
    }
}

public sealed class MultiDayWhiteListManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DaySnapshot> _snapshots = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _fallbackCache;
    private readonly AuditLogChannelService _auditChannel;
    private readonly ILogger<MultiDayWhiteListManager> _logger;

    public MultiDayWhiteListManager(
        IHttpClientFactory httpClientFactory,
        IMemoryCache fallbackCache,
        AuditLogChannelService auditChannel,
        ILogger<MultiDayWhiteListManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _fallbackCache = fallbackCache;
        _auditChannel = auditChannel;
        _logger = logger;
    }

    public string[] GetLoadedDates() => [.. _snapshots.Keys];
    public bool HasDayLoaded(string rawDate) => _snapshots.ContainsKey(rawDate);

    public void RegisterOrUpdateDay(string dateIso, string binPath, int iterations, IEnumerable<string> masks)
    {
        string rawDate = dateIso.Replace("-", "");
        var store = new WhiteListBinaryStore(binPath, iterations);
        var matcher = new VirtualAccountMaskMatcher(masks);
        var newSnapshot = new DaySnapshot(rawDate, store, matcher, binPath);

        _snapshots.AddOrUpdate(rawDate, newSnapshot, (key, oldSnapshot) =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(5000);
                oldSnapshot.Dispose();
            });
            return newSnapshot;
        });
    }

    public void EnforceRetentionPolicy(DateTime currentPolishDate)
    {
        var allowedDates = new HashSet<string>(StringComparer.Ordinal)
        {
            currentPolishDate.ToString("yyyyMMdd"),
            currentPolishDate.AddDays(-1).ToString("yyyyMMdd"),
            currentPolishDate.AddDays(-2).ToString("yyyyMMdd")
        };

        foreach (var key in _snapshots.Keys)
        {
            if (!allowedDates.Contains(key) && _snapshots.TryRemove(key, out var oldSnapshot))
            {
                oldSnapshot.Dispose();
            }
        }
    }

    public async Task<IResult> CheckAccountAsync(string identifier, string bankAccount, string dateIso, string idType, CancellationToken ct)
    {
        string rawDate = dateIso.Replace("-", "");
        if (_snapshots.TryGetValue(rawDate, out DaySnapshot? snapshot))
        {
            try
            {
                UInt128 hash = WhiteListHasher.ComputeTruncatedHash(rawDate, identifier, bankAccount, snapshot.BinaryStore.Iterations);
                bool isAssigned = snapshot.BinaryStore.ContainsHash(hash);
                if (!isAssigned) isAssigned = snapshot.MaskMatcher.TryMatchAnyMask(rawDate, identifier, bankAccount, snapshot.BinaryStore);

                string assignedStr = isAssigned ? "TAK" : "NIE";
                string reqId = Guid.NewGuid().ToString("N")[..18];

                _auditChannel.Enqueue(new AuditLogEntry(
                    reqId, DateTime.UtcNow.ToString("o"), dateIso, identifier, bankAccount, assignedStr, "LOCAL_BIN"));

                return Results.Ok(new
                {
                    result = new
                    {
                        accountAssigned = assignedStr,
                        requestDateTime = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"),
                        requestId = reqId
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd odczytu lokalnego dla {Date}. Przełączam na Fallback MF.", dateIso);
            }
        }

        // Fallback do oficjalnego API MF z buforowaniem w IMemoryCache
        string cacheKey = $"fb:{idType}:{identifier}:{bankAccount}:{rawDate}";
        if (_fallbackCache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson is not null)
        {
            return Results.Content(cachedJson, "application/json", statusCode: 200);
        }

        var client = _httpClientFactory.CreateClient("MfApi");
        string url = $"api/check/{idType}/{identifier}/bank-account/{bankAccount}?date={dateIso}";
        using var response = await client.GetAsync(url, ct);
        string content = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            _fallbackCache.Set(cacheKey, content, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                Size = 1
            });

            _auditChannel.Enqueue(new AuditLogEntry(
                "MF_RESPONSE", DateTime.UtcNow.ToString("o"), dateIso, identifier, bankAccount, "FROM_MF", "MF_FALLBACK"));
        }

        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }

    public void Dispose()
    {
        foreach (var s in _snapshots.Values) s.Dispose();
        _snapshots.Clear();
    }
}
