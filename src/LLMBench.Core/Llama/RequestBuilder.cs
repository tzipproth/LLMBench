using System.Text.Json.Nodes;
using LLMBench.Core.Runs;

namespace LLMBench.Core.Llama;

/// <summary>
/// Builds the /v1/chat/completions body from three sources, later ones overwrite earlier ones (concept 8.2):
/// 1. fixed fields from the run definition, 2. thinking fragment, 3. extraRequest.
/// </summary>
public static class RequestBuilder
{
    public static JsonObject Build(RunDefinition def, string? systemMessage, string userMessage, long? seed, string? imageDataUrl = null)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(systemMessage))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemMessage });

        if (imageDataUrl != null)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } },
                    new JsonObject { ["type"] = "text", ["text"] = userMessage },
                },
            });
        }
        else messages.Add(new JsonObject { ["role"] = "user", ["content"] = userMessage });

        var req = new JsonObject();
        if (!string.IsNullOrWhiteSpace(def.Model)) req["model"] = def.Model;
        req["messages"] = messages;
        if (def.Temperature.HasValue) req["temperature"] = def.Temperature.Value;
        if (def.TopP.HasValue) req["top_p"] = def.TopP.Value;
        if (def.TopK.HasValue) req["top_k"] = def.TopK.Value;
        if (def.MinP.HasValue) req["min_p"] = def.MinP.Value;
        if (def.RepeatPenalty.HasValue) req["repeat_penalty"] = def.RepeatPenalty.Value;
        if (def.PresencePenalty.HasValue) req["presence_penalty"] = def.PresencePenalty.Value;
        if (seed.HasValue) req["seed"] = seed.Value;
        if (def.MaxTokens.HasValue) req["max_tokens"] = def.MaxTokens.Value;
        req["stream"] = false;

        var fragment = def.Thinking switch
        {
            "on" => def.ThinkingOn,
            "off" => def.ThinkingOff,
            _ => null,
        };
        if (fragment != null) Merge(req, fragment);
        Merge(req, def.ExtraRequest);
        return req;
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            target[key] = value?.DeepClone();
    }

    /// <summary>Copy of the request with image data shortened, for logging in the result file.</summary>
    public static JsonObject ForLog(JsonObject request)
    {
        var copy = (JsonObject)request.DeepClone();
        foreach (var msg in copy["messages"]!.AsArray())
            if (msg?["content"] is JsonArray parts)
                foreach (var part in parts)
                    if (part?["image_url"]?["url"] is JsonValue url && url.ToString().Length > 100)
                        part["image_url"]!["url"] = url.ToString()[..60] + "…(base64 omitted)";
        return copy;
    }
}
