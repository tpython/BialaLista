namespace WhiteListApi.Services;

using System.Collections.Concurrent;
using System.Text.Json;
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
        // Plik binarny nie jest usuwany w Dispose!
        // Retencją i usuwaniem przeterminowanych plików zarządza wyłącznie EnforceRetentionPolicy.
    }
}

public sealed class MultiDayWhiteListManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DaySnapshot> _snapshots = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _fallbackCache;
    private readonly AuditLogChannelService _auditChannel;
    private readonly ILogger<MultiDayWhiteListManager> _logger;
    private readonly TimeZoneInfo _polandTz;

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
        _polandTz = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Warsaw", out var tz) ? tz : TimeZoneInfo.Local;
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
                try
                {
                    if (File.Exists(oldSnapshot.BinaryFilePath))
                        File.Delete(oldSnapshot.BinaryFilePath);

                    string metaPath = Path.ChangeExtension(oldSnapshot.BinaryFilePath, ".meta.json");
                    if (File.Exists(metaPath))
                        File.Delete(metaPath);
                }
                catch { }
            }
        }
    }

    public async Task<IResult> CheckAccountAsync(string identifier, string bankAccount, string dateIso, string idType, CancellationToken ct)
    {
        string rawDate = dateIso.Replace("-", "");
        // Plik płaski MF zawiera wyłącznie skróty generowane dla numerów NIP (Data + NIP + NRB).
        // Zapytania dla numeru REGON nie mogą być sprawdzane w pliku płaskim i trafiają bezpośrednio do Fallbacku MF API.
        if (idType.Equals("nip", StringComparison.OrdinalIgnoreCase) && _snapshots.TryGetValue(rawDate, out DaySnapshot? snapshot))
        {
            try
            {
                UInt128 hash = WhiteListHasher.ComputeTruncatedHash(rawDate, identifier, bankAccount, snapshot.BinaryStore.Iterations);
                bool isAssigned = snapshot.BinaryStore.ContainsHash(hash);
                if (!isAssigned) isAssigned = snapshot.MaskMatcher.TryMatchAnyMask(rawDate, identifier, bankAccount, snapshot.BinaryStore);

                string assignedStr = isAssigned ? "TAK" : "NIE";
                string reqId = Guid.NewGuid().ToString("N")[..18];

                DateTime nowInPoland = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _polandTz);
                string reqDateFormatted = nowInPoland.ToString("dd-MM-yyyy HH:mm:ss");

                _auditChannel.Enqueue(new AuditLogEntry(
                    reqId, DateTime.UtcNow.ToString("o"), dateIso, identifier, bankAccount, assignedStr, "LOCAL_BIN"));

                return Results.Ok(new
                {
                    result = new
                    {
                        accountAssigned = assignedStr,
                        requestDateTime = reqDateFormatted,
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
            var (cachedReqId, cachedAssigned, _) = ParseMfResponse(cachedJson);
            string auditReqId = !string.IsNullOrEmpty(cachedReqId) ? cachedReqId : Guid.NewGuid().ToString("N")[..18];
            string assignedStr = !string.IsNullOrEmpty(cachedAssigned) ? cachedAssigned : "NIE";

            // Każde sprawdzenie - nawet z cache - musi trafić do audytu z oznaczeniem fallbacku
            _auditChannel.Enqueue(new AuditLogEntry(
                auditReqId, DateTime.UtcNow.ToString("o"), dateIso, identifier, bankAccount, assignedStr, "MF_FALLBACK"));

            return Results.Content(cachedJson, "application/json", statusCode: 200);
        }

        var client = _httpClientFactory.CreateClient("MfApi");
        string url = $"api/check/{idType}/{identifier}/bank-account/{bankAccount}?date={dateIso}";
        try
        {
            using var response = await client.GetAsync(url, ct);
            string content = await response.Content.ReadAsStringAsync(ct);

            string trimmedContent = content.TrimStart();
            bool isJson = (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                || (trimmedContent.StartsWith('{') || trimmedContent.StartsWith('['));

            if (!isJson)
            {
                _logger.LogWarning("Oficjalne API MF zwróciło odpowiedź niebędącą JSON ({StatusCode}): {Url}", response.StatusCode, url);
                return Results.Json(new
                {
                    code = "WL-500",
                    message = "Błąd komunikacji z bramką Ministerstwa Finansów (bramka MF zwróciła stronę HTML lub błąd WAF zamiast formatu JSON)."
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            if (response.IsSuccessStatusCode)
            {
                var (mfReqId, mfAssigned, _) = ParseMfResponse(content);
                string auditReqId = !string.IsNullOrEmpty(mfReqId) ? mfReqId : Guid.NewGuid().ToString("N")[..18];
                string assignedStr = !string.IsNullOrEmpty(mfAssigned) ? mfAssigned : "NIE";

                _fallbackCache.Set(cacheKey, content, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                    Size = 1
                });

                _auditChannel.Enqueue(new AuditLogEntry(
                    auditReqId, DateTime.UtcNow.ToString("o"), dateIso, identifier, bankAccount, assignedStr, "MF_FALLBACK"));
            }

            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Błąd komunikacji z oficjalnym API MF ({Url}).", url);
            return Results.Json(new
            {
                code = "WL-500",
                message = "Błąd komunikacji z bramką Ministerstwa Finansów."
            }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static (string RequestId, string AccountAssigned, string RequestDateTime) ParseMfResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var resultProp))
            {
                string reqId = resultProp.TryGetProperty("requestId", out var rId) ? rId.GetString() ?? "" : "";
                string assigned = resultProp.TryGetProperty("accountAssigned", out var acc) ? acc.GetString() ?? "" : "";
                string reqDt = resultProp.TryGetProperty("requestDateTime", out var dt) ? dt.GetString() ?? "" : "";
                return (reqId, assigned, reqDt);
            }
        }
        catch { }
        return ("", "", "");
    }

    public void Dispose()
    {
        foreach (var s in _snapshots.Values) s.Dispose();
        _snapshots.Clear();
    }
}
