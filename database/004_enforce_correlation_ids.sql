/*
    Backfill and enforce unique RTA correlation IDs for an existing claim queue.
    Run against project-pulse-db after 001_create_claim_queue.sql.
    Existing IDs are retained. Missing IDs are generated in SQL and written into PayloadJson.
    Duplicate IDs are reported and cause the entire migration to roll back.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.MockClaimQueue', N'U') IS NULL
    THROW 50020, 'dbo.MockClaimQueue does not exist. Run 001_create_claim_queue.sql first.', 1;
GO

BEGIN TRY
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
        THROW 50021, 'Duplicate CorrelationId values exist. Resolve duplicates, then rerun this script.', 1;

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

    IF COLUMNPROPERTY(OBJECT_ID(N'dbo.MockClaimQueue'), N'CorrelationId', 'AllowsNull') = 1
    BEGIN
        /*
            SQL Server cannot ALTER COLUMN while these indexes depend on CorrelationId.
            Remove them for the schema change; they are recreated below.
        */
        IF EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
              AND name = N'IX_MockClaimQueue_Status_CreatedAtUtc'
        )
            DROP INDEX IX_MockClaimQueue_Status_CreatedAtUtc ON dbo.MockClaimQueue;

        IF EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
              AND name = N'IX_MockClaimQueue_CorrelationId_Status'
        )
            DROP INDEX IX_MockClaimQueue_CorrelationId_Status ON dbo.MockClaimQueue;

        IF EXISTS
        (
            SELECT 1
            FROM sys.key_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.MockClaimQueue')
              AND name = N'UQ_MockClaimQueue_CorrelationId'
        )
            ALTER TABLE dbo.MockClaimQueue
                DROP CONSTRAINT UQ_MockClaimQueue_CorrelationId;
        ELSE IF EXISTS
        (
            SELECT 1 FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
              AND name = N'UQ_MockClaimQueue_CorrelationId'
        )
            DROP INDEX UQ_MockClaimQueue_CorrelationId ON dbo.MockClaimQueue;

        ALTER TABLE dbo.MockClaimQueue
            ALTER COLUMN CorrelationId nvarchar(128) NOT NULL;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
          AND name = N'IX_MockClaimQueue_Status_CreatedAtUtc'
    )
    BEGIN
        CREATE INDEX IX_MockClaimQueue_Status_CreatedAtUtc
            ON dbo.MockClaimQueue (Status, CreatedAtUtc, ClaimId)
            INCLUDE (UniqueId, RtaRequestId, CorrelationId, IdempotencyKey, RequestType, Program);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
          AND name = N'IX_MockClaimQueue_CorrelationId_Status'
    )
    BEGIN
        CREATE INDEX IX_MockClaimQueue_CorrelationId_Status
            ON dbo.MockClaimQueue (CorrelationId, Status, ProcessingAtUtc DESC)
            INCLUDE (ClaimId, RtaRequestId, UniqueId);
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
          AND name = N'UQ_MockClaimQueue_CorrelationId'
    )
    BEGIN
        CREATE UNIQUE INDEX UQ_MockClaimQueue_CorrelationId
            ON dbo.MockClaimQueue (CorrelationId);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
