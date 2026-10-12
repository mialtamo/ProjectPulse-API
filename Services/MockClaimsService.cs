using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Data.Tables;
using ProjectPulse.Api.Models;

namespace ProjectPulse.Api.Services;

public sealed class MockClaimsService
{
    private const string PartitionKey = "claims";

    private readonly TableClient _table;
    private readonly IConfiguration _configuration;

    public MockClaimsService(TableClient table, IConfiguration configuration)
    {
        _table = table;
        _configuration = configuration;
    }

    public async Task<bool> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            await foreach (var _ in _table.QueryAsync<TableEntity>(maxPerPage: 1, cancellationToken: cancellationToken))
            {
                break;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<JsonNode>> GetPendingAsync(int requestedMax, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        await _table.CreateIfNotExistsAsync(cancellationToken);

        var configuredCount = GetInt("FAKE_CLAIMS_PER_POLL", 10, 1, 10000);
        var maxRecords = Math.Clamp(requestedMax <= 0 ? configuredCount : requestedMax, 1, 10000);
        var take = Math.Min(configuredCount, maxRecords);
        var autoGenerate = GetBool("FAKE_AUTO_GENERATE_ON_POLL", true);

        if (autoGenerate)
        {
            var generated = new List<JsonNode>(take);
            for (var i = 0; i < take; i++)
            {
                var payload = BuildPayload();
                var uniqueId = GetUniqueId(payload);
                await UpsertAsync(uniqueId, payload.ToJsonString(), "Processing", DateTimeOffset.UtcNow, cancellationToken);
                generated.Add(payload);
            }
            return generated;
        }

        var pending = new List<JsonNode>();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {PartitionKey} and Status eq {"Pending"}");

        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, maxPerPage: take, cancellationToken: cancellationToken))
        {
            if (pending.Count >= take)
                break;

            entity["Status"] = "Processing";
            entity["ProcessingAt"] = DateTimeOffset.UtcNow;
            await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);

