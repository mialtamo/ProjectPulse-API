/*
    Project Pulse API: SQL-backed mock claim queue.

    Run this script once against the Azure SQL database that will back the API.
    It creates:
      - dbo.MockClaimQueue: full fake RTA request JSON and queue lifecycle.
      - dbo.MockClaimWritebacks: append-only log of every status PUT body.
      - dbo.ClaimPendingMockClaims: atomically changes QUEUED rows to PROCESSING and returns them.
      - dbo.RecordMockClaimWriteback: logs each PUT and completes a matched PROCESSING row.

    Status lifecycle in dbo.MockClaimQueue:
      QUEUED -> PROCESSING -> COMPLETED

    The RTA-shaped payload remains in PayloadJson. Status is an internal queue state;
    it is distinct from fields such as REQUEST_QUEUE_STATUS_TYPE in that JSON.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.MockClaimQueue', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MockClaimQueue
    (
        ClaimId             bigint IDENTITY(1,1) NOT NULL,
        UniqueId            nvarchar(128) NOT NULL,
        RtaRequestId         nvarchar(100) NULL,
        CorrelationId        nvarchar(128) NOT NULL
            CONSTRAINT DF_MockClaimQueue_CorrelationId DEFAULT (CONVERT(nvarchar(36), NEWID())),
        IdempotencyKey       nvarchar(128) NULL,
        RequestType          nvarchar(50) NULL,
        Program              nvarchar(80) NULL,
        Status               nvarchar(20) NOT NULL
            CONSTRAINT DF_MockClaimQueue_Status DEFAULT (N'QUEUED'),
        PayloadJson          nvarchar(max) NOT NULL,
        CreatedAtUtc         datetime2(3) NOT NULL
            CONSTRAINT DF_MockClaimQueue_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ProcessingAtUtc      datetime2(3) NULL,
        CompletedAtUtc       datetime2(3) NULL,
        UpdatedAtUtc         datetime2(3) NOT NULL
            CONSTRAINT DF_MockClaimQueue_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_MockClaimQueue PRIMARY KEY CLUSTERED (ClaimId),
        CONSTRAINT UQ_MockClaimQueue_UniqueId UNIQUE (UniqueId),
        CONSTRAINT UQ_MockClaimQueue_CorrelationId UNIQUE (CorrelationId),
        CONSTRAINT CK_MockClaimQueue_Status
            CHECK (Status IN (N'QUEUED', N'PROCESSING', N'COMPLETED')),
        CONSTRAINT CK_MockClaimQueue_PayloadJson
            CHECK (ISJSON(PayloadJson) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
      AND name = N'IX_MockClaimQueue_Status_CreatedAtUtc'
)
BEGIN
    CREATE INDEX IX_MockClaimQueue_Status_CreatedAtUtc
        ON dbo.MockClaimQueue (Status, CreatedAtUtc, ClaimId)
        INCLUDE (UniqueId, RtaRequestId, CorrelationId, IdempotencyKey, RequestType, Program);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MockClaimQueue')
      AND name = N'IX_MockClaimQueue_CorrelationId_Status'
)
BEGIN
    CREATE INDEX IX_MockClaimQueue_CorrelationId_Status
        ON dbo.MockClaimQueue (CorrelationId, Status, ProcessingAtUtc DESC)
        INCLUDE (ClaimId, RtaRequestId, UniqueId);
END;
GO

IF OBJECT_ID(N'dbo.MockClaimWritebacks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MockClaimWritebacks
    (
        WritebackId         bigint IDENTITY(1,1) NOT NULL,
        ClaimId             bigint NULL,
        CorrelationId       nvarchar(128) NOT NULL,
        ResultStatus        nvarchar(32) NULL,
        RequestBodyJson     nvarchar(max) NOT NULL,
        ReceivedAtUtc       datetime2(3) NOT NULL
            CONSTRAINT DF_MockClaimWritebacks_ReceivedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_MockClaimWritebacks PRIMARY KEY CLUSTERED (WritebackId),
        CONSTRAINT FK_MockClaimWritebacks_MockClaimQueue
            FOREIGN KEY (ClaimId) REFERENCES dbo.MockClaimQueue (ClaimId),
        CONSTRAINT CK_MockClaimWritebacks_RequestBodyJson
            CHECK (ISJSON(RequestBodyJson) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MockClaimWritebacks')
      AND name = N'IX_MockClaimWritebacks_CorrelationId_ReceivedAtUtc'
)
BEGIN
    CREATE INDEX IX_MockClaimWritebacks_CorrelationId_ReceivedAtUtc
        ON dbo.MockClaimWritebacks (CorrelationId, ReceivedAtUtc DESC)
        INCLUDE (ClaimId, ResultStatus);
END;
GO

CREATE OR ALTER PROCEDURE dbo.ClaimPendingMockClaims
    @MaxRecords int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @MaxRecords IS NULL OR @MaxRecords < 1
        THROW 50001, 'MaxRecords must be greater than zero.', 1;

    IF @MaxRecords > 10000
        SET @MaxRecords = 10000;

    DECLARE @Claimed TABLE
    (
        ClaimId         bigint NOT NULL,
        UniqueId        nvarchar(128) NOT NULL,
        RtaRequestId     nvarchar(100) NULL,
        CorrelationId    nvarchar(128) NULL,
        IdempotencyKey   nvarchar(128) NULL,
        RequestType      nvarchar(50) NULL,
        Program          nvarchar(80) NULL,
        PayloadJson      nvarchar(max) NOT NULL,
        CreatedAtUtc     datetime2(3) NOT NULL,
        ProcessingAtUtc  datetime2(3) NOT NULL
    );

    BEGIN TRANSACTION;

    ;WITH NextClaims AS
    (
        SELECT TOP (@MaxRecords) *
        FROM dbo.MockClaimQueue WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Status = N'QUEUED'
        ORDER BY CreatedAtUtc, ClaimId
    )
    UPDATE NextClaims
       SET Status = N'PROCESSING',
           ProcessingAtUtc = SYSUTCDATETIME(),
           UpdatedAtUtc = SYSUTCDATETIME()
    OUTPUT
        inserted.ClaimId,
        inserted.UniqueId,
        inserted.RtaRequestId,
        inserted.CorrelationId,
        inserted.IdempotencyKey,
        inserted.RequestType,
        inserted.Program,
        inserted.PayloadJson,
        inserted.CreatedAtUtc,
        inserted.ProcessingAtUtc
    INTO @Claimed;

    COMMIT TRANSACTION;

    SELECT
        ClaimId,
        UniqueId,
        RtaRequestId,
        CorrelationId,
        IdempotencyKey,
        RequestType,
        Program,
        PayloadJson,
        CreatedAtUtc,
        ProcessingAtUtc
    FROM @Claimed
    ORDER BY CreatedAtUtc, ClaimId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordMockClaimWriteback
    @CorrelationId nvarchar(128),
    @RequestBodyJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NULLIF(LTRIM(RTRIM(@CorrelationId)), N'') IS NULL
        THROW 50002, 'CorrelationId is required.', 1;

    IF ISJSON(@RequestBodyJson) <> 1
        THROW 50003, 'RequestBodyJson must contain valid JSON.', 1;

    DECLARE @ClaimId bigint = NULL;
    DECLARE @ReceivedAtUtc datetime2(3) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    SELECT TOP (1)
        @ClaimId = ClaimId
    FROM dbo.MockClaimQueue WITH (UPDLOCK, ROWLOCK)
    WHERE CorrelationId = @CorrelationId
      AND Status = N'PROCESSING'
    ORDER BY ProcessingAtUtc DESC, ClaimId DESC;

    INSERT dbo.MockClaimWritebacks
    (
        ClaimId,
        CorrelationId,
        ResultStatus,
        RequestBodyJson,
        ReceivedAtUtc
    )
    VALUES
    (
        @ClaimId,
        @CorrelationId,
        JSON_VALUE(@RequestBodyJson, '$.STATUS'),
        @RequestBodyJson,
        @ReceivedAtUtc
    );

    IF @ClaimId IS NOT NULL
    BEGIN
        UPDATE dbo.MockClaimQueue
           SET Status = N'COMPLETED',
               CompletedAtUtc = @ReceivedAtUtc,
               UpdatedAtUtc = @ReceivedAtUtc
         WHERE ClaimId = @ClaimId;
    END;

    COMMIT TRANSACTION;

    SELECT
        CAST(CASE WHEN @ClaimId IS NULL THEN 0 ELSE 1 END AS bit) AS Matched,
        @ClaimId AS ClaimId,
        @ReceivedAtUtc AS ReceivedAtUtc;
END;
GO
