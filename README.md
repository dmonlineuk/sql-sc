# sql-sc

A free, MIT-licensed replacement for the core of Redgate SQL Source Control. It has a CLI and a local web UI, works directly against a live SQL Server database and uses the Redgate working-folder layout.

**Status:** Phase 0 (proof of concept). See [docs/phase0-findings.md](docs/phase0-findings.md).

## Build

Needs the .NET 10 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```sh
dotnet build
dotnet test tests/SqlSc.Core.Tests
dotnet test tests/SqlSc.IntegrationTests
```

## Try it

```sh
# Load a working folder and report anything DacFx can't understand
dotnet run --project src/SqlSc.Cli -- load path/to/working-folder

# Compare a database with its working folder
export SQLSC_CONNECTION="Server=localhost;Database=MyDb;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project src/SqlSc.Cli -- status path/to/working-folder

# Who changed what, from the default trace
dotnet run --project src/SqlSc.Cli -- changes
```

Connection strings are plain `Microsoft.Data.SqlClient` ones, so SQL authentication, Windows authentication
(`Integrated Security=true`) and Entra ID (`Authentication=Active Directory Default` or `Active Directory Interactive`) all work.
Add `--json` to any command for machine-readable output.

"Changed by" reads SQL Server's default trace and needs `ALTER TRACE` permission; without it, status still works.

## Layout

| Path | |
|---|---|
| `src/SqlSc.Core` | Working folder reader, DacFx model loading, comparison, default trace reader |
| `src/SqlSc.Cli` | `sql-sc` command-line tool |
| `tests/SqlSc.Core.Tests` | Unit tests |
| `tests/SqlSc.IntegrationTests` | Tests against SQL Server 2022 in Docker |
| `tests/Fixtures/Demo` | Small Redgate-layout working folder used by the tests |
