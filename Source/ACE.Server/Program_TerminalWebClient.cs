using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ACE.Common;

namespace ACE.Server;

public class TerminalWebClient: IDisposable
{
    private readonly HttpClient httpClient;

    public TerminalWebClient()
    {
        httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(ConfigManager.Config.TerminalCoffee.ApiHost);
        httpClient.DefaultRequestHeaders.Add("bearer_token", ConfigManager.Config.TerminalCoffee.ApiKey);
    }

    public async Task<TResponse> GetAsync<TResponse>(string uri)
    {
        var response = await httpClient.GetAsync(uri);
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
