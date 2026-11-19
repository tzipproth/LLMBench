using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMBench.Core.Llama;

public sealed class ChatResult
{
    public bool Success { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
    public bool TimedOut { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public string? FinishReason { get; set; }
    public JsonNode? Usage { get; set; }
    public JsonNode? Timings { get; set; }
    public string? RawResponse { get; set; }
    public double ElapsedSeconds { get; set; }

    public int? PromptTokens => Usage?["prompt_tokens"]?.GetValue<int>();
    public int? CompletionTokens => Usage?["completion_tokens"]?.GetValue<int>();
}

public sealed class HealthResult
{
    public bool Ok { get; set; }
    public int? HttpStatus { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Minimal client for the llama-server HTTP API (OpenAI compatible endpoints plus /health and /props).
/// Also works with other OpenAI compatible servers that are already running (e.g. TabbyAPI); a server with an
/// API key gets it from the environment variable <see cref="ApiKeyVariable"/> (never stored in a run).
/// </summary>
public sealed class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    /// <summary>Environment variable with the API key for servers that need one (sent as "Authorization: Bearer").</summary>
    public const string ApiKeyVariable = "LLMBENCH_API_KEY";

    public LlamaClient(string serverUrl)
    {
        BaseUrl = serverUrl.TrimEnd('/');
        // Timeouts are handled per request with CancellationTokens (HttpClient default is 100 s).
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } key)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public void Dispose() => _http.Dispose();

    public async Task<HealthResult> GetHealthAsync(CancellationToken ct, int timeoutSeconds = 10)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/health", cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return new HealthResult { Ok = resp.IsSuccessStatusCode, HttpStatus = (int)resp.StatusCode, Message = body.Trim() };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult { Message = $"No response from {BaseUrl} within {timeoutSeconds} s." };
        }
        catch (HttpRequestException ex)
        {
            return new HealthResult { Message = $"Cannot reach {BaseUrl}: {ex.Message}" };
        }
    }

    public async Task<JsonObject?> GetPropsAsync(CancellationToken ct, int timeoutSeconds = 30)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = await _http.GetAsync($"{BaseUrl}/props", cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject;
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken ct, int timeoutSeconds = 30)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = await _http.GetAsync($"{BaseUrl}/v1/models", cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        return (node?["data"] as JsonArray)?.Select(m => m?["id"]?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? [];
    }

    /// <summary>
    /// Sends one chat completion. Cancellation through <paramref name="ct"/> (user stop) is rethrown;
    /// timeouts and HTTP problems are returned as an unsuccessful result.
    /// </summary>
    public async Task<ChatResult> ChatAsync(JsonObject request, TimeSpan timeout, CancellationToken ct)
    {
        var result = new ChatResult();
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{BaseUrl}/v1/chat/completions", content, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            result.HttpStatus = (int)resp.StatusCode;
            result.RawResponse = body;
            if (!resp.IsSuccessStatusCode)
            {
                result.Error = $"HTTP {(int)resp.StatusCode}: {Truncate(body, 500)}";
                return result;
            }

            var node = JsonNode.Parse(body);
            var choice = node?["choices"]?[0];
            var msg = choice?["message"];
            result.Content = msg?["content"]?.GetValue<string>();
            result.ReasoningContent = msg?["reasoning_content"]?.GetValue<string>();
            result.FinishReason = choice?["finish_reason"]?.GetValue<string>();
            result.Usage = node?["usage"]?.DeepClone();
            result.Timings = node?["timings"]?.DeepClone();
            result.Success = choice != null;
            if (choice == null) result.Error = "Response contains no choices.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.TimedOut = true;
            result.Error = $"Timeout after {timeout.TotalSeconds:0} s.";
        }
        catch (HttpRequestException ex)
        {
            result.Error = $"Connection error: {ex.Message}";
        }
        catch (System.Text.Json.JsonException ex)
        {
            result.Error = $"Invalid JSON in response: {ex.Message}";
        }
        finally
        {
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
