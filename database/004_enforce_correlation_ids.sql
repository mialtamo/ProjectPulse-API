/*
    Backfill and enforce unique RTA correlation IDs for an existing claim queue.
    Run once against project-pulse-db after 001_create_claim_queue.sql.
    Existing IDs are retained. Missing IDs are generated in SQL and written into PayloadJson.
    Duplicate existing IDs are reported and must be resolved before this migration can finish.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.MockClaimQueue', N'U') IS NULL
    THROW 50020, 'dbo.MockClaimQueue does not exist. Run 001_create_claim_queue.sql first.', 1;
GO

BEGIN TRANSACTION;

DECLARE @MissingIds TABLE
(
    ClaimId bigint NOT NULL PRIMARY KEY,
    CorrelationId nvarchar(36) NOT NULL UNIQUE
);

INSERT @MissingIds (ClaimId, CorrelationId)
SELECT
    ClaimId,
    COALESCE(
        NULLIF(JSON_VALUE(PayloadJson, '$.CORRELATION_ID'), N''),
        CONVERT(nvarchar(36), NEWID()))
FROM dbo.MockClaimQueue
WHERE CorrelationId IS NULL;

UPDATE q
   SET CorrelationId = m.CorrelationId,
       PayloadJson = JSON_MODIFY(q.PayloadJson, '$.CORRELATION_ID', m.CorrelationId),
       UpdatedAtUtc = SYSUTCDATETIME()
FROM dbo.MockClaimQueue AS q
JOIN @MissingIds AS m
  ON m.ClaimId = q.ClaimId;

IF EXISTS
(
    SELECT CorrelationId
    FROM dbo.MockClaimQueue
    GROUP BY CorrelationId
    HAVING COUNT_BIG(*) > 1
)
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50021, 'Duplicate CorrelationId values exist. Resolve duplicates, then rerun this script.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.MockClaimQueue')
      AND name = N'DF_MockClaimQueue_CorrelationId'
)
BEGIN
    ALTER TABLE dbo.MockClaimQueue
        ADD CONSTRAINT DF_MockClaimQueue_CorrelationId
        DEFAULT (CONVERT(nvarchar(36), NEWID())) FOR CorrelationId;
END;

ALTER TABLE dbo.MockClaimQueue
    ALTER COLUMN CorrelationId nvarchar(128) NOT NULL;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
      AND name = N'UQ_MockClaimQueue_CorrelationId'
)
BEGIN
    CREATE UNIQUE INDEX UQ_MockClaimQueue_CorrelationId
        ON dbo.MockClaimQueue (CorrelationId);
END;

COMMIT TRANSACTION;
GO
