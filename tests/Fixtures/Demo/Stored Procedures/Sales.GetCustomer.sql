SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [Sales].[GetCustomer]
    @CustomerId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, Name, IsActive FROM Sales.Customer WHERE CustomerId = @CustomerId;
END
GO
