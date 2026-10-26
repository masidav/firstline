using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using YamlDotNet.Serialization;

namespace FandomScore.Tests.Contract;

/// <summary>
/// openapi.yaml as a checker: which operation a route is, which statuses it documents, and whether a response body
/// matches the documented schema (JSON Schema 2020-12, as OpenAPI 3.1 uses). Component schemas are inlined under
/// <c>$defs</c> so every response schema is self-contained.
/// </summary>
public sealed partial class OpenApiContract
{
    private static readonly Lazy<OpenApiContract> Loaded = new(() => new OpenApiContract(Path.Combine(AppContext.BaseDirectory, "openapi.yaml")));

    private static readonly EvaluationOptions Evaluation = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

    private readonly JsonNode _root;
    private readonly string _defs;
    private readonly Dictionary<string, (string Path, JsonObject Operation)> _operations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _planned = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    public static OpenApiContract Instance => Loaded.Value;

    public OpenApiContract(string file)
    {
        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build()
            .Deserialize<object>(File.ReadAllText(file));
        _root = JsonNode.Parse(JsonSerializer.Serialize(yaml))!;
        _defs = Rewrite(_root["components"]!["schemas"]!.ToJsonString());
        foreach (var (path, item) in _root["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (method is "get" or "put" or "post" or "patch" or "delete")
                {
                    _operations[Key(method, path)] = (path, operation!.AsObject());
                    if (operation!["x-requires-schema"] is not null || operation["x-planned"] is not null)
                    {
                        _planned.Add(Key(method, path));
                    }
                }
            }
        }
    }

    /// <summary>Every documented operation as "METHOD /path/{}" (parameters normalized).</summary>
    public IReadOnlyCollection<string> Operations => _operations.Keys;

    /// <summary>Operations marked <c>x-requires-schema</c> or <c>x-planned</c>: designed, not built.</summary>
    public IReadOnlyCollection<string> Planned => _planned;

    /// <summary>"GET /allocations/{}/eligibility": the method and a route with every parameter written as {}.</summary>
    public static string Key(string method, string route)
    {
        var path = route.StartsWith("/v1/", StringComparison.Ordinal) ? route[3..] : route;
        return $"{method.ToUpperInvariant()} {Parameter().Replace(path, "{}").TrimEnd('/')}";
    }

    /// <summary>What doesn't match the contract in one response; empty when it matches.</summary>
    public IReadOnlyList<string> Check(string method, string route, int status, string? contentType, ReadOnlySpan<byte> body)
    {
        var key = Key(method, route);
        if (!_operations.TryGetValue(key, out var found))
        {
            return [$"{key} is not in openapi.yaml"];
        }

        var responses = found.Operation["responses"]?.AsObject();
        var response = responses?[status.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                       ?? responses?[$"{status / 100}XX"] ?? responses?["default"]
                       ?? Implicit(found.Path, found.Operation, status);
        if (response is null)
        {
            return [$"{key} answered {status}, which openapi.yaml doesn't list for it"];
        }

        response = Resolve(response);
        var content = response["content"]?.AsObject();
        var mediaType = contentType?.Split(';')[0].Trim();
        if (body.Length == 0)
        {
            return content is { Count: > 0 } && status != 204 && method != "HEAD"
                ? [$"{key} {status} has an empty body, but openapi.yaml describes one"]
                : [];
        }

        if (content is null || content.Count == 0)
        {
            return [$"{key} {status} has a body ({mediaType}), but openapi.yaml describes none"];
        }

        var documented = content.FirstOrDefault(c => MediaMatches(c.Key, mediaType));
        if (documented.Key is null)
        {
            return [$"{key} {status} answered {mediaType}; openapi.yaml lists {string.Join(", ", content.Select(c => c.Key))}"];
        }

        if (mediaType is null || !mediaType.EndsWith("json", StringComparison.Ordinal) || documented.Value?["schema"] is not { } schemaNode)
        {
            return [];
        }

        JsonDocument instance;
        try
        {
            instance = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return [$"{key} {status} sent {mediaType} that isn't JSON"];
        }

        using (instance)
        {
            var schema = _schemas.GetOrAdd($"{key} {status} {documented.Key}", _ => Build(schemaNode));
            var result = schema.Evaluate(instance.RootElement, Evaluation);
            return result.IsValid ? [] : [.. Errors(result).Select(e => $"{key} {status}: {e}")];
        }
    }

    // The conventions at the top of openapi.yaml: an operation that needs sign-in can answer 401, and an /admin one 403,
    // without listing them.
    private JsonNode? Implicit(string path, JsonObject operation, int status)
    {
        var signedIn = operation["security"] is not JsonArray { Count: 0 } && _root["security"] is JsonArray { Count: > 0 };
        return status switch
        {
            401 when signedIn => _root["components"]!["responses"]!["Unauthorized"],
            403 when signedIn && path.StartsWith("/admin/", StringComparison.Ordinal) => _root["components"]!["responses"]!["Forbidden"],
            _ => null,
        };
    }

    private JsonNode Resolve(JsonNode node)
    {
        while (node["$ref"]?.GetValue<string>() is { } reference)
        {
            node = reference.Split('/').Skip(1).Aggregate(_root, (n, part) => n[part]!);
        }

        return node;
    }

    private JsonSchema Build(JsonNode schema)
    {
        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = JsonNode.Parse(_defs),
            ["allOf"] = new JsonArray(JsonNode.Parse(Rewrite(schema.ToJsonString()))),
        };
        return JsonSchema.FromText(document.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() },
            new Uri($"https://contract.test/{Guid.NewGuid():N}"));
    }

    // The leaf errors with where they are; the summaries above them ("some properties did not match") add nothing.
    private static IEnumerable<string> Errors(EvaluationResults result)
    {
        var all = (result.Details ?? []).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => (Location: d.InstanceLocation.ToString(), e.Key, e.Value)))
            .ToList();
        var leaves = all.Where(e => e.Key is not ("allOf" or "properties" or "items" or "$ref" or "additionalProperties" or "anyOf")).ToList();
        return (leaves.Count > 0 ? leaves : all).Select(e => $"{(e.Location.Length == 0 ? "(body)" : e.Location)} {e.Key}: {e.Value}").Distinct();
    }

    private static bool MediaMatches(string documented, string? actual) =>
        actual is not null && (documented == actual || documented == "*/*"
                               || (documented.EndsWith("/*", StringComparison.Ordinal) && actual.StartsWith(documented[..^1], StringComparison.Ordinal)));

    private static string Rewrite(string json) => json.Replace("\"#/components/schemas/", "\"#/$defs/", StringComparison.Ordinal);

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Parameter();
}
