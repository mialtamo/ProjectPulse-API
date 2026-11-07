/*
    Insert synthetic UPDATE_REQUEST items into dbo.MockClaimQueue.
    Run against the same project-pulse-db used by the Project Pulse API.
    The queue table already supports both request types; no additional columns are required.
    Set @RequestCount from 1 through 10,000 before running.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.MockClaimQueue', N'U') IS NULL
    THROW 50030, 'dbo.MockClaimQueue does not exist. Run 001_create_claim_queue.sql first.', 1;

DECLARE @RequestCount int = 1;

IF @RequestCount < 1 OR @RequestCount > 10000
    THROW 50031, 'RequestCount must be between 1 and 10000.', 1;

DECLARE @Inserted TABLE (CorrelationId nvarchar(36) NOT NULL PRIMARY KEY);

DECLARE @Seed TABLE
(
    N               int NOT NULL PRIMARY KEY,
    RequestId       bigint NOT NULL,
    RequestQueueId  bigint NOT NULL,
    CorrelationId   nvarchar(36) NOT NULL UNIQUE,
    IdempotencyKey  nvarchar(36) NOT NULL UNIQUE,
    PatientIcn      nvarchar(24) NOT NULL,
    SponsorIcn      nvarchar(24) NOT NULL,
    ClaimNumber     nvarchar(32) NOT NULL,
    StartDate       date NOT NULL,
    EndDate         date NOT NULL
);

;WITH Numbers AS
(
    SELECT TOP (@RequestCount)
        CONVERT(int, ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id)) AS N
    FROM sys.all_objects AS a
    CROSS JOIN sys.all_objects AS b
)
INSERT @Seed
(
    N, RequestId, RequestQueueId, CorrelationId, IdempotencyKey,
    PatientIcn, SponsorIcn, ClaimNumber, StartDate, EndDate
)
SELECT
    n.N,
    800000000000 + n.N,
    600000 + n.N,
    CONVERT(nvarchar(36), NEWID()),
    CONVERT(nvarchar(36), NEWID()),
    N'1099800000V' + RIGHT(N'000000' + CONVERT(nvarchar(6), n.N), 6),
    N'1099800000V' + RIGHT(N'500000' + CONVERT(nvarchar(6), n.N), 6),
    CONCAT(N'UPDATE-TEST-', RIGHT(N'000000' + CONVERT(nvarchar(6), n.N), 6)),
    DATEADD(DAY, -(n.N % 90), CONVERT(date, SYSUTCDATETIME())),
    DATEADD(DAY, -(n.N % 90) + 30, CONVERT(date, SYSUTCDATETIME()))
FROM Numbers AS n;

INSERT dbo.MockClaimQueue
(
    UniqueId, RtaRequestId, CorrelationId, IdempotencyKey,
    RequestType, Program, PayloadJson
)
OUTPUT INSERTED.CorrelationId INTO @Inserted (CorrelationId)
SELECT
    CONCAT(N'UPDATE-', REPLACE(s.CorrelationId, N'-', N'')),
    CONVERT(nvarchar(100), s.RequestId),
    s.CorrelationId,
    s.IdempotencyKey,
    N'UPDATE_REQUEST',
    N'CHAMPVA',
    payload.PayloadJson
FROM @Seed AS s
CROSS APPLY
(
    SELECT
    (
        SELECT
            s.RequestId AS [REQUEST_ID],
            s.CorrelationId AS [CORRELATION_ID],
            s.IdempotencyKey AS [IDEMPOTENCY_KEY],
            CAST(NULL AS nvarchar(36)) AS [EXTERNAL_CORRELATION_ID],
            1 AS [INTERFACE_ID],
            s.RequestQueueId AS [REQUEST_QUEUE_ID],
            N'PROCESSING' AS [REQUEST_QUEUE_STATUS_TYPE],
            10 AS [REQUEST_PRIORITY],
            CAST(NULL AS bigint) AS [REQUEST_PROCESS_ENGINE_ID],
            N'PROCESSING' AS [REQUEST_STATUS_TYPE],
            CAST(NULL AS nvarchar(50)) AS [REQUEST_SUB_STATUS_TYPE],
            N'UPDATE_REQUEST' AS [REQUEST_TYPE],
            CAST(NULL AS nvarchar(max)) AS [INITIAL_REQUEST_METADATA],
            JSON_QUERY
            (
                (
                    SELECT
                        s.PatientIcn AS [PATIENT_ICN],
                        s.SponsorIcn AS [SPONSOR_ICN],
                        CONVERT(char(10), s.StartDate, 23) AS [START_DATE_OF_SERVICE],
                        CONVERT(char(10), s.EndDate, 23) AS [END_DATE_OF_SERVICE],
                        N'CHAMPVA' AS [PROGRAM],
                        s.ClaimNumber AS [CLAIM_NUMBER],
                        N'CLAIMS' AS [SOURCE],
                        N'Synthetic update request for DEV flow validation' AS [NOTES],
                        CONVERT(decimal(10, 2), 25.00) AS [ADJ_IND_DEDUCTIBLE],
                        CONVERT(decimal(10, 2), 10.00) AS [ADJ_FAM_DEDUCTIBLE],
                        CONVERT(decimal(10, 2), 15.00) AS [ADJ_OUT_OF_POCKET]
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                )
            ) AS [UPDATE_REQUEST_METADATA],
            0 AS [ID]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
    ) AS PayloadJson
) AS payload;

SELECT
    q.RequestType,
    q.Status,
    COUNT_BIG(*) AS InsertedRequests
FROM dbo.MockClaimQueue AS q
JOIN @Inserted AS i
  ON i.CorrelationId = q.CorrelationId
GROUP BY q.RequestType, q.Status;
