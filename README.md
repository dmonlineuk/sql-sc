# sql-sc

A free, MIT-licensed replacement for the core of Redgate SQL Source Control. It has a CLI and a local web UI, works directly against a live SQL Server database and uses the Redgate working-folder layout.

**Status:** Phase 1 in progress. See [docs/phase0-findings.md](docs/phase0-findings.md).

## Getting started

1. **Install sql-sc**, in one of these ways:
   - **Stand-alone executable** (no .NET needed): download the `sql-sc-win-x64` or `sql-sc-linux-x64` artifact from a
     [CI run](https://github.com/dmonlineuk/sql-sc/actions/workflows/ci.yml) on `main`, or publish it yourself (see below).
   - **.NET tool** (needs the .NET 10 runtime): download the `sql-sc-dotnet-tool` artifact, unzip it, then run
     `dotnet tool install --global sql-sc --add-source <folder containing sql-sc.*.nupkg>`.
2. **Link** your working folder to its database. This is saved per user, never in the repo, and never saves a password:
   ```sh
   sql-sc link path/to/working-folder --connection "Server=myserver;Database=MyDb;Authentication=Active Directory Interactive" --mode shared
   ```
3. **Check** the connection, permissions and folder: `sql-sc doctor path/to/working-folder`
4. **See what differs**, and on which side: `sql-sc status path/to/working-folder --diff`
5. **Commit** your database changes to the folder: preview with `sql-sc export path/to/working-folder --all --dry-run`,
   then `sql-sc commit path/to/working-folder -m "Add Customer.Email" Sales.Customer`.
6. **Get latest** from the folder into the database: preview with `sql-sc apply path/to/working-folder --all --script-only`,
   then run it without `--script-only`. On a shared database sql-sc asks before each change, since it changes the
   database for everyone.

Or run `sql-sc ui [path/to/working-folder]` for the local web UI. It opens your browser on a page where you can link a
folder and test the connection (Setup), and see each change with a side-by-side diff, then commit the ones you tick and
optionally push (Commit). It listens on `127.0.0.1` only, and the address it prints includes a one-off token, so other
users and websites can't use it. Press Ctrl+C to stop it.

Add `--data` to `export`, `commit` and `apply` to include static data (see `sql-sc data link`). `sql-sc <command> --help`
lists every option.

## Build

Needs the .NET 10 SDK. Integration tests also need Docker (they start SQL Server 2022 with Testcontainers).

```sh
dotnet build
dotnet test tests/SqlSc.Core.Tests
dotnet test tests/SqlSc.Web.Tests
dotnet test tests/SqlSc.IntegrationTests
```

### Stand-alone executable

To run sql-sc on a machine without the .NET SDK or the source (for example an airgapped server), publish a self-contained single file:

```sh
dotnet publish src/SqlSc.Cli -p:PublishProfile=win-x64     # artifacts/publish/win-x64/sql-sc.exe
dotnet publish src/SqlSc.Cli -p:PublishProfile=linux-x64   # artifacts/publish/linux-x64/sql-sc
```

Copy that one file across; nothing else is needed to run it. `THIRD-PARTY-NOTICES.md` is published next to it: it covers the Microsoft `master.dacpac` files built into sql-sc, which are not MIT-licensed. Either profile can be built on Windows or Linux. CI also uploads both as build artifacts (`sql-sc-win-x64`, `sql-sc-linux-x64`).

`dotnet pack src/SqlSc.Cli -o artifacts/package` builds the .NET tool package, which CI uploads as `sql-sc-dotnet-tool`.

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

# Who changed what, from the change log if it's installed, otherwise the default trace
dotnet run --project src/SqlSc.Cli -- changes

# Record every object change in the database (needs db_owner, once); --script-only prints the SQL instead
dotnet run --project src/SqlSc.Cli -- changelog install path/to/working-folder
dotnet run --project src/SqlSc.Cli -- changelog show path/to/working-folder

# Write changed objects from the database into the folder (deleting files of dropped objects)
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder Sales.Customer "[Sales].[GetCustomer]"
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder --all

# Preview: the unified diff of each file export would write or delete, changing nothing
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder --all --dry-run

# Export, then git commit exactly those files; anything else staged or changed is left alone
dotnet run --project src/SqlSc.Cli -- commit path/to/working-folder -m "Add Customer.Email" Sales.Customer
dotnet run --project src/SqlSc.Cli -- commit path/to/working-folder -m "My changes" --mine

# Get latest: deploy the folder's version of objects to the database. --script-only prints the script and changes nothing
dotnet run --project src/SqlSc.Cli -- apply path/to/working-folder --all --script-only
dotnet run --project src/SqlSc.Cli -- apply path/to/working-folder Sales.Customer "[Sales].[GetCustomer]"
dotnet run --project src/SqlSc.Cli -- apply path/to/working-folder --all --yes   # don't ask before each change, e.g. in scripts

# Static data: track a table's rows in Data\Sales.Customer_Data.sql (listed in RedGateDatabaseInfo.xml), then export them
dotnet run --project src/SqlSc.Cli -- data link path/to/working-folder Sales.Customer
dotnet run --project src/SqlSc.Cli -- export path/to/working-folder Sales.Customer --data
dotnet run --project src/SqlSc.Cli -- data unlink path/to/working-folder Sales.Customer
```

`export` writes one file per object in Redgate's layout (`Tables\Sales.Customer.sql`, `Stored Procedures\...`) with the
object's keys, indexes, triggers, permissions and extended properties in the same file, UTF-8 and CRLF line endings.
Database scoped credentials aren't exported, since their secrets can't be read back. `--mine` uses the change log or the default trace.

`apply` runs DacFx's deployment script for the selected objects in one transaction, and rolls it back on any error. On a
shared database this changes it for everyone. `--all` never drops objects that are only in the database; name one to drop
it. Changes that could lose data (dropping a column, or a table with rows) stop the deployment unless you pass
`--allow-data-loss`. Objects whose last change in the database was made by another login are refused unless you pass
`--force`; this uses the change log or the default trace, so on Azure SQL Database or Hyperscale it needs the change log.

On a shared database (a link's default mode, and the mode when there's no link) `apply` lists each change and asks
before running anything: `y` yes, `n` no, `a` yes to the rest, `q` no to the rest. `--yes` skips this, and is needed with
`--json` or when input is redirected. `--script-only` never asks. Every deployment script that runs is saved for audit
in `deployments` next to `links.json`, with the time, server, database, login, working folder, git commit, the changes
and the outcome. Connection strings and passwords are never written.

sql-sc keeps a baseline of each object from the last time the database and the folder matched, so `status` can say where
each difference came from ("Changed in"): `Database` (changed on the server since), `Folder` (changed in the folder, e.g.
by a git pull) or `Conflict` (changed on both). `export` and `commit` won't overwrite a folder-side change, and `apply`
won't undo a database-side change, such as a column someone added on the server: `--all` skips them and says so, and
naming one is refused. Conflicts are always refused; keep the database version with `export <object> --force` or the
folder version with `apply <object> --force`. The baseline is recorded whenever `status`, `export`, `commit` or `apply`
sees an object match or makes it match, so differences show as `Unknown` until then. It's per user, in `baselines` next to
`links.json`, and comparing script text ignores layout.

Static data tables need a primary key; `data link` warns above 10,000 rows. `export`, `commit` and `apply` only include
their rows with `--data`; without it they handle schema only. `status` compares the rows by primary key
(`--diff` lists them) by loading the folder's script into a temporary table, so only `#` tables are written. `export`
writes one `INSERT` per row, ordered by primary key, with `SET IDENTITY_INSERT` around identity tables. `apply` inserts,
updates and deletes rows in one transaction, parents before children; `--all` never deletes rows that are only in the
database, so name the table to delete them.

Changed by (in `status`, `--mine` and `apply`'s check) comes from the default trace on SQL Server and MI. Azure SQL
Database and Hyperscale have no default trace, and the default trace only keeps recent changes, so `changelog install`
adds a database DDL trigger `sqlsc_ChangeLog` that records the time, login, host, application and object of every DDL
statement in `[sqlsc].[ChangeLog]`. When it's installed, sql-sc uses it instead of the default trace. The trigger runs as
`dbo`, so every login's changes are logged without extra permissions, and it never blocks DDL, even if it fails or the
table is missing. Index rebuilds and statistics updates aren't logged. `public` can read the table. sql-sc never shows,
exports or applies the `sqlsc` schema or the trigger. `changelog remove` drops them and what they recorded.

Connection strings are plain `Microsoft.Data.SqlClient` ones, so SQL authentication, Windows authentication
(`Integrated Security=true`) and Entra ID (`Authentication=Active Directory Default` or `Active Directory Interactive`) all work.
Add `--json` to any command for machine-readable output.

The connection comes from `--connection`, then `SQLSC_CONNECTION`, then the link for the folder (or the closest linked
parent folder). Links are stored in `%APPDATA%\sql-sc\links.json` on Windows and `~/.config/sql-sc/links.json` on Linux
(`SQLSC_HOME` overrides the folder). `link` never saves a password: for SQL authentication sql-sc asks for it, or set `SQLSC_PASSWORD` (needed when input is
redirected, e.g. in scripts).

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
| `src/SqlSc.Core` | Working folder reader, settings and links, filters, DacFx model loading, comparison, default trace reader, doctor, export, commit, apply and static data |
| `src/SqlSc.Cli` | `sql-sc` command-line tool |
| `src/SqlSc.Web` | Local Blazor web UI started by `sql-sc ui` |
| `tests/SqlSc.Core.Tests` | Unit tests |
| `tests/SqlSc.Web.Tests` | Web UI unit tests (local access checks, diff view) |
| `tests/SqlSc.IntegrationTests` | Tests against SQL Server 2022 in Docker |
| `tests/Fixtures/Demo` | Small Redgate-layout working folder used by the tests |
