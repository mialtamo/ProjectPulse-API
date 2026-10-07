using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectPulse.Api.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration["MOCK_STORAGE_CONNECTION_STRING"]
        ?? configuration["AzureWebJobsStorage"]
        ?? throw new InvalidOperationException("AzureWebJobsStorage or MOCK_STORAGE_CONNECTION_STRING must be configured.");

    var tableName = configuration["MOCK_CLAIMS_TABLE"] ?? "PulseClaims";
    return new TableClient(connectionString, tableName);
});

builder.Services.AddSingleton<MockClaimsService>();

builder.Build().Run();
