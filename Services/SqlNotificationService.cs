using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ProjectPulse.Api.Services;

public sealed class SqlNotificationService
{
    private readonly string? _connectionString;

    public SqlNotificationService(IConfiguration configuration)
    {
        _connectionString = configuration["AZURE_SQL_CONNECTION_STRING"];
    }

    public async Task<long> SaveAsync(
        string requestBodyJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(requestBodyJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A notification JSON object is required.", nameof(requestBodyJson));
        }

        var emailSubject = GetString(root, "EMAIL_SUBJECT");
        var emailBody = GetString(root, "EMAIL_BODY");
        if (string.IsNullOrWhiteSpace(emailSubject))
        {
            throw new ArgumentException("EMAIL_SUBJECT is required.", nameof(requestBodyJson));
        }

        if (string.IsNullOrWhiteSpace(emailBody))
        {
            throw new ArgumentException("EMAIL_BODY is required.", nameof(requestBodyJson));
        }

        var externalCorrelationId = GetString(root, "EXTERNAL_CORRELATION_ID");
        if (string.Equals(externalCorrelationId, "null", StringComparison.OrdinalIgnoreCase))
        {
            externalCorrelationId = null;
        }

        var requestId = GetInt64(root, "REQUEST_ID");
        var requestQueueId = GetInt64(root, "REQUEST_QUEUE_ID");
        string? attributesJson = null;
        if (TryGetProperty(root, "NOTIFICATION_ATTRS", out var attributes) &&
            attributes.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            attributesJson = attributes.GetRawText();
        }

        if (attributesJson is not null && !IsJson(attributesJson))
        {
            throw new ArgumentException("NOTIFICATION_ATTRS must contain valid JSON.", nameof(requestBodyJson));
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "AZURE_SQL_CONNECTION_STRING must be configured for SQL-backed notification storage.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            INSERT dbo.MockNotifications
            (
                ExternalCorrelationId, EmailSubject, EmailBody,
                RequestId, RequestQueueId, NotificationAttrsJson, RequestBodyJson
            )
            OUTPUT INSERTED.NotificationId
            VALUES
            (
                @ExternalCorrelationId, @EmailSubject, @EmailBody,
                @RequestId, @RequestQueueId, @NotificationAttrsJson, @RequestBodyJson
            );
            """,
            connection);

        AddNullable(command, "@ExternalCorrelationId", SqlDbType.NVarChar, 128, externalCorrelationId);
        command.Parameters.Add("@EmailSubject", SqlDbType.NVarChar, 500).Value = emailSubject;
        command.Parameters.Add("@EmailBody", SqlDbType.NVarChar, -1).Value = emailBody;
        AddNullable(command, "@RequestId", SqlDbType.BigInt, requestId);
        AddNullable(command, "@RequestQueueId", SqlDbType.BigInt, requestQueueId);
        command.Parameters.Add("@NotificationAttrsJson", SqlDbType.NVarChar, -1).Value =
            (object?)attributesJson ?? DBNull.Value;
        command.Parameters.Add("@RequestBodyJson", SqlDbType.NVarChar, -1).Value = requestBodyJson;

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static long? GetInt64(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(value.ToString(), out var parsed) ? parsed : null;
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

    private static bool IsJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddNullable(
        SqlCommand command,
        string name,
        SqlDbType type,
        long? value)
    {
        command.Parameters.Add(name, type).Value = (object?)value ?? DBNull.Value;
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
}
