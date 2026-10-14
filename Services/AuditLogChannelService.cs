namespace WhiteListApi.Services;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public readonly record struct AuditLogEntry(
    string RequestId,
    string TimestampUtc,
    string QueryDate,
    string Identifier,
    string BankAccount,
    string AccountAssigned,
    string Source);

public sealed class AuditLogChannelService : BackgroundService
{
    private readonly Channel<AuditLogEntry> _channel = Channel.CreateBounded<AuditLogEntry>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly BlobContainerClient? _blobContainer;
    private readonly TableClient? _tableClient;
    private readonly ILogger<AuditLogChannelService> _logger;

    public AuditLogChannelService(
        IServiceProvider sp,
        ILogger<AuditLogChannelService> logger)
    {
        _blobContainer = sp.GetService<BlobContainerClient>();
        _tableClient = sp.GetService<TableClient>();
        _logger = logger;
    }

    public void Enqueue(AuditLogEntry entry) => _channel.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Inicjalizacja zasobów w chmurze (CreateIfNotExists)
        await InitializeStorageAsync(stoppingToken);

        var batch = new List<AuditLogEntry>(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Czekamy na pierwszy element w kanale
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken))
                    break;

                while (batch.Count < 500 && _channel.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                // Krótkie okno (max 2s) na skumulowanie większej paczki, jeśli ruch jest powolny
                if (batch.Count < 100 && !stoppingToken.IsCancellationRequested)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    delayCts.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        while (batch.Count < 500 && await _channel.Reader.WaitToReadAsync(delayCts.Token))
                        {
                            while (batch.Count < 500 && _channel.Reader.TryRead(out var extra))
                                batch.Add(extra);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Upłynął limit 2s zbierania małej paczki, przechodzimy do wysyłki
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas zapisu paczki logów audytowych.");
            }
            finally
            {
                // Zawsze czyścimy batch, zapobiegając wyciekowi pamięci i zapętleniu ponowień
                batch.Clear();
            }
        }

        // Graceful Drain: opróżnienie pozostałych w kanale elementów przed zakończeniem pracy
        await DrainRemainingLogsAsync();
    }

    private async Task InitializeStorageAsync(CancellationToken ct)
    {
        try
        {
            if (_blobContainer is not null)
                await _blobContainer.CreateIfNotExistsAsync(cancellationToken: ct);
            if (_tableClient is not null)
                await _tableClient.CreateIfNotExistsAsync(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ostrzeżenie podczas inicjalizacji kontenera Blob lub Table Storage.");
        }
    }

    private async Task FlushBatchAsync(List<AuditLogEntry> batch, CancellationToken ct)
    {
        // 1. Zapis paczki do Azure Blob Storage (.jsonl)
        if (_blobContainer is not null)
        {
            string day = DateTime.UtcNow.ToString("yyyyMMdd");
            var blob = _blobContainer.GetBlobClient($"audit/{day}/{Guid.NewGuid():N}.jsonl");
            var sb = new StringBuilder(batch.Count * 128);
            foreach (var item in batch)
                sb.AppendLine(JsonSerializer.Serialize(item));

            await blob.UploadAsync(BinaryData.FromString(sb.ToString()), overwrite: true, ct);
        }

        // 2. Zapis transakcyjny do Azure Table Storage (paczki do 100 elementów per PartitionKey)
        if (_tableClient is not null)
        {
            var byPartition = batch.GroupBy(b => b.QueryDate);
            foreach (var partitionGroup in byPartition)
            {
                var transactions = new List<TableTransactionAction>(100);
                var seenRowKeys = new HashSet<string>(StringComparer.Ordinal);

                foreach (var item in partitionGroup)
                {
                    string rowKey = item.RequestId;
                    if (!seenRowKeys.Add(rowKey))
                    {
                        rowKey = $"{item.RequestId}_{Guid.NewGuid():N}";
                    }

                    var entity = new TableEntity(item.QueryDate, rowKey)
                    {
                        { "TimestampUtc", item.TimestampUtc },
                        { "Identifier", item.Identifier },
                        { "BankAccount", item.BankAccount },
                        { "AccountAssigned", item.AccountAssigned },
                        { "Source", item.Source }
                    };

                    transactions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity));

                    if (transactions.Count == 100)
                    {
                        await _tableClient.SubmitTransactionAsync(transactions, ct);
                        transactions.Clear();
                        seenRowKeys.Clear();
                    }
                }

                if (transactions.Count > 0)
                {
                    await _tableClient.SubmitTransactionAsync(transactions, ct);
                    transactions.Clear();
                }
            }
        }
    }

    private async Task DrainRemainingLogsAsync()
    {
        try
        {
            var drainBatch = new List<AuditLogEntry>(500);
            while (_channel.Reader.TryRead(out var entry))
            {
                drainBatch.Add(entry);
                if (drainBatch.Count >= 500)
                {
                    await FlushBatchAsync(drainBatch, CancellationToken.None);
                    drainBatch.Clear();
                }
            }

            if (drainBatch.Count > 0)
            {
                await FlushBatchAsync(drainBatch, CancellationToken.None);
                drainBatch.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd podczas opróżniania resztek bufora kanału audytowego (Graceful Drain).");
        }
    }
}
