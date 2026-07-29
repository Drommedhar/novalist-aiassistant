using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>
/// Anthropic Messages API client.
///
/// This talks HTTP directly rather than through the official Anthropic .NET SDK
/// on purpose: the extension ships as a single assembly (see the release
/// workflow's ZIP step, which packs only Novalist.Extensions.AiAssistant.dll),
/// so a NuGet dependency would not be present at load time in the field.
///
/// Two things differ from the OpenAI-compatible path and are easy to get wrong:
/// the system prompt is a top-level field rather than a message, and
/// <c>max_tokens</c> is required on every request. Sampling parameters are
/// deliberately never sent - current Claude models reject temperature, top_p and
/// top_k with a 400.
/// </summary>
public sealed class AnthropicClient
{
    /// <summary>Wire version pinned by the API. Not the model version.</summary>
    private const string ApiVersion = "2023-06-01";

    private const string DefaultBaseUrl = "https://api.anthropic.com";

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "claude-opus-5";
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public int MaxTokens { get; set; } = 8192;

    private CancellationTokenSource? _cts;

    private string Root =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl).TrimEnd('/');

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public void CancelPrompt()
    {
        _cts?.Cancel();
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{Root}{path}");
        request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        return request;
    }

    /// <summary>
    /// Whether the configured key reaches the API. Uses the models endpoint
    /// because it is cheap and needs no request body.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        if (!IsConfigured)
            return false;

        try
        {
            using var request = NewRequest(HttpMethod.Get, "/v1/models");
            using var response = await SharedClient.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Models the key can actually reach, newest first as the API returns them.
    /// Returns an empty list rather than throwing so the settings page can show
    /// "none found" instead of an error dialog.
    /// </summary>
    public async Task<List<AiModelInfo>> ListModelsAsync()
    {
        var result = new List<AiModelInfo>();
        if (!IsConfigured)
            return result;

        try
        {
            using var request = NewRequest(HttpMethod.Get, "/v1/models?limit=100");
            using var response = await SharedClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return result;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data))
                return result;

            foreach (var model in data.EnumerateArray())
            {
                var id = model.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
                if (string.IsNullOrEmpty(id))
                    continue;

                var display = model.TryGetProperty("display_name", out var nameProperty)
                    ? nameProperty.GetString()
                    : null;
                result.Add(new AiModelInfo
                {
                    Key = id,
                    DisplayName = string.IsNullOrEmpty(display) ? id : display
                });
            }
        }
        catch (Exception)
        {
            // A listing failure is not fatal - the model id can still be typed in.
        }

        return result;
    }

    /// <summary>
    /// Streams a completion. <paramref name="onDelta"/> receives text as it
    /// arrives. Returns the full text.
    /// </summary>
    public async Task<string> GenerateAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> messages,
        Action<string>? onDelta = null)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("No Anthropic API key configured.");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        using var request = NewRequest(HttpMethod.Post, "/v1/messages");
        request.Content = new StringContent(
            BuildRequestBody(systemPrompt, messages, MaxTokens, ModelId),
            Encoding.UTF8,
            "application/json");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await SharedClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Anthropic API returned {(int)response.StatusCode}: {Summarize(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        string? refusalCategory = null;

        while (!reader.EndOfStream)
        {
            token.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null)
                break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0)
                continue;

            var (delta, refusal) = ReadEvent(payload);
            if (refusal != null)
                refusalCategory = refusal;
            if (delta == null)
                continue;

            text.Append(delta);
            onDelta?.Invoke(delta);
        }

        // A refusal arrives as a successful response with an empty or partial
        // body, so it has to be surfaced explicitly or it looks like the model
        // simply had nothing to say.
        if (refusalCategory != null && text.Length == 0)
            throw new InvalidOperationException(
                $"The model declined this request (category: {refusalCategory}).");

        return text.ToString();
    }

    /// <summary>
    /// Request body for the Messages API. Split out so the shape is testable
    /// without a live endpoint.
    /// </summary>
    internal static string BuildRequestBody(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens,
        string modelId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            // Required on every request, unlike the OpenAI-compatible endpoint.
            writer.WriteNumber("max_tokens", maxTokens < 1 ? 1 : maxTokens);
            writer.WriteBoolean("stream", true);

            // The system prompt is a top-level field here, not a message with
            // role "system" - passing it as a message is rejected.
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                writer.WriteString("system", systemPrompt);

            writer.WriteStartArray("messages");
            foreach (var (role, content) in messages)
            {
                if (string.IsNullOrEmpty(content))
                    continue;

                writer.WriteStartObject();
                // Only "user" and "assistant" are accepted.
                writer.WriteString(
                    "role",
                    string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? "assistant"
                        : "user");
                writer.WriteString("content", content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Deliberately no temperature / top_p / top_k: current Claude models
            // reject all three with a 400.
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Pulls the text delta and any refusal category out of one SSE payload.
    /// Unrecognised or malformed events yield nothing rather than throwing - the
    /// stream carries several event types this client does not need.
    /// </summary>
    internal static (string? Delta, string? RefusalCategory) ReadEvent(string payload)
    {
        if (payload == "[DONE]")
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty))
                return (null, null);

            switch (typeProperty.GetString())
            {
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("text", out var textProperty))
                        return (textProperty.GetString(), null);
                    return (null, null);

                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta)
                        && messageDelta.TryGetProperty("stop_reason", out var stopReason)
                        && stopReason.GetString() == "refusal")
                    {
                        var category = "unspecified";
                        if (root.TryGetProperty("stop_details", out var details)
                            && details.ValueKind == JsonValueKind.Object
                            && details.TryGetProperty("category", out var categoryProperty)
                            && categoryProperty.ValueKind == JsonValueKind.String)
                            category = categoryProperty.GetString() ?? category;
                        return (null, category);
                    }
                    return (null, null);

                case "error":
                    var message = root.TryGetProperty("error", out var error)
                        && error.TryGetProperty("message", out var errorMessage)
                        ? errorMessage.GetString()
                        : null;
                    throw new InvalidOperationException(
                        $"Anthropic API stream error: {message ?? "unknown"}");

                default:
                    return (null, null);
            }
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Trims an error body to something loggable without dumping a wall
    /// of JSON into the UI.</summary>
    private static string Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty response)";

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
                return message.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }

        return body.Length > 300 ? body[..300] + "..." : body;
    }
}
