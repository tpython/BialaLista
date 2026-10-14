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
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });

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
        var batch = new List<AuditLogEntry>(200);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                while (_channel.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                    if (batch.Count >= 500) break;
                }

                if (batch.Count == 0) continue;

                // 1. Zapis paczki do Azure Blob Storage (.jsonl)
                if (_blobContainer is not null)
                {
                    string day = DateTime.UtcNow.ToString("yyyyMMdd");
                    var blob = _blobContainer.GetBlobClient($"audit/{day}/{Guid.NewGuid():N}.jsonl");
                    var sb = new StringBuilder();
                    foreach (var item in batch)
                        sb.AppendLine(JsonSerializer.Serialize(item));

                    await blob.UploadAsync(BinaryData.FromString(sb.ToString()), overwrite: true, stoppingToken);
                }

                // 2. Zapis encji do Azure Table Storage
                if (_tableClient is not null)
                {
                    foreach (var item in batch)
                    {
                        var entity = new TableEntity(item.QueryDate, item.RequestId)
                        {
                            { "TimestampUtc", item.TimestampUtc },
                            { "Identifier", item.Identifier },
                            { "BankAccount", item.BankAccount },
                            { "AccountAssigned", item.AccountAssigned },
                            { "Source", item.Source }
                        };
                        await _tableClient.AddEntityAsync(entity, stoppingToken);
                    }
                }

                batch.Clear();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Błąd zapisu paczki logów audytowych requestId.");
            }
        }
    }
}
