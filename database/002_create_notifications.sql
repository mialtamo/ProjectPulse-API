/*
    Project Pulse API notification inbox.
    Run once against project-pulse-db after 001_create_claim_queue.sql.
    The API stores the original notification JSON and searchable fields here.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.MockNotifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MockNotifications
    (
        NotificationId          bigint IDENTITY(1,1) NOT NULL,
        ExternalCorrelationId   nvarchar(128) NULL,
        EmailSubject            nvarchar(500) NOT NULL,
        EmailBody               nvarchar(max) NOT NULL,
        RequestId               bigint NULL,
        RequestQueueId          bigint NULL,
        NotificationAttrsJson   nvarchar(max) NULL,
        RequestBodyJson         nvarchar(max) NOT NULL,
        ReceivedAtUtc           datetime2(3) NOT NULL
            CONSTRAINT DF_MockNotifications_ReceivedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_MockNotifications PRIMARY KEY CLUSTERED (NotificationId),
        CONSTRAINT CK_MockNotifications_AttrsJson
            CHECK (NotificationAttrsJson IS NULL OR ISJSON(NotificationAttrsJson) = 1),
        CONSTRAINT CK_MockNotifications_RequestBodyJson
            CHECK (ISJSON(RequestBodyJson) = 1)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.MockNotifications')
      AND name = N'IX_MockNotifications_ReceivedAtUtc'
)
BEGIN
    CREATE INDEX IX_MockNotifications_ReceivedAtUtc
        ON dbo.MockNotifications (ReceivedAtUtc DESC, NotificationId DESC)
        INCLUDE (RequestId, RequestQueueId, ExternalCorrelationId);
END;
GO
