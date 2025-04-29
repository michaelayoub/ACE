using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ACE.Common;
using log4net;

namespace ACE.Server;

public class TerminalWebClient: IDisposable
{
    private static readonly ILog log = LogManager.GetLogger(nameof(TerminalWebClient));
    private readonly HttpClient httpClient;

    public TerminalWebClient()
    {
        httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(ConfigManager.Config.TerminalCoffee.ApiHost);
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ConfigManager.Config.TerminalCoffee.ApiKey}");
    }

    public async Task<TResponse> GetAsync<TResponse>(string uri)
    {
        var response = await httpClient.GetAsync(uri);
        response.EnsureSuccessStatusCode();

        var jsonString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TResponse>(jsonString);
    }

    public async Task<TResponse> GetAsync<TResponse>(string uri, string token)
    {
        using var client = new HttpClient();
        client.BaseAddress = httpClient.BaseAddress;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();

        var jsonString = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TResponse>(jsonString);
    }

    public void Dispose()
    {
        httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
