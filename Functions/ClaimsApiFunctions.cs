using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ProjectPulse.Api.Services;

namespace ProjectPulse.Api.Functions;

public sealed class ClaimsApiFunctions
{
    private readonly MockClaimsService _claims;
    private readonly IConfiguration _configuration;

    public ClaimsApiFunctions(
        MockClaimsService claims,
        IConfiguration configuration)
    {
        _claims = claims;
        _configuration = configuration;
    }

    [Function("PulseApiHealth")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "health")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var storageHealthy =
            await _claims.HealthAsync(cancellationToken);

        var response =
            request.CreateResponse(
                storageHealthy
                    ? HttpStatusCode.OK
                    : HttpStatusCode.ServiceUnavailable);

await WriteJsonAsync(
    response,
    new
    {
        IS_HEALTHY = storageHealthy
    });

        return response;
    }

    [Function("GetPendingClaims")]
    public async Task<HttpResponseData> GetPending(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "claims/pending")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var maxRecords =
            GetQueryInt(
                request.Url.Query,
                "maxRecords",
                GetInt(
                    "FAKE_CLAIMS_PER_POLL",
                    10));

        var claims =
            await _claims.GetPendingAsync(
                maxRecords,
                cancellationToken);

        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        await WriteJsonAsync(
            response,
            claims);

        return response;
    }

    [Function("GetClaimById")]
    public async Task<HttpResponseData> GetById(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "claims/by-id/{uniqueId}")]
        HttpRequestData request,
        string uniqueId,
        CancellationToken cancellationToken)
    {
        var claim =
            await _claims.GetByIdAsync(
                uniqueId,
                cancellationToken);

        if (claim is null)
        {
            return request.CreateResponse(
                HttpStatusCode.NotFound);
        }

        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        await WriteJsonAsync(
            response,
            claim);

        return response;
    }

    [Function("SubmitFakeClaim")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "claims")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var reader =
            new StreamReader(
                request.Body,
                Encoding.UTF8);

        var body =
            await reader.ReadToEndAsync(
                cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            body =
                _configuration["FAKE_CLAIM_JSON"]
                ?? "{}";
        }

        try
        {
            JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            var bad =
                request.CreateResponse(
                    HttpStatusCode.BadRequest);

            await WriteJsonAsync(
                bad,
                new
                {
                    error =
                        "Request body is not valid JSON."
                });

            return bad;
        }

        var uniqueId =
            await _claims.SubmitAsync(
                body,
                cancellationToken);

        var response =
            request.CreateResponse(
                HttpStatusCode.Accepted);

        await WriteJsonAsync(
            response,
            new
            {
                uniqueId,
                status = "Pending"
            });

        return response;
    }

    [Function("SaveClaimResult")]
    public async Task<HttpResponseData> SaveResult(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "claims/{uniqueId}/result")]
        HttpRequestData request,
        string uniqueId,
        CancellationToken cancellationToken)
    {
        using var reader =
            new StreamReader(
                request.Body,
                Encoding.UTF8);

        var resultJson =
            await reader.ReadToEndAsync(
                cancellationToken);

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            resultJson = "{}";
        }

        var saved =
            await _claims.SaveResultAsync(
                uniqueId,
                resultJson,
                cancellationToken);

        if (!saved)
        {
            return request.CreateResponse(
                HttpStatusCode.NotFound);
        }

        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        await WriteJsonAsync(
            response,
            new
            {
                uniqueId,
                status = "Completed"
            });

        return response;
    }

    private static async Task WriteJsonAsync(
        HttpResponseData response,
        object value)
    {
        response.Headers.Add(
            "Content-Type",
            "application/json; charset=utf-8");

        response.Headers.Add(
            "Cache-Control",
            "no-store");

        await response.WriteStringAsync(
            JsonSerializer.Serialize(value));
    }

    private int GetInt(
        string name,
        int defaultValue)
    {
        return int.TryParse(
            _configuration[name],
            out var value)
                ? value
                : defaultValue;
    }

    private bool GetBool(
        string name,
        bool defaultValue)
    {
        return bool.TryParse(
            _configuration[name],
            out var value)
                ? value
                : defaultValue;
    }

    private static int GetQueryInt(
        string query,
        string key,
        int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return defaultValue;
        }

        foreach (var pair in query
            .TrimStart('?')
            .Split(
                '&',
                StringSplitOptions.RemoveEmptyEntries))
        {
            var parts =
                pair.Split('=', 2);

            if (parts.Length == 2 &&
                string.Equals(
                    Uri.UnescapeDataString(parts[0]),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(
                    Uri.UnescapeDataString(parts[1]),
                    out var value)
                        ? value
                        : defaultValue;
            }
        }

        return defaultValue;
    }
}
