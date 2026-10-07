[Function("PulseApiLogo")]
public async Task<HttpResponseData> Logo(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pulse-api-logo")]
    HttpRequestData request)
{
    var response = request.CreateResponse(HttpStatusCode.OK);

    response.Headers.Add("Content-Type", "image/png");
    response.Headers.Add("Cache-Control", "public, max-age=86400, immutable");
    response.Headers.Add("X-Content-Type-Options", "nosniff");

    var assembly = Assembly.GetExecutingAssembly();

    var resources = assembly.GetManifestResourceNames();

    var resourceName = resources
        .FirstOrDefault(name =>
            name.EndsWith(
                "project-pulse-logo.png",
                StringComparison.OrdinalIgnoreCase));

    if (resourceName is null)
    {
        var debug = request.CreateResponse(HttpStatusCode.NotFound);

        await debug.WriteStringAsync(
            "Embedded resources: " +
            string.Join(", ", resources));

        return debug;
    }

    await using var resource =
        assembly.GetManifestResourceStream(resourceName);

    if (resource is null)
    {
        var debug = request.CreateResponse(HttpStatusCode.NotFound);

        await debug.WriteStringAsync(
            $"Resource found but stream could not be opened: {resourceName}");

        return debug;
    }

    await resource.CopyToAsync(response.Body);

    return response;
}
