SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [Sales].[ActiveCustomer]
AS
SELECT c.CustomerId, c.Name
FROM Sales.Customer AS c
WHERE c.IsActive = 1
GO
GRANT SELECT ON  [Sales].[ActiveCustomer] TO [app_reader]
GO
