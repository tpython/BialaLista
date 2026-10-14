namespace WhiteListApi.Services;

using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using WhiteListApi.Storage;

public sealed class WhiteListSyncBackgroundService : BackgroundService
{
    private readonly MultiDayWhiteListManager _manager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BlobContainerClient? _blobContainer;
    private readonly ILogger<WhiteListSyncBackgroundService> _logger;
    private readonly string _dataDir;
    private readonly TimeZoneInfo _polandTz;

    public WhiteListSyncBackgroundService(
        MultiDayWhiteListManager manager,
        IHttpClientFactory httpClientFactory,
        IServiceProvider sp,
        ILogger<WhiteListSyncBackgroundService> logger)
    {
        _manager = manager;
        _httpClientFactory = httpClientFactory;
        _blobContainer = sp.GetService<BlobContainerClient>();
        _logger = logger;
        _dataDir = Path.Combine(Path.GetTempPath(), "whitelist_data");
        Directory.CreateDirectory(_dataDir);
        _polandTz = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Warsaw", out var tz) ? tz : TimeZoneInfo.Local;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (_blobContainer is not null)
        {
            try { await _blobContainer.CreateIfNotExistsAsync(cancellationToken: stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Ostrzeżenie podczas inicjalizacji kontenera Blob."); }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool success = await SyncLastThreeDaysAsync(stoppingToken);
            if (success)
            {
                DateTime nowInPoland = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _polandTz);
                DateTime nextRun = nowInPoland.Date.AddDays(1).AddMinutes(5);
                TimeSpan delay = nextRun - nowInPoland;
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(5), stoppingToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    public async Task<bool> SyncLastThreeDaysAsync(CancellationToken ct)
    {
        DateTime nowPl = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _polandTz);
        bool allOk = true;

        for (int i = 0; i < 3; i++)
        {
            string dateIso = nowPl.AddDays(-i).ToString("yyyy-MM-dd");
            string rawDate = dateIso.Replace("-", "");
            if (_manager.HasDayLoaded(rawDate)) continue;

            bool loaded = await TryLoadDayAsync(dateIso, rawDate, ct);
            if (!loaded) allOk = false;
        }

        _manager.EnforceRetentionPolicy(nowPl);
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return allOk;
    }

    public async Task<bool> TryLoadDayAsync(string dateIso, string rawDate, CancellationToken ct)
    {
        string finalBinPath = Path.Combine(_dataDir, $"{rawDate}.bin");
        string localMetaPath = Path.Combine(_dataDir, $"{rawDate}.meta.json");
        string temp7z = Path.Combine(_dataDir, $"{rawDate}.7z.tmp");
        string tempBin = Path.Combine(_dataDir, $"{rawDate}.bin.tmp");

        try
        {
            if (File.Exists(finalBinPath) && File.Exists(localMetaPath))
            {
                try
                {
                    string metaJson = await File.ReadAllTextAsync(localMetaPath, ct);
                    var localMeta = System.Text.Json.JsonSerializer.Deserialize<ParsedFlatFileMetadata>(metaJson);
                    if (localMeta is not null)
                    {
                        _manager.RegisterOrUpdateDay(dateIso, finalBinPath, localMeta.Iterations, localMeta.Masks);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Błąd odczytu lokalnego pliku meta dla {Date}.", dateIso);
                }
            }

            if (_blobContainer is not null)
            {
                var binBlob = _blobContainer.GetBlobClient($"{rawDate}.bin");
                var metaBlob = _blobContainer.GetBlobClient($"{rawDate}.meta.json");
                if (await binBlob.ExistsAsync(ct) && await metaBlob.ExistsAsync(ct))
                {
                    await binBlob.DownloadToAsync(finalBinPath, ct);
                    var metaDownload = await metaBlob.DownloadContentAsync(ct);
                    var meta = metaDownload.Value.Content.ToObjectFromJson<ParsedFlatFileMetadata>()!;
                    await File.WriteAllTextAsync(localMetaPath, metaDownload.Value.Content.ToString(), ct);
                    _manager.RegisterOrUpdateDay(dateIso, finalBinPath, meta.Iterations, meta.Masks);
                    return true;
                }
            }

            var client = _httpClientFactory.CreateClient("MfFlatFile");
            string flatFileUrl = "https://plikplaski.mf.gov.pl/pliki/" + rawDate + ".7z";
            using var resp = await client.GetAsync(flatFileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return false;

            await using (var fs = new FileStream(temp7z, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await resp.Content.CopyToAsync(fs, ct);

            ParsedFlatFileMetadata newMeta;
            using (var archive = ArchiveFactory.OpenArchive(temp7z))
            {
                var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && (e.Key?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ?? false))
                    ?? throw new InvalidOperationException($"Nie odnaleziono pliku JSON w archiwum {temp7z}.");
                await using var stream = entry.OpenEntryStream();
                newMeta = await FlatFileStreamParser.ParseAndConvertAsync(stream, tempBin, ct);
            }
            File.Delete(temp7z);
            File.Move(tempBin, finalBinPath, overwrite: true);
            await File.WriteAllTextAsync(localMetaPath, System.Text.Json.JsonSerializer.Serialize(newMeta), ct);

            if (_blobContainer is not null)
            {
                await _blobContainer.GetBlobClient($"{rawDate}.bin").UploadAsync(finalBinPath, overwrite: true, ct);
                await _blobContainer.GetBlobClient($"{rawDate}.meta.json").UploadAsync(BinaryData.FromObjectAsJson(newMeta), overwrite: true, ct);
            }

            _manager.RegisterOrUpdateDay(dateIso, finalBinPath, newMeta.Iterations, newMeta.Masks);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Błąd synchronizacji pliku dla dnia {Date}", dateIso);
            return false;
        }
        finally
        {
            if (File.Exists(temp7z)) try { File.Delete(temp7z); } catch { }
            if (File.Exists(tempBin)) try { File.Delete(tempBin); } catch { }
        }
    }
}
