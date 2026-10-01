CREATE TABLE [Sales].[Customer]
(
[CustomerId] [int] NOT NULL IDENTITY(1, 1),
[Name] [nvarchar] (100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[IsActive] [bit] NOT NULL CONSTRAINT [DF_Customer_IsActive] DEFAULT ((1))
)
GO
ALTER TABLE [Sales].[Customer] ADD CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([CustomerId])
GO
EXEC sp_addextendedproperty N'MS_Description', N'People we sell to', 'SCHEMA', N'Sales', 'TABLE', N'Customer', NULL, NULL
GO
