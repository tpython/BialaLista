namespace WhiteListApi.Tests;

using Microsoft.Extensions.Configuration;
using WhiteListApi.Configuration;
using Xunit;

public class ClientQuotasConfigTests
{
    [Fact]
    public void LoadClientQuotas_LoadsFromConfigurationSection()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ClientQuotas:firma-abc"] = "1500",
            ["ClientQuotas:firma-xyz"] = "25000"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var quotas = ClientQuotasConfig.LoadClientQuotas(config);

        Assert.Equal(1500, quotas["firma-abc"]);
        Assert.Equal(25000, quotas["firma-xyz"]);
    }

    [Fact]
    public void LoadClientQuotas_LoadsFromEnvString_CommaAndSemicolonSeparated()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["CLIENT_QUOTAS"] = "partner1:5000, partner2:10000; partner3=20000"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var quotas = ClientQuotasConfig.LoadClientQuotas(config);

        Assert.Equal(5000, quotas["partner1"]);
        Assert.Equal(10000, quotas["partner2"]);
        Assert.Equal(20000, quotas["partner3"]);
    }

    [Fact]
    public void LoadClientQuotas_FallsBackToDefaults_WhenEmpty()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        var quotas = ClientQuotasConfig.LoadClientQuotas(config);

        Assert.True(quotas.ContainsKey("klucz-klienta-standard"));
        Assert.True(quotas.ContainsKey("klucz-klienta-premium"));
        Assert.Equal(1000, quotas["klucz-klienta-standard"]);
        Assert.Equal(50000, quotas["klucz-klienta-premium"]);
    }
}
