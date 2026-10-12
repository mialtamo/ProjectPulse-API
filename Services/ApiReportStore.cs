using Azure;
using Azure.Data.Tables;

namespace ProjectPulse.Api.Services;

public sealed record ApiReportRecord(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Method,
    string Route,
    int HttpStatusCode,
    string? UniqueId,
    int? ClaimCount,
    string? RequestBody,
    string? ResponseBody);

public sealed class ApiReportStore
{
    private const string PartitionKey = "reports";
    private const int MaxBodyLength = 24000;

    private readonly TableClient _table;

    public ApiReportStore(IConfiguration configuration)
    {
        var connectionString =
            configuration["MOCK_STORAGE_CONNECTION_STRING"]
            ?? configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException(
                "AzureWebJobsStorage or MOCK_STORAGE_CONNECTION_STRING must be configured.");

        var tableName =
            configuration["MOCK_REPORTS_TABLE"]
            ?? "PulseApiReports";

        _table = new TableClient(
            connectionString,
            tableName);
    }

    public async Task RecordAsync(
        string method,
        string route,
        int httpStatusCode,
        string? uniqueId,
        int? claimCount,
        string? requestBody,
        string? responseBody,
        CancellationToken cancellationToken)
    {
        try
        {
            await _table.CreateIfNotExistsAsync(
                cancellationToken);

            var now = DateTimeOffset.UtcNow;

            var rowKey =
                $"{DateTimeOffset.MaxValue.Ticks - now.Ticks:D19}-{Guid.NewGuid():N}";

            var entity =
                new TableEntity(
                    PartitionKey,
                    rowKey)
                {
                    ["CreatedAtUtc"] = now,
                    ["Method"] = method,
                    ["Route"] = route,
                    ["HttpStatusCode"] = httpStatusCode
                };

            if (!string.IsNullOrWhiteSpace(uniqueId))
                entity["UniqueId"] = uniqueId;

            if (claimCount is not null)
                entity["ClaimCount"] = claimCount.Value;

            if (!string.IsNullOrWhiteSpace(requestBody))
                entity["RequestBody"] = Truncate(requestBody);

            if (!string.IsNullOrWhiteSpace(responseBody))
                entity["ResponseBody"] = Truncate(responseBody);

            await _table.AddEntityAsync(
                entity,
                cancellationToken);
        }
        catch
        {
            // Reporting is intentionally best-effort for this mock test API.
        }
    }

    public async Task<IReadOnlyList<ApiReportRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(
            cancellationToken);

        var items =
            new List<ApiReportRecord>();

        var filter =
            TableClient.CreateQueryFilter(
                $"PartitionKey eq {PartitionKey}");

        await foreach (
            var entity in _table.QueryAsync<TableEntity>(
                filter,
                maxPerPage: Math.Clamp(limit, 1, 200),
                cancellationToken: cancellationToken))
        {
            items.Add(
                Map(entity));

            if (items.Count >= limit)
                break;
        }

        return items
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToList();
    }

    public async Task<ApiReportRecord?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await _table.CreateIfNotExistsAsync(
            cancellationToken);

        try
        {
            var entity =
                (await _table.GetEntityAsync<TableEntity>(
                    PartitionKey,
                    id,
                    cancellationToken: cancellationToken))
                .Value;

            return Map(entity);
        }
        catch (RequestFailedException ex)
            when (ex.Status == 404)
        {
            return null;
        }
    }

    private static ApiReportRecord Map(
        TableEntity entity)
    {
        return new ApiReportRecord(
            entity.RowKey,
            entity.GetDateTimeOffset("CreatedAtUtc")
                ?? DateTimeOffset.MinValue,
            entity.GetString("Method")
                ?? string.Empty,
            entity.GetString("Route")
                ?? string.Empty,
            entity.GetInt32("HttpStatusCode")
                ?? 0,
            entity.GetString("UniqueId"),
            entity.GetInt32("ClaimCount"),
            entity.GetString("RequestBody"),
            entity.GetString("ResponseBody"));
    }

    private static string Truncate(
        string value)
    {
        return value.Length <= MaxBodyLength
            ? value
            : value[..MaxBodyLength] + "\n...TRUNCATED...";
    }
}
