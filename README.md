# Project Pulse API

Mock claims API for the Project Pulse POC. Claim queue records, full RTA-shaped request payloads, status PUT writebacks, and admin notifications are stored in Azure SQL Database. Azure Table Storage remains in use for the API activity/reporting history and the Azure Functions host storage.

## Azure SQL setup

1. Create or select the Azure SQL database (the current POC database is `project-pulse-db`).
2. Run `database/001_create_claim_queue.sql` against that database to create the queue and writeback tables, indexes, and stored procedures. Correlation IDs are required and unique in SQL; the database generates one for inserts that omit it.
3. For an existing database created before correlation IDs were constrained as unique and required, run `database/004_enforce_correlation_ids.sql` to backfill missing IDs and enforce the constraint.
4. Run `database/002_create_notifications.sql` against that database to create the notification inbox table.
4. Set the Function App application setting `AZURE_SQL_CONNECTION_STRING` to a SQL connection string for that database. For a deployed Function App with a system-assigned managed identity, use a connection string with `Authentication=Active Directory Managed Identity`. Grant that identity a database user and the required permissions. For local development, use an appropriate developer credential, such as `Authentication=Active Directory Default`, or a SQL-auth connection string stored only in local settings. The project references the matching `Microsoft.Data.SqlClient.Extensions.Azure` 7.1.1 package because Microsoft.Data.SqlClient 7.x requires it for Microsoft Entra authentication.
5. Allow the Function App to reach the SQL server through its firewall/network configuration. Do not commit real credentials or connection strings.

For a system-assigned managed identity, connect as a database administrator and grant access in `project-pulse-db` (replace the bracketed name with the Function App identity name):

```sql
CREATE USER [<function-app-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<function-app-name>];
ALTER ROLE db_datawriter ADD MEMBER [<function-app-name>];
GRANT EXECUTE ON OBJECT::dbo.ClaimPendingMockClaims TO [<function-app-name>];
GRANT EXECUTE ON OBJECT::dbo.RecordMockClaimWriteback TO [<function-app-name>];
```

The Function App also needs `AzureWebJobsStorage` for Functions runtime storage and API activity reports. `MOCK_STORAGE_CONNECTION_STRING` can point the report history at a separate Storage account; if omitted, reports use `AzureWebJobsStorage`.

## Queue behavior

- `GET /claims/pending?maxRecords=100` executes `dbo.ClaimPendingMockClaims`. The stored procedure atomically changes returned rows from `QUEUED` to `PROCESSING`; it returns the complete JSON payload saved in `PayloadJson`.
- `POST /claims` inserts one JSON claim as `QUEUED`. It preserves a supplied `CORRELATION_ID`; if omitted, SQL generates a GUID and stores it in both the indexed column and returned JSON payload. A unique database constraint prevents duplicate correlation IDs. The API does not generate correlation IDs.
- The batch seed script generates correlation IDs in SQL, so they are assigned and stored in the database before polling.
- `GET /claims/by-id/{uniqueId}` retrieves the saved claim JSON.
- `PUT /requests/{correlationId}/status` appends the complete PUT JSON to `dbo.MockClaimWritebacks` and marks a matching `PROCESSING` row `COMPLETED`. Unmatched writebacks are still recorded and return HTTP 404.
- `POST /claims/{uniqueId}/result` is a convenience endpoint that records a result by internal ID.
- `POST /admin/notifications` accepts the RTA notification contract, saves the complete JSON and searchable fields in `dbo.MockNotifications`, and returns HTTP 200 with a plain-text receipt. `REQUEST_ID`, `REQUEST_QUEUE_ID`, and `EXTERNAL_CORRELATION_ID` may be null; notification attributes are preserved as JSON.

The API treats queue `Status` as internal database state. It is separate from the status fields in each RTA JSON message.

## Generate fake claims with T-SQL

Run `database/003_seed_mock_claims.sql` against `project-pulse-db`. Change `@ClaimCount` to a value from 1 to 10,000, then execute it to insert synthetic `INITIAL_REQUEST` records in `QUEUED` state. Each record gets a distinct request ID, correlation ID, idempotency key, request queue ID, and fake ICNs. The database generates each correlation ID and enforces uniqueness. Polling the API moves them to `PROCESSING`.

## Function App settings

- `AZURE_SQL_CONNECTION_STRING`: required SQL connection string for `project-pulse-db`.
- `FAKE_CLAIMS_PER_POLL`: default maximum records when a poll omits `maxRecords`; the request query parameter can override it and the API caps it at 10,000.
- `FAKE_API_DELAY_MS`: optional simulated API latency.
- `FAKE_CLAIM_JSON`: optional default payload used when manually submitting an empty `POST /claims` request.
- `UNIQUE_ID_FIELD`: optional JSON property used as the internal row identifier when present; otherwise the API uses `CORRELATION_ID` or generates a separate internal row ID. This internal ID is distinct from `CORRELATION_ID`, which is generated and constrained by SQL when the payload omits it.
- `AzureWebJobsStorage`: required Functions host storage and, by default, API report storage.
- `MOCK_STORAGE_CONNECTION_STRING`: optional separate Azure Storage connection string for API reports.
- `MOCK_REPORTS_TABLE`: optional report table name; defaults to `PulseApiReports`.

## Landing page

The Function App includes a branded Project Pulse API landing page at `/` and `/pulse-api`. Its system status checks whether the Function API is reachable and runs a live `SELECT 1` probe against Azure SQL Database every five seconds. The `/health` endpoint returns HTTP 200 when SQL is connected and HTTP 503 when the SQL probe fails. The page also includes an endpoint selector for the mock API operations.

Set `PROJECT_PULSE_API_VERSION`, `PROJECT_PULSE_API_ENVIRONMENT`, and `AzureWebJobsDisableHomepage=true` in the Function App environment variables.
