using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ProjectPulse.Api.Services;

namespace ProjectPulse.Api.Functions;

public sealed class ClaimsApiFunctions
{
    private readonly SqlMockClaimsService _claims;
    private readonly SqlNotificationService _notifications;
    private readonly ApiReportStore _reports;
    private readonly IConfiguration _configuration;

    public ClaimsApiFunctions(
        SqlMockClaimsService claims,
        SqlNotificationService notifications,
        ApiReportStore reports,
        IConfiguration configuration)
    {
        _claims = claims;
        _notifications = notifications;
        _reports = reports;
        _configuration = configuration;
    }

    [Function("PulseApiHealth")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var sqlHealthy = await _claims.HealthAsync(cancellationToken);
        var response = request.CreateResponse(
            sqlHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);

        await WriteJsonAsync(response, new { IS_HEALTHY = sqlHealthy });
        return response;
    }

    [Function("GetPendingClaims")]
    public async Task<HttpResponseData> GetPending(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "claims/pending")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var maxRecords = GetQueryInt(
            request.Url.Query,
            "maxRecords",
            GetInt("FAKE_CLAIMS_PER_POLL", 10));

        var claims = await _claims.GetPendingAsync(maxRecords, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await WriteJsonAsync(response, claims);

        await _reports.RecordAsync(
            "GET",
            request.Url.PathAndQuery,
            (int)HttpStatusCode.OK,
            null,
            claims.Count,
            null,
            JsonSerializer.Serialize(claims),
            cancellationToken);

        return response;
    }

    [Function("GetClaimById")]
    public async Task<HttpResponseData> GetById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "claims/by-id/{uniqueId}")]
        HttpRequestData request,
        string uniqueId,
        CancellationToken cancellationToken)
    {
        var claim = await _claims.GetByIdAsync(uniqueId, cancellationToken);
        if (claim is null)
        {
            await _reports.RecordAsync(
                "GET", request.Url.PathAndQuery, (int)HttpStatusCode.NotFound,
                uniqueId, null, null, null, cancellationToken);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        await WriteJsonAsync(response, claim);
        await _reports.RecordAsync(
            "GET", request.Url.PathAndQuery, (int)HttpStatusCode.OK,
            uniqueId, 1, null, claim.ToJsonString(), cancellationToken);
        return response;
    }

    [Function("SubmitFakeClaim")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "claims")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = _configuration["FAKE_CLAIM_JSON"] ?? "{}";
        }

        try
        {
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A single claim object is required.");
            }
        }
        catch (JsonException)
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            var badBody = new { error = "Request body must be one valid claim JSON object." };
            await WriteJsonAsync(bad, badBody);
            await _reports.RecordAsync(
                "POST", request.Url.PathAndQuery, (int)HttpStatusCode.BadRequest,
                null, null, body, JsonSerializer.Serialize(badBody), cancellationToken);
            return bad;
        }

        var uniqueId = await _claims.SubmitAsync(body, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.Accepted);
        var submitBody = new { uniqueId, status = "QUEUED" };
        await WriteJsonAsync(response, submitBody);
        await _reports.RecordAsync(
            "POST", request.Url.PathAndQuery, (int)HttpStatusCode.Accepted,
            uniqueId, 1, body, JsonSerializer.Serialize(submitBody), cancellationToken);
        return response;
    }

    [Function("SaveRequestStatus")]
    public async Task<HttpResponseData> SaveRequestStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "requests/{correlationId}/status")]
        HttpRequestData request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var resultJson = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            resultJson = "{}";
        }

        try
        {
            using var parsed = JsonDocument.Parse(resultJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A status object is required.");
            }
        }
        catch (JsonException)
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await WriteJsonAsync(bad, new { error = "Request body must be a valid JSON object." });
            return bad;
        }

        var saved = await _claims.SaveResultByCorrelationIdAsync(
            correlationId, resultJson, cancellationToken);

        if (!saved)
        {
            await _reports.RecordAsync(
                "PUT", request.Url.PathAndQuery, (int)HttpStatusCode.NotFound,
                correlationId, 1, resultJson, null, cancellationToken);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        await _reports.RecordAsync(
            "PUT", request.Url.PathAndQuery, (int)HttpStatusCode.OK,
            correlationId, 1, resultJson, null, cancellationToken);
        return response;
    }

    [Function("SaveClaimResult")]
    public async Task<HttpResponseData> SaveResult(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "claims/{uniqueId}/result")]
        HttpRequestData request,
        string uniqueId,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var resultJson = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            resultJson = "{}";
        }

        var saved = await _claims.SaveResultAsync(uniqueId, resultJson, cancellationToken);
        if (!saved)
        {
            await _reports.RecordAsync(
                "POST", request.Url.PathAndQuery, (int)HttpStatusCode.NotFound,
                uniqueId, 1, resultJson, null, cancellationToken);
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        var resultBody = new { uniqueId, status = "Completed" };
        await WriteJsonAsync(response, resultBody);
        await _reports.RecordAsync(
            "POST", request.Url.PathAndQuery, (int)HttpStatusCode.OK,
            uniqueId, 1, resultJson, JsonSerializer.Serialize(resultBody), cancellationToken);
        return response;
    }

    [Function("CreateAdminNotification")]
    public async Task<HttpResponseData> CreateAdminNotification(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/notifications")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A notification object is required.");
            }

            var notificationId = await _notifications.SaveAsync(body, cancellationToken);
            const string receipt = "Notification accepted.";
            var response = request.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteStringAsync(receipt);

            await _reports.RecordAsync(
                "POST",
                request.Url.PathAndQuery,
                (int)HttpStatusCode.OK,
                notificationId.ToString(),
                1,
                body,
                receipt,
                cancellationToken);

            return response;
        }
        catch (JsonException)
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await WriteJsonAsync(bad, new { error = "Request body must be a valid notification JSON object." });
            return bad;
        }
        catch (ArgumentException exception)
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await WriteJsonAsync(bad, new { error = exception.Message });
            return bad;
        }
    }

    private static async Task WriteJsonAsync(HttpResponseData response, object value)
    {
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(value));
    }

    private int GetInt(string name, int defaultValue) =>
        int.TryParse(_configuration[name], out var value) ? value : defaultValue;

    private static int GetQueryInt(string query, string key, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return defaultValue;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 &&
                string.Equals(
                    Uri.UnescapeDataString(parts[0]),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(Uri.UnescapeDataString(parts[1]), out var value)
                    ? value
                    : defaultValue;
            }
        }

        return defaultValue;
    }
}
