CREATE SCHEMA app AUTHORIZATION dbo
GO
CREATE SCHEMA [odd]]name] AUTHORIZATION dbo
GO
CREATE ROLE app_role AUTHORIZATION dbo
GO
CREATE USER rich_user WITHOUT LOGIN WITH DEFAULT_SCHEMA = app
GO
CREATE USER no_login_user WITHOUT LOGIN
GO
ALTER ROLE app_role ADD MEMBER rich_user
GO
ALTER ROLE db_datareader ADD MEMBER no_login_user
GO
CREATE TYPE app.Code FROM varchar(20) NOT NULL
GO
CREATE TYPE app.IdList AS TABLE (Id int NOT NULL PRIMARY KEY, Note nvarchar(50) NULL, CHECK (Id > 0), INDEX IX_Note NONCLUSTERED (Note))
GO
CREATE SEQUENCE app.OrderNumbers AS bigint START WITH 1000 INCREMENT BY 5 MINVALUE 1 MAXVALUE 99999999 NO CYCLE CACHE 50
GO
CREATE SEQUENCE app.Small AS int START WITH 1 INCREMENT BY 1
GO
CREATE TABLE app.Customer
(
    CustomerId int NOT NULL IDENTITY(10, 2),
    Code app.Code NOT NULL,
    Name nvarchar(100) COLLATE Latin1_General_CI_AS NOT NULL,
    Email varchar(max) NULL,
    Balance decimal(18, 4) NOT NULL CONSTRAINT DF_Customer_Balance DEFAULT (0),
    Rate float NULL,
    SmallRate float(24) NULL,
    Created datetime2(3) NOT NULL DEFAULT (sysutcdatetime()),
    Stamp rowversion,
    RowId uniqueidentifier ROWGUIDCOL NOT NULL CONSTRAINT DF_Customer_RowId DEFAULT (newsequentialid()),
    Payload varbinary(16) NULL,
    Sparse1 int SPARSE NULL,
    Upper AS (UPPER(Name)),
    Total AS (Balance * 2) PERSISTED,
    Name2 sysname NULL,
    Doc xml NULL,
    Lat geography NULL,
    Node hierarchyid NULL,
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId DESC) WITH (FILLFACTOR = 90, PAD_INDEX = ON),
    CONSTRAINT UQ_Customer_Code UNIQUE NONCLUSTERED (Code),
    CONSTRAINT CK_Customer_Balance CHECK (Balance >= 0)
)
GO
ALTER TABLE app.Customer ADD CHECK (Rate < 100)
GO
CREATE TABLE app.[Order]
(
    OrderId bigint NOT NULL CONSTRAINT DF_Order_Id DEFAULT (NEXT VALUE FOR app.OrderNumbers),
    CustomerId int NOT NULL,
    Amount money NOT NULL,
    Placed date NOT NULL,
    Notes nvarchar(200) NULL,
    PRIMARY KEY NONCLUSTERED (OrderId)
) WITH (DATA_COMPRESSION = PAGE)
GO
ALTER TABLE app.[Order] SET (LOCK_ESCALATION = AUTO)
GO
ALTER TABLE app.[Order] WITH NOCHECK ADD CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES app.Customer (CustomerId) ON DELETE CASCADE
GO
ALTER TABLE app.[Order] NOCHECK CONSTRAINT FK_Order_Customer
GO
CREATE CLUSTERED INDEX CIX_Order_Placed ON app.[Order] (Placed, OrderId) WITH (DATA_COMPRESSION = ROW)
GO
CREATE NONCLUSTERED INDEX IX_Order_Customer ON app.[Order] (CustomerId) INCLUDE (Amount, Notes) WHERE Amount > 0 WITH (IGNORE_DUP_KEY = OFF, ALLOW_PAGE_LOCKS = OFF)
GO
CREATE UNIQUE NONCLUSTERED INDEX UX_Order_Notes ON app.[Order] (Notes DESC) WHERE Notes IS NOT NULL
GO
CREATE STATISTICS ST_Order_Amount ON app.[Order] (Amount, Placed) WITH NORECOMPUTE
GO
CREATE TABLE [odd]]name].[Weird Table] ([Key] int NOT NULL PRIMARY KEY, [Value]]x] nvarchar(10) NULL)
GO
CREATE TABLE dbo.Heap (A int NULL, B char(3) NULL, C nchar(4) NULL, D time(0) NULL, E datetimeoffset(7) NULL, F numeric(5, 0) NULL, G text NULL)
GO
CREATE TABLE dbo.Untracked (Id int NOT NULL PRIMARY KEY)
GO
CREATE TABLE app.Price
(
    PriceId int NOT NULL CONSTRAINT PK_Price PRIMARY KEY,
    Amount money NOT NULL,
    ValidFrom datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL CONSTRAINT DF_Price_ValidFrom DEFAULT (sysutcdatetime()),
    ValidTo datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PriceHistory, HISTORY_RETENTION_PERIOD = 6 MONTHS))
