using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ServerMonitor.Application.Logs;
using ServerMonitor.Contracts.Logs;

namespace ServerMonitor.Infrastructure.Logs;

public sealed class OpenSearchOptions
{
    public const string SectionName = "OpenSearch";

    public string Url { get; set; } = "http://localhost:9200";

    public int Replicas { get; set; }

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(14);
}

internal sealed class OpenSearchLogStore(HttpClient http) : ILogWriter, ILogReader
{
    public const string DataStream = "logs-servermonitor";

    public async Task WriteAsync(IReadOnlyList<LogRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        var body = new StringBuilder();
        foreach (var record in records)
        {
            var entryId = EntryId(record.ServerId, record.Id);
            body.Append(JsonSerializer.Serialize(new { create = new { _index = DataStream, _id = entryId } })).Append('\n');
            body.Append(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["@timestamp"] = record.Time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["entry_id"] = entryId,
                ["server_id"] = record.ServerId.ToString(),
                ["level"] = record.Level,
                ["source"] = record.Source,
                ["message"] = record.Message,
            })).Append('\n');
        }

        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var response = await SendAsync(() => http.PostAsync("_bulk", content, ct), ct);
        var result = await ReadJsonAsync(response, ct);

        if (result["errors"]?.GetValue<bool>() != true)
        {
            return;
        }

        // 409 = already stored (re-delivery).
        var failure = result["items"]!.AsArray()
            .Select(item => item!["create"]!)
            .FirstOrDefault(item => item["status"]!.GetValue<int>() is not (>= 200 and < 300) and not 409);

        if (failure is not null)
        {
            throw new LogStoreException($"OpenSearch rejected a log entry: {failure["error"]?.ToJsonString()}");
        }
    }

    public async Task<LogSearchResultDto> SearchAsync(LogQuery query, CancellationToken ct)
    {
        var filters = new JsonArray
        {
            new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["@timestamp"] = new JsonObject
                    {
                        ["gte"] = query.From.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        ["lt"] = query.To.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    },
                },
            },
        };

        if (query.ServerId is { } serverId)
        {
            filters.Add(new JsonObject { ["term"] = new JsonObject { ["server_id"] = serverId.ToString() } });
        }

        if (query.Levels.Count > 0)
        {
            filters.Add(new JsonObject { ["terms"] = new JsonObject { ["level"] = new JsonArray([.. query.Levels.Select(l => JsonValue.Create(l))]) } });
        }

        var boolQuery = new JsonObject { ["filter"] = filters };
        if (query.Text is { } text)
        {
            boolQuery["must"] = new JsonArray(new JsonObject
            {
                ["match"] = new JsonObject { ["message"] = new JsonObject { ["query"] = text, ["operator"] = "and" } },
            });
        }

        var search = new JsonObject
        {
            ["size"] = query.Limit,
            ["track_total_hits"] = false,
            ["query"] = new JsonObject { ["bool"] = boolQuery },

            // entry_id breaks ties, otherwise search_after can skip entries.
            ["sort"] = new JsonArray(
                new JsonObject { ["@timestamp"] = "desc" },
                new JsonObject { ["entry_id"] = "desc" }),
        };

        if (query.After is { } after)
        {
            search["search_after"] = new JsonArray(after.TimeMilliseconds, after.EntryId);
        }

        using var response = await SendAsync(() => http.PostAsJsonAsync($"{DataStream}/_search", search, ct), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new LogSearchResultDto([], null);
        }

        var result = await ReadJsonAsync(response, ct);
        var hits = result["hits"]!["hits"]!.AsArray();

        var entries = hits.Select(hit =>
        {
            var source = hit!["_source"]!;
            var entryId = source["entry_id"]!.GetValue<string>();
            return new LogEntryDto(
                entryId[(entryId.IndexOf(':', StringComparison.Ordinal) + 1)..],
                Guid.Parse(source["server_id"]!.GetValue<string>()),
                DateTimeOffset.Parse(source["@timestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                source["level"]!.GetValue<string>(),
                source["source"]!.GetValue<string>(),
                source["message"]!.GetValue<string>());
        }).ToList();

        string? next = null;
        if (hits.Count == query.Limit && hits[^1]!["sort"] is JsonArray sort)
        {
            next = new LogCursor(sort[0]!.GetValue<long>(), sort[1]!.GetValue<string>()).Encode();
        }

        return new LogSearchResultDto(entries, next);
    }

    private static string EntryId(Guid serverId, string agentEntryId) => $"{serverId:N}:{agentEntryId}";

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new LogStoreException($"OpenSearch is unreachable: {ex.Message}");
        }
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new LogStoreException($"OpenSearch returned {(int)response.StatusCode}: {text[..Math.Min(text.Length, 500)]}");
        }

        return JsonNode.Parse(text) ?? throw new LogStoreException("OpenSearch returned an empty response.");
    }
}

