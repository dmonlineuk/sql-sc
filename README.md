# sql-sc

A free, MIT-licensed replacement for the core of Redgate SQL Source Control. It has a CLI and a local web UI, works directly against a live SQL Server database and uses the Redgate working-folder layout.

**Status:** Phase 1 in progress. See [docs/phase0-findings.md](docs/phase0-findings.md).

## Build

Needs the .NET 10 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```sh
dotnet build
dotnet test tests/SqlSc.Core.Tests
dotnet test tests/SqlSc.IntegrationTests
```

### Stand-alone executable

To run sql-sc on a machine without the .NET SDK or the source (for example an airgapped server), publish a self-contained single file:

```sh
dotnet publish src/SqlSc.Cli -p:PublishProfile=win-x64     # artifacts/publish/win-x64/sql-sc.exe
dotnet publish src/SqlSc.Cli -p:PublishProfile=linux-x64   # artifacts/publish/linux-x64/sql-sc
```

Copy that one file across; nothing else is needed to run it. `THIRD-PARTY-NOTICES.md` is published next to it: it covers the Microsoft `master.dacpac` files built into sql-sc, which are not MIT-licensed. Either profile can be built on Windows or Linux. CI also uploads both as build artifacts (`sql-sc-win-x64`, `sql-sc-linux-x64`).

On its first run the executable unpacks itself to `%TEMP%\.net` (`$TMPDIR/.net` on Linux). If that folder isn't writable, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to one that is.

## Try it

```sh
# Load a working folder and report anything DacFx can't understand
dotnet run --project src/SqlSc.Cli -- load path/to/working-folder

# Link a working folder to its database (saved per user, never in the repo)
dotnet run --project src/SqlSc.Cli -- link path/to/working-folder \
  --connection "Server=myserver;Database=MyDb;Authentication=Active Directory Default" --mode shared

# Check the connection, permissions, default trace and folder; the output is safe to paste into an issue.
# It also counts what the database holds and how much of it Filter.scpf keeps; --no-extract skips the slow DacFx extract.
dotnet run --project src/SqlSc.Cli -- doctor path/to/working-folder

# Compare the database with its working folder
dotnet run --project src/SqlSc.Cli -- status path/to/working-folder

# Who changed what, from the default trace
dotnet run --project src/SqlSc.Cli -- changes

# Write changed objects from the database into the folder (deleting files of dropped objects)
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder Sales.Customer "[Sales].[GetCustomer]"
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder --all

# Export, then git commit exactly those files; anything else staged or changed is left alone
dotnet run --project src/SqlSc.Cli -- commit path/to/working-folder -m "Add Customer.Email" Sales.Customer
dotnet run --project src/SqlSc.Cli -- commit path/to/working-folder -m "My changes" --mine
```

`export` writes one file per object in Redgate's layout (`Tables\Sales.Customer.sql`, `Stored Procedures\...`) with the
object's keys, indexes, triggers, permissions and extended properties in the same file, UTF-8 and CRLF line endings.
Database scoped credentials aren't exported, since their secrets can't be read back. `--mine` uses the default trace.

Connection strings are plain `Microsoft.Data.SqlClient` ones, so SQL authentication, Windows authentication
(`Integrated Security=true`) and Entra ID (`Authentication=Active Directory Default` or `Active Directory Interactive`) all work.
Add `--json` to any command for machine-readable output.

The connection comes from `--connection`, then `SQLSC_CONNECTION`, then the link for the folder (or the closest linked
parent folder). Links are stored in `%APPDATA%\sql-sc\links.json` on Windows and `~/.config/sql-sc/links.json` on Linux
(`SQLSC_HOME` overrides the folder). `link` never saves a password: for SQL authentication set `SQLSC_PASSWORD`.

"Changed by" reads SQL Server's default trace and needs `ALTER TRACE` permission; without it, status still works.

## Working folder settings

`status` groups changes to keys, constraints, indexes, extended properties and permissions under the object whose
script contains them. Objects excluded by `Filter.scpf` (Redgate's filter format: `@SCHEMA`/`@NAME` with `=`, `<>`,
`LIKE`, `AND`, `OR`, `NOT`) are never reported.

An optional `sql-sc.json` in the working folder holds settings shared by the team. It never contains connection details.

```jsonc
{
  "filter": "Filter.scpf",          // default: Filter.scpf when present
  "compare": {
    "ignoreWhitespace": true,       // default true
    "ignoreComments": false,
    "ignoreColumnOrder": false,
    "ignorePermissions": false,
    "ignoreRoleMembership": false,
    "ignoreExtendedProperties": false
  }
}
```

## Layout

| Path | |
|---|---|
| `src/SqlSc.Core` | Working folder reader, settings and links, filters, DacFx model loading, comparison, default trace reader, doctor, export and commit |
| `src/SqlSc.Cli` | `sql-sc` command-line tool |
| `tests/SqlSc.Core.Tests` | Unit tests |
| `tests/SqlSc.IntegrationTests` | Tests against SQL Server 2022 in Docker |
| `tests/Fixtures/Demo` | Small Redgate-layout working folder used by the tests |
