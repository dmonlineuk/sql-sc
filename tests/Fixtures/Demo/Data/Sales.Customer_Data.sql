SET IDENTITY_INSERT [Sales].[Customer] ON
INSERT INTO [Sales].[Customer] ([CustomerId], [Name], [IsActive]) VALUES (1, N'Contoso', 1)
INSERT INTO [Sales].[Customer] ([CustomerId], [Name], [IsActive]) VALUES (2, N'Fabrikam', 0)
SET IDENTITY_INSERT [Sales].[Customer] OFF
