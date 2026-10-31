/*
    Insert a batch of synthetic INITIAL_REQUEST items into dbo.MockClaimQueue.
    Run against project-pulse-db. Change @ClaimCount, then execute it to seed a batch.
    The API's GET /claims/pending route atomically moves these rows QUEUED -> PROCESSING.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ClaimCount int = 100;

IF @ClaimCount < 1 OR @ClaimCount > 10000
    THROW 50010, 'ClaimCount must be between 1 and 10000.', 1;

DECLARE @Seed TABLE
(
    N               int NOT NULL PRIMARY KEY,
    RequestId       bigint NOT NULL,
    RequestQueueId  bigint NOT NULL,
    CorrelationId   nvarchar(36) NOT NULL,
    PatientIcn      nvarchar(24) NOT NULL,
    SponsorIcn      nvarchar(24) NOT NULL,
    StartDate       date NOT NULL,
    EndDate         date NOT NULL
);

DECLARE @Inserted TABLE (ClaimId bigint NOT NULL);

;WITH Numbers AS
(
    SELECT TOP (@ClaimCount)
        CONVERT(int, ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id)) AS N
    FROM sys.all_objects AS a
    CROSS JOIN sys.all_objects AS b
)
INSERT @Seed
(
    N, RequestId, RequestQueueId, CorrelationId,
    PatientIcn, SponsorIcn, StartDate, EndDate
)
SELECT
    N,
    700000000000 + N,
    500000 + N,
    CONVERT(nvarchar(36), NEWID()),
    N'1099800000V' + RIGHT(N'000000' + CONVERT(nvarchar(6), N), 6),
    N'1099800000V' + RIGHT(N'500000' + CONVERT(nvarchar(6), N), 6),
    DATEADD(DAY, -(N % 365), CONVERT(date, SYSUTCDATETIME())),
    DATEADD(DAY, -(N % 365) + (N % 3), CONVERT(date, SYSUTCDATETIME()))
FROM Numbers;

INSERT dbo.MockClaimQueue
(
    UniqueId, RtaRequestId, CorrelationId, IdempotencyKey,
    RequestType, Program, PayloadJson
)
OUTPUT INSERTED.ClaimId INTO @Inserted (ClaimId)
SELECT
    CONCAT(N'CLAIM-', REPLACE(s.CorrelationId, N'-', N'')),
    CONVERT(nvarchar(100), s.RequestId),
    s.CorrelationId,
    s.CorrelationId,
    N'INITIAL_REQUEST',
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
            s.CorrelationId AS [IDEMPOTENCY_KEY],
            CAST(NULL AS nvarchar(36)) AS [EXTERNAL_CORRELATION_ID],
            1 AS [INTERFACE_ID],
            s.RequestQueueId AS [REQUEST_QUEUE_ID],
            N'PROCESSING' AS [REQUEST_QUEUE_STATUS_TYPE],
            5 AS [REQUEST_PRIORITY],
            CAST(NULL AS bigint) AS [REQUEST_PROCESS_ENGINE_ID],
            N'INPROGRESS' AS [REQUEST_STATUS_TYPE],
            CAST(NULL AS nvarchar(50)) AS [REQUEST_SUB_STATUS_TYPE],
            N'INITIAL_REQUEST' AS [REQUEST_TYPE],
            JSON_QUERY
            (
                (
                    SELECT
                        s.PatientIcn AS [PATIENT_ICN],
                        s.SponsorIcn AS [SPONSOR_ICN],
                        CONVERT(char(10), s.StartDate, 23) AS [START_DATE_OF_SERVICE],
                        CONVERT(char(10), s.EndDate, 23) AS [END_DATE_OF_SERVICE],
                        N'CHAMPVA' AS [PROGRAM],
                        CAST(0 AS bit) AS [INCLUDE_ADJUSTMENT_DETAILS]
                    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
                )
            ) AS [INITIAL_REQUEST_METADATA],
            CAST(NULL AS nvarchar(1)) AS [UPDATE_REQUEST_METADATA]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
    ) AS PayloadJson
) AS payload;

SELECT COUNT_BIG(*) AS InsertedClaims
FROM @Inserted;
