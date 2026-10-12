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

`FAKE_CLAIM_JSON` is treated as a reusable shape/default template. When `FAKE_AUTO_GENERATE_ON_POLL=true`, the API generates fresh identity values for each claim, including `REQUEST_ID`, `CORRELATION_ID`, `IDEMPOTENCY_KEY`, and the configured `UNIQUE_ID_FIELD`. It also varies obvious mock fields such as patient/sponsor ICNs and, when present, member ID, claim amount, procedure code, and service dates. Contract fields such as `REQUEST_TYPE` and `PROGRAM` remain whatever the template specifies.

`FAKE_CLAIMS_PER_POLL` controls how many records are generated per poll. The `maxRecords` query parameter acts as a cap.

`FAKE_AUTO_GENERATE_ON_POLL=true` makes every poll generate new claims automatically. Set it to `false` if you want `/claims/pending` to return only claims previously submitted with `POST /claims`.

`FAKE_API_DELAY_MS` simulates latency.

`UNIQUE_ID_FIELD` controls the internal mock claim identifier property. Auto-generated polls always replace that field with a new generated value, even if the template contains a fixed value. Manually submitted claims keep the normal submit behavior.

The API uses Azure Table Storage so generated claims can be retrieved again by ID. By default it uses the Function App's `AzureWebJobsStorage`. Set `MOCK_STORAGE_CONNECTION_STRING` only if you want a separate storage account.
