# Phase 0 findings

Phase 0 checked whether DacFx can do the heavy lifting for a Redgate-style, database-first workflow.
Short answer: yes, with one design change (borrowing missing references from the live database).

Tested with DacFx 170.5.96 on .NET 10, against SQL Server 2022 (16.0, Linux container), using:

- `tests/Fixtures/Demo`: a small Redgate-layout folder written for the tests.
- The redacted `SDEDLZMedalDb_rg` working folder supplied by Dan (not committed).

## 1. Loading a Redgate working folder

`FolderModelLoader` reads every `.sql` file outside `Data/`, `Migrations/` and `Custom Scripts/`, splits it into
GO batches with ScriptDom, and adds each batch to an in-memory `TSqlModel` with `AddOrUpdateObjects`.
`SET QUOTED_IDENTIFIER` / `SET ANSI_NULLS` batches are applied to the batches that follow them.
Each batch is tagged `file|line`, so every model object can be traced back to its file.

Results on the supplied sample (27 schema files, Azure SQL Database platform from `RedGateDatabaseInfo.xml`):

| | |
|---|---|
| Parse errors | 0 |
| Load errors | 0 |
| Objects loaded | 40: 7 external data sources, 2 external tables, 2 tables, 2 views, 2 procedures, 2 TVFs, 3 table types, 2 users, 1 role, 1 schema, 9 role memberships, 5 permissions, 1 extended property |
| Validation | 960 messages, all "unresolved reference" (SQL71501 / SQL71502 / SQL71562) |
| Load time | about 2.3 s cold |

Everything Redgate wrote (CRLF, `GO`, `sp_addextendedproperty`, `ALTER ROLE ... ADD MEMBER`, `DENY ... ON SCHEMA::`,
external tables / data sources) loads. The validation messages are expected: the sample is reduced, so schemas,
tables, logins and the database-scoped credential it refers to are not in it.

Also found: `RedGateDatabaseInfo.xml` declares `encoding="utf-16"` but the sample is UTF-8 with a BOM, so it must be
read through a BOM-detecting reader.

## 2. Comparing the folder with a live database

DacFx refuses to build a .dacpac while a model has unresolved references (`IgnoreValidationErrors` does not help;
these are build-blocking). Real working folders will nearly always have some: logins, credentials, objects excluded
by `Filter.scpf`, cross-database references, and objects nobody has committed yet.

Fix used in `StatusService`:

1. Load the folder model (unresolved references become warnings).
2. Extract the database model (`TSqlModel.LoadFromDatabase`, script-backed, including referenced logins).
3. Copy every object the folder lacks from the database model into the folder model, tagged `db:`.
   The copied application objects are exactly the "New in database" list.
4. Build a .dacpac from the merged model and run `SchemaComparison` (database → dacpac).
5. Drop differences on copied objects, server-level objects, files, the auto-created route and database options.

Tested end to end (integration tests, Testcontainers SQL Server 2022):

- Database deployed from the folder: no differences.
- `ALTER PROCEDURE`: Modified, mapped to `Stored Procedures/Sales.GetCustomer.sql`.
- `CREATE TABLE`: New (plus its primary key and extended property as separate rows, see below).
- `DROP VIEW`: Deleted.

Timings on the demo database: database extract 0.5 s (4.4 s cold), compare 2.7 s (8 s cold), everything else
under 1 s. `SchemaCompareDatabaseEndpoint` extracts the database a second time; comparing two .dacpacs would halve
that, worth doing in Phase 1.

Not usable: `SchemaCompareProjectEndpoint` needs an .sqlproj; no public in-memory-model endpoint exists.

## 3. "Changed by" from the default trace

`DefaultTraceReader` reads `sys.fn_trace_gettable` from `log.trc` in the default trace folder (all rollover files),
filtered to Object:Created (46), Object:Deleted (47) and Object:Altered (164) for the current database, ignoring
auto-created statistics. The latest event per object is attached to each status row.

Works on SQL Server 2022: login, host, application and time are reported for creates, alters and drops.

Limitations found:

- The trace records `ObjectID` and `ObjectName` but not the schema. The schema is resolved now, from the object id,
  so dropped objects are matched by name only.
- Needs `ALTER TRACE` (`VIEW SERVER STATE` on 2022+ also works); without it status still works and shows Unknown.
- The trace keeps 5 x 20 MB files and rolls over; busy servers may lose history in hours.
- Not yet tested on Azure SQL Managed Instance.

## 4. Platform and authentication

- Platform is detected from `SERVERPROPERTY('EngineEdition')` / `ProductVersion`; Managed Instance maps to Sql160.
- All connection handling is plain `Microsoft.Data.SqlClient`, so SQL auth, `Integrated Security=true` (Windows)
  and `Authentication=Active Directory Default|Interactive` (Entra ID) all work through the connection string.
  Only SQL auth was exercised here.

## Decisions for Phase 1

- Keep DacFx + "borrow from database" as the comparison engine.
- Group child objects (keys, constraints, indexes, extended properties, permissions) under their owning object, as
  Redgate does.
- Apply `Filter.scpf` (or our own filter file) before borrowing, so filtered objects are not reported as New.
- Compare dacpac-to-dacpac to avoid the second extraction.
- Static data needs its own row-level diff; DacFx does not cover it.
- Writer for our own folder layout, starting with Redgate's folder names.
