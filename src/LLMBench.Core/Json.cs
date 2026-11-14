using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMBench.Core;

/// <summary>Shared JSON settings. All numbers are written/read culture-invariant by System.Text.Json.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Indented = Create(true);
    public static readonly JsonSerializerOptions Compact = Create(false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Indented);

    public static string Pretty(JsonNode? node) =>
        node is null ? "" : node.ToJsonString(Indented);

    /// <summary>Parses a JSON object from text; empty text gives an empty object.</summary>
    public static JsonObject ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return node as JsonObject ?? throw new FormatException("Expected a JSON object { ... }.");
    }

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>Errors meant to be shown to the user as-is (configuration, catalog, server problems).</summary>
public sealed class BenchException(string message) : Exception(message);
