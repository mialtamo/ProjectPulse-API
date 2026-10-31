using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace ProjectPulse.Api.Services;

public sealed class SqlMockClaimsService
{
    private readonly IConfiguration _configuration;
    private readonly string? _connectionString;

    public SqlMockClaimsService(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration["AZURE_SQL_CONNECTION_STRING"];
    }

    public async Task<bool> HealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<JsonNode>> GetPendingAsync(
        int requestedMax,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);

        var defaultCount = GetInt("FAKE_CLAIMS_PER_POLL", 10, 1, 10000);
        var maxRecords = Math.Clamp(requestedMax <= 0 ? defaultCount : requestedMax, 1, 10000);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.ClaimPendingMockClaims", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@MaxRecords", SqlDbType.Int).Value = maxRecords;

        var claims = new List<JsonNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var payloadOrdinal = reader.GetOrdinal("PayloadJson");

        while (await reader.ReadAsync(cancellationToken))
        {
            var payloadJson = reader.GetString(payloadOrdinal);
            var payload = JsonNode.Parse(payloadJson);
            if (payload is not null)
            {
                claims.Add(payload);
            }
        }

        return claims;
    }

    public async Task<JsonNode?> GetByIdAsync(
        string uniqueId,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT PayloadJson FROM dbo.MockClaimQueue WHERE UniqueId = @UniqueId;",
            connection);
        command.Parameters.Add("@UniqueId", SqlDbType.NVarChar, 128).Value = uniqueId;

        var payloadJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payloadJson is null ? null : JsonNode.Parse(payloadJson);
    }

    public async Task<string> SubmitAsync(
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A single claim JSON object is required.", nameof(payloadJson));
        }

        var correlationId = GetText(root, "CORRELATION_ID");
        var configuredIdField = _configuration["UNIQUE_ID_FIELD"] ?? "uniqueId";
        var uniqueId =
            GetText(root, configuredIdField)
            ?? correlationId
            ?? $"CLAIM-{Guid.NewGuid():N}";

        if (uniqueId.Length > 128)
        {
            uniqueId = uniqueId[..128];
        }

        var requestId = GetText(root, "REQUEST_ID");
        var idempotencyKey = GetText(root, "IDEMPOTENCY_KEY");
        var requestType = GetText(root, "REQUEST_TYPE");
        var program =
            GetText(root, "PROGRAM")
            ?? GetMetadataText(root, "INITIAL_REQUEST_METADATA", "PROGRAM")
            ?? GetMetadataText(root, "UPDATE_REQUEST_METADATA", "PROGRAM");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            DECLARE @ResolvedCorrelationId nvarchar(128) =
                COALESCE(@CorrelationId, CONVERT(nvarchar(36), NEWID()));
            DECLARE @ResolvedPayloadJson nvarchar(max) = @PayloadJson;

            IF JSON_VALUE(@PayloadJson, '$.CORRELATION_ID') IS NULL
            BEGIN
                SET @ResolvedPayloadJson =
                    JSON_MODIFY(@PayloadJson, '$.CORRELATION_ID', @ResolvedCorrelationId);
            END;

            INSERT dbo.MockClaimQueue
            (
                UniqueId, RtaRequestId, CorrelationId, IdempotencyKey,
                RequestType, Program, Status, PayloadJson
            )
            VALUES
            (
                @UniqueId, @RtaRequestId, @ResolvedCorrelationId, @IdempotencyKey,
                @RequestType, @Program, N'QUEUED', @ResolvedPayloadJson
            );
            """,
            connection);

        command.Parameters.Add("@UniqueId", SqlDbType.NVarChar, 128).Value = uniqueId;
        AddNullable(command, "@RtaRequestId", SqlDbType.NVarChar, 100, requestId);
        AddNullable(command, "@CorrelationId", SqlDbType.NVarChar, 128, correlationId);
        AddNullable(command, "@IdempotencyKey", SqlDbType.NVarChar, 128, idempotencyKey);
        AddNullable(command, "@RequestType", SqlDbType.NVarChar, 50, requestType);
        AddNullable(command, "@Program", SqlDbType.NVarChar, 80, program);
        command.Parameters.Add("@PayloadJson", SqlDbType.NVarChar, -1).Value = payloadJson;

        await command.ExecuteNonQueryAsync(cancellationToken);
        return uniqueId;
    }

    public async Task<bool> SaveResultByCorrelationIdAsync(
        string correlationId,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordMockClaimWriteback", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 128).Value = correlationId;
        command.Parameters.Add("@RequestBodyJson", SqlDbType.NVarChar, -1).Value = resultJson;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            reader.GetBoolean(reader.GetOrdinal("Matched"));
    }

    public async Task<bool> SaveResultAsync(
        string uniqueId,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long claimId;
        string correlationId;

        await using (var find = new SqlCommand(
            """
            SELECT TOP (1) ClaimId, CorrelationId
            FROM dbo.MockClaimQueue WITH (UPDLOCK, ROWLOCK)
            WHERE UniqueId = @UniqueId;
            """,
            connection,
            transaction))
        {
            find.Parameters.Add("@UniqueId", SqlDbType.NVarChar, 128).Value = uniqueId;
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            claimId = reader.GetInt64(0);
            correlationId = reader.IsDBNull(1) ? uniqueId : reader.GetString(1);
        }

        await using (var insert = new SqlCommand(
            """
            INSERT dbo.MockClaimWritebacks
                (ClaimId, CorrelationId, ResultStatus, RequestBodyJson)
            VALUES
                (@ClaimId, @CorrelationId, JSON_VALUE(@RequestBodyJson, '$.STATUS'), @RequestBodyJson);

            UPDATE dbo.MockClaimQueue
               SET Status = N'COMPLETED',
                   CompletedAtUtc = SYSUTCDATETIME(),
                   UpdatedAtUtc = SYSUTCDATETIME()
             WHERE ClaimId = @ClaimId;
            """,
            connection,
            transaction))
        {
            insert.Parameters.Add("@ClaimId", SqlDbType.BigInt).Value = claimId;
            insert.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 128).Value = correlationId;
            insert.Parameters.Add("@RequestBodyJson", SqlDbType.NVarChar, -1).Value = resultJson;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "AZURE_SQL_CONNECTION_STRING must be configured for SQL-backed claim storage.");
        }

        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddNullable(
        SqlCommand command,
        string name,
        SqlDbType type,
        int size,
        string? value)
    {
        command.Parameters.Add(name, type, size).Value = (object?)value ?? DBNull.Value;
    }

    private static string? GetMetadataText(JsonElement root, string metadataName, string fieldName) =>
        TryGetProperty(root, metadataName, out var metadata) &&
        metadata.ValueKind == JsonValueKind.Object
            ? GetText(metadata, fieldName)
            : null;

    private static string? GetText(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        var delayMs = GetInt("FAKE_API_DELAY_MS", 0, 0, 60000);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    private int GetInt(string name, int defaultValue, int min, int max) =>
        int.TryParse(_configuration[name], out var parsed)
            ? Math.Clamp(parsed, min, max)
            : defaultValue;
}
