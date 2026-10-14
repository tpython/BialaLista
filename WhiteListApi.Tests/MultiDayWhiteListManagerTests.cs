namespace WhiteListApi.Tests;

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using WhiteListApi.Core;
using WhiteListApi.Services;
using WhiteListApi.Storage;
using Xunit;

public class MultiDayWhiteListManagerTests
{
    private class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    [Fact]
    public async Task CheckAccountAsync_ReturnsTak_WhenAccountInLocalBin()
    {
        string dateIso = "2026-10-14";
        string rawDate = "20261014";
        string nip = "5260250995";
        string account = "94102010260000140201648043";

        UInt128 hash = WhiteListHasher.ComputeTruncatedHash(rawDate, nip, account, iterations: 10);

        string tempBin = Path.GetTempFileName();
        try
        {
            byte[] bytes = new byte[16];
            MemoryMarshal.Write(bytes, in hash);
            File.WriteAllBytes(tempBin, bytes);

            var cache = new MemoryCache(new MemoryCacheOptions());
            var auditService = new AuditLogChannelService(new EmptyServiceProvider(), NullLogger<AuditLogChannelService>.Instance);
            using var manager = new MultiDayWhiteListManager(
                new DummyHttpClientFactory(),
                cache,
                auditService,
                NullLogger<MultiDayWhiteListManager>.Instance);

            manager.RegisterOrUpdateDay(dateIso, tempBin, 10, []);

            Assert.True(manager.HasDayLoaded(rawDate));
            Assert.Contains(rawDate, manager.GetLoadedDates());

            var result = await manager.CheckAccountAsync(nip, account, dateIso, "nip", CancellationToken.None);

            var okResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
            Assert.NotNull(okResult.Value);

            dynamic val = okResult.Value;
            string assigned = val.result.accountAssigned;
            Assert.Equal("TAK", assigned);
        }
        finally
        {
            if (File.Exists(tempBin)) File.Delete(tempBin);
        }
    }

    [Fact]
    public void EnforceRetentionPolicy_RemovesDatesOlderThanThreeDays()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var auditService = new AuditLogChannelService(new EmptyServiceProvider(), NullLogger<AuditLogChannelService>.Instance);
        using var manager = new MultiDayWhiteListManager(
            new DummyHttpClientFactory(),
            cache,
            auditService,
            NullLogger<MultiDayWhiteListManager>.Instance);

        DateTime today = new DateTime(2026, 10, 14);

        string tempBinToday = CreateDummyBin();
        string tempBinYesterday = CreateDummyBin();
        string tempBinTwoDaysAgo = CreateDummyBin();
        string tempBinFourDaysAgo = CreateDummyBin();

        try
        {
            manager.RegisterOrUpdateDay("2026-10-14", tempBinToday, 5000, []);
            manager.RegisterOrUpdateDay("2026-10-13", tempBinYesterday, 5000, []);
            manager.RegisterOrUpdateDay("2026-10-12", tempBinTwoDaysAgo, 5000, []);
            manager.RegisterOrUpdateDay("2026-10-10", tempBinFourDaysAgo, 5000, []);

            Assert.True(manager.HasDayLoaded("20261014"));
            Assert.True(manager.HasDayLoaded("20261013"));
            Assert.True(manager.HasDayLoaded("20261012"));
            Assert.True(manager.HasDayLoaded("20261010"));

            manager.EnforceRetentionPolicy(today);

            Assert.True(manager.HasDayLoaded("20261014"));
            Assert.True(manager.HasDayLoaded("20261013"));
            Assert.True(manager.HasDayLoaded("20261012"));
            Assert.False(manager.HasDayLoaded("20261010"));
        }
        finally
        {
            if (File.Exists(tempBinToday)) File.Delete(tempBinToday);
            if (File.Exists(tempBinYesterday)) File.Delete(tempBinYesterday);
            if (File.Exists(tempBinTwoDaysAgo)) File.Delete(tempBinTwoDaysAgo);
            if (File.Exists(tempBinFourDaysAgo)) File.Delete(tempBinFourDaysAgo);
        }
    }

    private static string CreateDummyBin()
    {
        string p = Path.GetTempFileName();
        byte[] bytes = new byte[16];
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