GO
CREATE TABLE app.Rate
(
    RateId int NOT NULL PRIMARY KEY,
    SysStart datetime2(0) GENERATED ALWAYS AS ROW START NOT NULL,
    SysEnd datetime2(0) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
) WITH (SYSTEM_VERSIONING = ON)
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE app.QuotedOff AS SELECT "literal" AS x
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS OFF
GO
CREATE VIEW app.AnsiOff AS SELECT CustomerId FROM app.Customer WHERE Email = NULL
GO
SET ANSI_NULLS ON
GO
CREATE VIEW app.CustomerOrders WITH SCHEMABINDING AS
SELECT c.CustomerId, COUNT_BIG(*) AS Orders FROM app.Customer AS c JOIN app.[Order] AS o ON o.CustomerId = c.CustomerId GROUP BY c.CustomerId
GO
CREATE UNIQUE CLUSTERED INDEX CIX_CustomerOrders ON app.CustomerOrders (CustomerId)
GO
CREATE FUNCTION app.Twice (@x int) RETURNS int AS BEGIN RETURN @x * 2 END
GO
CREATE FUNCTION app.Orders (@customer int) RETURNS TABLE AS RETURN SELECT OrderId FROM app.[Order] WHERE CustomerId = @customer
GO
CREATE FUNCTION app.Multi (@ids app.IdList READONLY) RETURNS @r TABLE (Id int) AS BEGIN INSERT @r SELECT Id FROM @ids RETURN END
GO
CREATE PROCEDURE app.UsesTypes @ids app.IdList READONLY, @code app.Code AS SELECT app.Twice(Id) FROM @ids
GO
CREATE PROCEDURE app.UsesUntracked AS SELECT Id FROM dbo.Untracked
GO
CREATE TRIGGER app.Customer_Audit ON app.Customer AFTER UPDATE AS SET NOCOUNT ON
GO
CREATE TRIGGER app.Customer_Off ON app.Customer AFTER INSERT AS SET NOCOUNT ON
GO
DISABLE TRIGGER app.Customer_Off ON app.Customer
GO
CREATE SYNONYM app.Cust FOR app.Customer
GO
CREATE SYNONYM app.Remote FOR OtherDb.dbo.Thing
GO
GRANT SELECT ON app.Customer TO app_role
GO
GRANT CREATE PROCEDURE TO rich_user
GO
GRANT ALTER ON SCHEMA::app TO rich_user
GO
EXECUTE AS USER = 'rich_user'
EXEC ('CREATE PROCEDURE NoSchema AS SELECT 1 AS One')
REVERT
GO
CREATE PROCEDURE app.OldName AS SELECT 2 AS Two
GO
CREATE VIEW app.ColumnInfo AS
SELECT cols.COLUMN_NAME, cols.ORDINAL_POSITION, c.column_id
FROM INFORMATION_SCHEMA.COLUMNS AS cols
JOIN sys.columns AS c ON c.name = cols.COLUMN_NAME
GO
DECLARE @view nvarchar(max) = N'CREATE VIEW app.SelfReference AS SELECT t.TABLE_NAME FROM ' + QUOTENAME(DB_NAME()) + N'.INFORMATION_SCHEMA.TABLES AS t'
EXEC (@view)
GO
EXEC sp_rename 'app.OldName', 'NewName'
GO
CREATE TABLE dbo.Moved (Id int NOT NULL)
GO
CREATE TRIGGER dbo.trMoved ON dbo.Moved AFTER INSERT AS SET NOCOUNT ON
GO
ALTER SCHEMA app TRANSFER dbo.Moved
GO
GRANT UPDATE ON app.Customer (Name) TO app_role
GO
DENY DELETE ON app.Customer TO rich_user
GO
GRANT EXECUTE ON SCHEMA::app TO app_role WITH GRANT OPTION
GO
GRANT EXECUTE ON TYPE::app.IdList TO app_role
GO
GRANT CREATE TABLE TO app_role
GO
GRANT IMPERSONATE ON USER::no_login_user TO rich_user
GO
GRANT SELECT ON app.CustomerOrders TO public
GO
EXEC sp_addextendedproperty N'MS_Description', N'Customers', 'SCHEMA', N'app', 'TABLE', N'Customer'
EXEC sp_addextendedproperty N'MS_Description', 'Varchar note', 'SCHEMA', N'app', 'TABLE', N'Customer', 'COLUMN', N'Name'
EXEC sp_addextendedproperty N'Version', 3, 'SCHEMA', N'app', 'TABLE', N'Customer', 'CONSTRAINT', N'PK_Customer'
EXEC sp_addextendedproperty N'MS_Description', N'Index', 'SCHEMA', N'app', 'TABLE', N'Order', 'INDEX', N'IX_Order_Customer'
EXEC sp_addextendedproperty N'MS_Description', N'Param', 'SCHEMA', N'app', 'PROCEDURE', N'UsesTypes', 'PARAMETER', N'@ids'
EXEC sp_addextendedproperty N'MS_Description', N'Schema', 'SCHEMA', N'app'
EXEC sp_addextendedproperty N'MS_Description', N'Db'
EXEC sp_addextendedproperty N'MS_Description', N'Type', 'SCHEMA', N'app', 'TYPE', N'Code'
EXEC sp_addextendedproperty N'MS_Description', N'Trig', 'SCHEMA', N'app', 'TABLE', N'Customer', 'TRIGGER', N'Customer_Audit'
EXEC sp_addextendedproperty N'MS_Description', N'View', 'SCHEMA', N'app', 'VIEW', N'CustomerOrders'
GO
CREATE FUNCTION app.CheckDigits (@No varchar(10)) RETURNS TABLE AS RETURN (
    WITH num AS (SELECT n.n FROM (VALUES (1),(2),(3)) n(n))
    SELECT COUNT(1) AS Digits FROM num WHERE SUBSTRING(@No, num.n, 1) LIKE '[0-9]'
)
GO
DECLARE @secret nvarchar(100) = CONVERT(nvarchar(36), NEWID()) + N'Aa1!'
EXEC (N'CREATE MASTER KEY ENCRYPTION BY PASSWORD = ''' + @secret + N'''')
EXEC (N'CREATE DATABASE SCOPED CREDENTIAL [Rich Credential] WITH IDENTITY = ''SHARED ACCESS SIGNATURE'', SECRET = ''' + @secret + N'''')
GO
CREATE EXTERNAL DATA SOURCE [Rich Blobs] WITH (TYPE = BLOB_STORAGE, LOCATION = 'https://example.blob.core.windows.net/rich', CREDENTIAL = [Rich Credential])
GO