            var payloadJson = entity.GetString("PayloadJson") ?? "{}";
            pending.Add(JsonNode.Parse(payloadJson) ?? new JsonObject());
        }

        return pending;
    }

    public async Task<JsonNode?> GetByIdAsync(string uniqueId, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        await _table.CreateIfNotExistsAsync(cancellationToken);

        try
        {
            var entity = (await _table.GetEntityAsync<TableEntity>(PartitionKey, uniqueId, cancellationToken: cancellationToken)).Value;
            var json = entity.GetString("PayloadJson") ?? "{}";
            return JsonNode.Parse(json);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<string> SubmitAsync(string payloadJson, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        await _table.CreateIfNotExistsAsync(cancellationToken);

        var payload = JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
        var uniqueId = GetUniqueId(payload);
        await UpsertAsync(uniqueId, payload.ToJsonString(), "Pending", null, cancellationToken);
        return uniqueId;
    }

    public async Task<bool> SaveResultAsync(string uniqueId, string resultJson, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        await _table.CreateIfNotExistsAsync(cancellationToken);

        try
        {
            var entity = (await _table.GetEntityAsync<TableEntity>(PartitionKey, uniqueId, cancellationToken: cancellationToken)).Value;
            return await CompleteEntityAsync(entity, resultJson, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<bool> SaveResultByCorrelationIdAsync(
        string correlationId,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        await _table.CreateIfNotExistsAsync(cancellationToken);

        TableEntity? newestMatch = null;

        var filter =
            TableClient.CreateQueryFilter(
                $"PartitionKey eq {PartitionKey}");

        await foreach (
            var entity in _table.QueryAsync<TableEntity>(
                filter,
                cancellationToken: cancellationToken))
        {
            var payloadJson =
                entity.GetString("PayloadJson")
                ?? "{}";

            try
            {
                using var document =
                    JsonDocument.Parse(payloadJson);

                var root =
                    document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty(
                        "CORRELATION_ID",
                        out var value))
                {
                    continue;
                }

                var candidate =
                    value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.ToString();

                if (!string.Equals(
                    candidate,
                    correlationId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (newestMatch is null ||
                    GetSubmittedAt(entity) >
                    GetSubmittedAt(newestMatch))
                {
                    newestMatch = entity;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return newestMatch is not null &&
            await CompleteEntityAsync(
                newestMatch,
                resultJson,
                cancellationToken);
    }

    private async Task<bool> CompleteEntityAsync(
        TableEntity entity,
        string resultJson,
        CancellationToken cancellationToken)
    {
        entity["Status"] = "Completed";
        entity["ResultJson"] = resultJson;
        entity["CompletedAt"] = DateTimeOffset.UtcNow;

        await _table.UpdateEntityAsync(
            entity,
            entity.ETag,
            TableUpdateMode.Replace,
            cancellationToken);

        return true;
    }

    private static DateTimeOffset GetSubmittedAt(
        TableEntity entity) =>
        entity.GetDateTimeOffset("SubmittedAt")
        ?? DateTimeOffset.MinValue;

    private JsonObject BuildPayload()
    {
        var template = _configuration["FAKE_CLAIM_JSON"];
        JsonObject payload;

        try
        {
            payload = JsonNode.Parse(string.IsNullOrWhiteSpace(template) ? "{}" : template) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            payload = new JsonObject
            {
                ["templateError"] = "FAKE_CLAIM_JSON is not valid JSON"
            };
        }

        ApplyGeneratedValues(payload);

        var uniqueIdField = _configuration["UNIQUE_ID_FIELD"] ?? "uniqueId";
        payload.Remove(uniqueIdField);
        GetUniqueId(payload);

        payload["mockGeneratedAt"] = DateTimeOffset.UtcNow;
        return payload;
    }

    private static void ApplyGeneratedValues(JsonObject payload)
    {
        payload["REQUEST_ID"] = CreateRequestId();
        payload["CORRELATION_ID"] = Guid.NewGuid().ToString();
        payload["IDEMPOTENCY_KEY"] = Guid.NewGuid().ToString();

        if (payload["METADATA"] is JsonObject metadata)
        {
            metadata["PATIENT_ICN"] = CreateIcn();
            metadata["SPONSOR_ICN"] = CreateIcn();

            if (metadata["START_DATE_OF_SERVICE"] is not null)
            {
                var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 90)));
                metadata["START_DATE_OF_SERVICE"] = start.ToString("yyyy-MM-dd");
            }

            if (metadata["END_DATE_OF_SERVICE"] is not null)
            {
                var end = DateOnly.FromDateTime(DateTime.UtcNow);
                metadata["END_DATE_OF_SERVICE"] = end.ToString("yyyy-MM-dd");
            }
        }

        if (payload["memberId"] is not null)
        {
            payload["memberId"] = $"MEM-{Random.Shared.Next(10000, 99999)}";
        }

        if (payload["claimAmount"] is not null)
        {
            payload["claimAmount"] = Math.Round(Random.Shared.NextDouble() * 5000 + 25, 2);
        }

        if (payload["procedureCode"] is not null)
        {
            payload["procedureCode"] = Random.Shared.Next(10000, 99999).ToString();
        }
    }

    private static long CreateRequestId() =>
        BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue;

    private static string CreateIcn() =>
        $"{RandomDigits(10)}V{RandomDigits(6)}";

    private static string RandomDigits(int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)('0' + Random.Shared.Next(0, 10));
        }

        return new string(chars);
    }

    private string GetUniqueId(JsonObject payload)
    {
        var fieldName = _configuration["UNIQUE_ID_FIELD"] ?? "uniqueId";
        var existing = payload[fieldName]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var uniqueId = $"CLAIM-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..42];
        payload[fieldName] = uniqueId;
        return uniqueId;
    }

    private async Task UpsertAsync(
        string uniqueId,
        string payloadJson,
        string status,
        DateTimeOffset? processingAt,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new TableEntity(PartitionKey, uniqueId)
        {
            ["PayloadJson"] = payloadJson,
            ["Status"] = status,
            ["SubmittedAt"] = now
        };

        if (processingAt is not null)
            entity["ProcessingAt"] = processingAt.Value;

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        var delayMs = GetInt("FAKE_API_DELAY_MS", 0, 0, 60000);
        if (delayMs > 0)
            await Task.Delay(delayMs, cancellationToken);
    }

    private int GetInt(string name, int defaultValue, int min, int max)
    {
        return int.TryParse(_configuration[name], out var parsed)
            ? Math.Clamp(parsed, min, max)
            : defaultValue;
    }

    private bool GetBool(string name, bool defaultValue)
    {
        return bool.TryParse(_configuration[name], out var parsed) ? parsed : defaultValue;
    }
}
