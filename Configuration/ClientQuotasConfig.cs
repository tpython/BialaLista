namespace WhiteListApi.Configuration;

using Microsoft.Extensions.Configuration;

public static class ClientQuotasConfig
{
    public static Dictionary<string, int> LoadClientQuotas(IConfiguration configuration)
    {
        var quotas = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Sekcja ClientQuotas z appsettings.json lub zmiennych ClientQuotas__<key>=<quota>
        var section = configuration.GetSection("ClientQuotas");
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Key) && int.TryParse(child.Value, out int quota))
            {
                quotas[child.Key] = quota;
            }
        }

        // 2. Obsługa zmiennej środowiskowej CLIENT_QUOTAS w formacie: "key1:1000,key2:50000" lub "key1=1000;key2=50000"
        string? envQuotas = configuration["CLIENT_QUOTAS"] ?? Environment.GetEnvironmentVariable("CLIENT_QUOTAS");
        if (!string.IsNullOrWhiteSpace(envQuotas))
        {
            var pairs = envQuotas.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split(['=', ':'], 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int quota))
                {
                    quotas[parts[0]] = quota;
                }
            }
        }

        // 3. Fallback do wartości domyślnych jeśli nic nie skonfigurowano
        if (quotas.Count == 0)
        {
            quotas["klucz-klienta-standard"] = 1_000;
            quotas["klucz-klienta-premium"] = 50_000;
        }

        return quotas;
    }
}