public sealed class OpenSearchProvisioner(HttpClient http, IOptions<OpenSearchOptions> options)
{
    private const string PolicyId = "logs-servermonitor-retention";

    public async Task EnsureAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var retention = $"{(int)settings.Retention.TotalDays}d";

        var policy = new JsonObject
        {
            ["policy"] = new JsonObject
            {
                ["description"] = $"Delete ServerMonitor logs after {retention}",
                ["default_state"] = "hot",
                ["states"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "hot",
                        ["actions"] = new JsonArray(new JsonObject { ["rollover"] = new JsonObject { ["min_index_age"] = "1d" } }),
                        ["transitions"] = new JsonArray(new JsonObject
                        {
                            ["state_name"] = "delete",
                            ["conditions"] = new JsonObject { ["min_index_age"] = retention },
                        }),
                    },
                    new JsonObject
                    {
                        ["name"] = "delete",
                        ["actions"] = new JsonArray(new JsonObject { ["delete"] = new JsonObject() }),
                        ["transitions"] = new JsonArray(),
                    }),
                ["ism_template"] = new JsonArray(new JsonObject
                {
                    ["index_patterns"] = new JsonArray(OpenSearchLogStore.DataStream + "*"),
                    ["priority"] = 100,
                }),
            },
        };

        using (var response = await http.PutAsJsonAsync($"_plugins/_ism/policies/{PolicyId}", policy, ct))
        {
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                throw new LogStoreException($"Creating the ISM policy failed: {await response.Content.ReadAsStringAsync(ct)}");
            }
        }

        var template = new JsonObject
        {
            ["index_patterns"] = new JsonArray(OpenSearchLogStore.DataStream + "*"),
            ["data_stream"] = new JsonObject(),
            ["priority"] = 100,
            ["template"] = new JsonObject
            {
                ["settings"] = new JsonObject { ["number_of_shards"] = 1, ["number_of_replicas"] = settings.Replicas },
                ["mappings"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["@timestamp"] = new JsonObject { ["type"] = "date" },
                        ["entry_id"] = new JsonObject { ["type"] = "keyword" },
                        ["server_id"] = new JsonObject { ["type"] = "keyword" },
                        ["level"] = new JsonObject { ["type"] = "keyword" },
                        ["source"] = new JsonObject { ["type"] = "keyword" },
                        ["message"] = new JsonObject { ["type"] = "text" },
                    },
                },
            },
        };

        using (var response = await http.PutAsJsonAsync($"_index_template/{OpenSearchLogStore.DataStream}", template, ct))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LogStoreException($"Creating the index template failed: {await response.Content.ReadAsStringAsync(ct)}");
            }
        }

        // Created up front: a search right after the first write could hit unallocated shards.
        using (var response = await http.PutAsync($"_data_stream/{OpenSearchLogStore.DataStream}", content: null, ct))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode && !body.Contains("resource_already_exists_exception", StringComparison.Ordinal))
            {
                throw new LogStoreException($"Creating the data stream failed: {body}");
            }
        }

        using (var response = await http.GetAsync(
            $"_cluster/health/{OpenSearchLogStore.DataStream}?wait_for_status=yellow&timeout=60s", ct))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LogStoreException($"The log data stream did not become ready: {await response.Content.ReadAsStringAsync(ct)}");
            }
        }
    }
}
