# Project Pulse API

Mock claims API for the Project Pulse POC. This Function App mimics the future SQL-backed API that Project Pulse will poll.


## Landing page

The Function App includes a branded Project Pulse API landing page at `/` and `/pulse-api`. It includes a live API/Table Storage health panel that refreshes every five seconds and an endpoint selector for the mock API operations.

Set `PROJECT_PULSE_API_VERSION`, `PROJECT_PULSE_API_ENVIRONMENT`, and `AzureWebJobsDisableHomepage=true` in the Function App environment variables.

## Endpoints

- `GET /health` - API + Table Storage health
- `GET /claims/pending?maxRecords=100` - returns fake claims for the poller
- `GET /claims/{uniqueId}` - retrieves a previously generated/submitted claim
- `POST /claims` - manually submits a fake claim
- `POST /claims/{uniqueId}/result` - records a simulated adjudication result

## Important settings

`FAKE_CLAIM_JSON` is the JSON template returned to Project Pulse. Change it without rebuilding the Function App.

`FAKE_CLAIMS_PER_POLL` controls how many records are generated per poll. The `maxRecords` query parameter acts as a cap.

`FAKE_AUTO_GENERATE_ON_POLL=true` makes every poll generate new claims automatically. Set it to `false` if you want `/claims/pending` to return only claims previously submitted with `POST /claims`.

`FAKE_API_DELAY_MS` simulates latency.

`UNIQUE_ID_FIELD` controls the name of the claim identifier property. If the template does not contain a value for that field, the API generates one.

The API uses Azure Table Storage so generated claims can be retrieved again by ID. By default it uses the Function App's `AzureWebJobsStorage`. Set `MOCK_STORAGE_CONNECTION_STRING` only if you want a separate storage account.
