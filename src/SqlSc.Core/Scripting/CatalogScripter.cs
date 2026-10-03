using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlSc.Core.Filtering;

namespace SqlSc.Core.Scripting;

/// <summary>One script to add to a model, and the object type and name DacFx should create from it.</summary>
internal sealed record ScriptUnit(string Source, string Script, bool? QuotedIdentifier = null, bool? AnsiNulls = null, string? ModuleType = null, string? Schema = null, string? Name = null);

internal sealed record ScriptPlan(IReadOnlyList<ScriptUnit> Units, IReadOnlyList<string> Unsupported, int TrackedCount, int ScriptedCount);

/// <summary>
/// Scripts the objects a filter tracks, plus everything they depend on, from a <see cref="CatalogSnapshot"/>.
/// Principals, schemas, role memberships and their permissions are always scripted: they are few and almost
/// everything references them. Anything this class cannot script faithfully is listed in <see cref="ScriptPlan.Unsupported"/>.
/// </summary>
internal sealed class CatalogScripter
{
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.Ordinal)
    {
        ["U"] = "Table",
        ["V"] = "View",
        ["P"] = "Procedure",
        ["FN"] = "ScalarFunction",
        ["IF"] = "TableValuedFunction",
        ["TF"] = "TableValuedFunction",
        ["SN"] = "Synonym",
        ["SO"] = "Sequence",
        ["ET"] = "ExternalTable",
        ["PC"] = "Procedure",
        ["FS"] = "ScalarFunction",
        ["FT"] = "TableValuedFunction",
        ["AF"] = "Aggregate",
    };

    /// <summary>DacFx needs a password to model SQL logins and contained users; like its own extract, a random one is used and never leaves the process.</summary>
    private static readonly string GeneratedPassword = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static readonly HashSet<string> ClrTypes = new(StringComparer.Ordinal) { "PC", "FS", "FT", "AF" };

    private const string BatchSeparator = "\nGO\n";

    private readonly CatalogSnapshot catalog;
    private readonly Dictionary<int, ObjectRow> objects;
    private readonly Dictionary<int, TypeRow> types;
    private readonly Dictionary<int, PrincipalRow> principals;
    private readonly Dictionary<int, SchemaRow> schemasById;
    private readonly ILookup<int, ColumnRow> columns;
    private readonly ILookup<int, IndexRow> indexes;
    private readonly ILookup<(int, int), IndexColumnRow> indexColumns;
    private readonly ILookup<int, StatisticsRow> statistics;
    private readonly ILookup<(int, int), StatisticsColumnRow> statisticsColumns;
    private readonly ILookup<int, ForeignKeyRow> foreignKeys;
    private readonly ILookup<int, ForeignKeyColumnRow> foreignKeyColumns;
    private readonly ILookup<int, CheckRow> checks;
    private readonly Dictionary<int, ModuleRow> modules;
    private readonly ILookup<int, ObjectRow> triggersByParent;
    private readonly Dictionary<int, TableRow> tables;
    private readonly Dictionary<int, ExternalTableRow> externalTables;
    private readonly List<string> unsupported = [];

    public CatalogScripter(CatalogSnapshot catalog)
    {
        this.catalog = catalog;
        objects = catalog.Objects.ToDictionary(o => o.Id);
        types = catalog.Types.ToDictionary(t => t.Id);
        principals = catalog.Principals.ToDictionary(p => p.Id);
        schemasById = catalog.Schemas.ToDictionary(s => s.Id);
        columns = catalog.Columns.ToLookup(c => c.ObjectId);
        indexes = catalog.Indexes.ToLookup(i => i.ObjectId);
        indexColumns = catalog.IndexColumns.ToLookup(c => (c.ObjectId, c.IndexId));
        statistics = catalog.Statistics.ToLookup(s => s.ObjectId);
        statisticsColumns = catalog.StatisticsColumns.ToLookup(c => (c.ObjectId, c.StatsId));
        foreignKeys = catalog.ForeignKeys.ToLookup(f => f.ParentId);
        foreignKeyColumns = catalog.ForeignKeyColumns.ToLookup(c => c.ConstraintId);
        checks = catalog.Checks.ToLookup(c => c.ParentId);
        modules = catalog.Modules.ToDictionary(m => m.ObjectId);
        triggersByParent = catalog.Objects.Where(o => o.Type == "TR").ToLookup(o => o.ParentId);
        tables = catalog.Tables.ToDictionary(t => t.Id);
        externalTables = catalog.ExternalTables.ToDictionary(t => t.Id);
    }

    /// <summary>A node of the dependency graph: a schema-scoped object, a user-defined type, a data source or a credential.</summary>
    private readonly record struct Node(char Kind, int Id);

    public ScriptPlan Plan(ObjectFilter filter, IEnumerable<(string? Schema, string Name)> extraNames)
    {
        unsupported.AddRange(catalog.UnsupportedFeatures.Select(f => Invariant($"{f.Count} {f.Feature}")));

        var seeds = new List<Node>();
        foreach (var obj in catalog.Objects.Where(o => o.ParentId == 0 && TypeNames.ContainsKey(o.Type)))
        {
            if (filter.Includes(TypeNames[obj.Type], [obj.Schema, obj.Name]))
            {
                seeds.Add(new Node('o', obj.Id));
            }
        }

        foreach (var type in catalog.Types)
        {
            if (filter.Includes(type.TableType ? "TableType" : "UserDefinedDataType", [type.Schema, type.Name]))
            {
                seeds.Add(new Node('t', type.Id));
            }
        }

        seeds.AddRange(catalog.DataSources.Where(d => filter.Includes("ExternalDataSource", [d.Name])).Select(d => new Node('d', d.Id)));
        var tracked = seeds.Count;
        seeds.AddRange(Resolve(extraNames));

        var scripted = Closure(seeds);
        var units = new List<ScriptUnit>();
        units.AddRange(ScriptPrincipals());
        units.AddRange(catalog.Schemas.Select(s => new ScriptUnit("schema:" + s.Name, Invariant($"CREATE SCHEMA {Q(s.Name)} AUTHORIZATION {Q(s.Owner)}"))));
        foreach (var node in scripted.OrderBy(n => n.Kind).ThenBy(n => n.Id))
        {
            units.AddRange(node.Kind switch
            {
                'o' => ScriptObject(objects[node.Id]),
                't' => [ScriptType(types[node.Id])],
                'd' => [ScriptDataSource(catalog.DataSources.Single(d => d.Id == node.Id))],
                _ => [ScriptCredential(catalog.Credentials.Single(c => c.Id == node.Id))],
            });
        }

        units.AddRange(ScriptPermissions(scripted));
        units.AddRange(ScriptExtendedProperties(scripted));
        return new ScriptPlan(units, unsupported, tracked, scripted.Count);
    }

    private IEnumerable<Node> Resolve(IEnumerable<(string? Schema, string Name)> names)
    {
        var bySchemaName = catalog.Objects.Where(o => o.ParentId == 0)
            .ToLookup(o => (o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant()));
        var typesByName = catalog.Types.ToLookup(t => (t.Schema.ToUpperInvariant(), t.Name.ToUpperInvariant()));
        var dataSources = catalog.DataSources.ToLookup(d => d.Name.ToUpperInvariant());
        foreach (var (schema, name) in names)
        {
            if (schema is null)
            {
                foreach (var d in dataSources[name.ToUpperInvariant()])
                {
                    yield return new Node('d', d.Id);
                }

                continue;
            }

            var key = (schema.ToUpperInvariant(), name.ToUpperInvariant());
            foreach (var o in bySchemaName[key])
            {
                yield return new Node('o', o.Id);
            }

            foreach (var t in typesByName[key])
            {
                yield return new Node('t', t.Id);
            }
        }
    }

    private HashSet<Node> Closure(IEnumerable<Node> seeds)
    {
        var edges = new Dictionary<Node, List<Node>>();
        void Edge(Node from, Node to) => (edges.TryGetValue(from, out var list) ? list : edges[from] = []).Add(to);

        foreach (var d in catalog.Dependencies)
        {
            if (objects.TryGetValue(d.From, out var from))
            {
                var owner = TopLevel(from);
                if (d.ToType)
                {
                    Edge(new Node('o', owner), new Node('t', d.To));
                }
                else if (objects.TryGetValue(d.To, out var to))
                {
                    Edge(new Node('o', owner), new Node('o', TopLevel(to)));
                }
            }
        }

        foreach (var fk in catalog.ForeignKeys)
        {
            Edge(new Node('o', fk.ParentId), new Node('o', fk.ReferencedId));
        }

        foreach (var table in catalog.Tables.Where(t => t.HistoryTableId != 0))
        {
            Edge(new Node('o', table.Id), new Node('o', table.HistoryTableId));
        }

        foreach (var c in catalog.Columns.Where(c => c.UserType))
        {
            var owner = catalog.Types.FirstOrDefault(t => t.TableObjectId == c.ObjectId) is { } tableType
                ? new Node('t', tableType.Id)
                : new Node('o', c.ObjectId);
            foreach (var t in catalog.Types.Where(t => !t.TableType && t.Schema == c.TypeSchema && t.Name == c.TypeName))
            {
                Edge(owner, new Node('t', t.Id));
            }
        }

        foreach (var (objectId, typeId) in catalog.ParameterTypes)
        {
            Edge(new Node('o', objectId), new Node('t', typeId));
        }

        foreach (var et in catalog.ExternalTables)
        {
            Edge(new Node('o', et.Id), new Node('d', et.DataSourceId));
        }

        foreach (var ds in catalog.DataSources.Where(d => d.CredentialId > 0))
        {
            Edge(new Node('d', ds.Id), new Node('c', ds.CredentialId));
        }

        var result = new HashSet<Node>();
        var queue = new Queue<Node>(seeds);
        while (queue.TryDequeue(out var node))
        {
            if ((node.Kind == 'o' && !objects.ContainsKey(node.Id)) || (node.Kind == 't' && !types.ContainsKey(node.Id)) || !result.Add(node))
            {
                continue;
            }

            foreach (var next in edges.GetValueOrDefault(node) ?? [])
            {
                queue.Enqueue(next);
            }
        }

        return result;
    }

    private int TopLevel(ObjectRow obj)
    {
        var current = obj;
        while (current.ParentId != 0 && objects.TryGetValue(current.ParentId, out var parent))
        {
            current = parent;
        }

        return current.Id;
    }

    private IEnumerable<ScriptUnit> ScriptPrincipals()
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in catalog.Principals.Where(p => p.Id > 4 && !p.FixedRole))
        {
            var name = Q(p.Name);
            switch (p.Type)
            {
                case "R":
                    yield return new ScriptUnit("role:" + p.Name, Invariant($"CREATE ROLE {name} AUTHORIZATION {Q(PrincipalName(p.OwnerId ?? 1))}"));
                    continue;
                case "A":
                    unsupported.Add("application role " + name);
                    continue;
                case "C" or "K":
                    unsupported.Add("certificate or key user " + name);
                    continue;
            }

            var schema = p.DefaultSchema is { } s && !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase)
                ? Invariant($" WITH DEFAULT_SCHEMA = {Q(s)}")
                : string.Empty;
            var login = p.LoginName ?? p.Name;
            string script;
            switch (p.AuthenticationType)
            {
                case 1:
                    script = Invariant($"CREATE USER {name} FOR LOGIN {Q(login)}{schema}");
                    if (logins.Add(login))
                    {
                        var source = p.Type switch { "U" or "G" => " FROM WINDOWS", "E" or "X" => " FROM EXTERNAL PROVIDER", _ => " WITH PASSWORD = " + N(GeneratedPassword) };
                        yield return new ScriptUnit("login:" + login, Invariant($"CREATE LOGIN {Q(login)}{source}"));
                    }

                    break;
                case 2:
                    script = Invariant($"CREATE USER {name} WITH PASSWORD = {N(GeneratedPassword)}{(schema.Length > 0 ? ", " + schema[6..] : string.Empty)}");
                    break;
                case 4:
                    script = Invariant($"CREATE USER {name} FROM EXTERNAL PROVIDER{schema}");
                    break;
                case 3:
                    script = Invariant($"CREATE USER {name}{schema}");
                    break;
                default:
                    script = Invariant($"CREATE USER {name} WITHOUT LOGIN{schema}");
                    break;
            }

            yield return new ScriptUnit("user:" + p.Name, script);
        }

        var members = catalog.RoleMembers
            .Where(m => m.MemberId != 1 && principals.ContainsKey(m.RoleId) && principals.ContainsKey(m.MemberId))
            .Select(m => Invariant($"ALTER ROLE {Q(PrincipalName(m.RoleId))} ADD MEMBER {Q(PrincipalName(m.MemberId))}"))
            .ToList();
        if (members.Count > 0)
        {
            yield return new ScriptUnit("rolemembers", string.Join(BatchSeparator, members));
        }
    }

    private IEnumerable<ScriptUnit> ScriptObject(ObjectRow obj)
    {
        var name = Q(obj.Schema, obj.Name);
        if (ClrTypes.Contains(obj.Type))
        {
            unsupported.Add(Invariant($"CLR object {name}"));
            return [];
        }

        return obj.Type switch
        {
            "U" => ScriptTable(obj),
            "ET" => [ScriptExternalTable(obj)],
            "SN" => [new ScriptUnit("o:" + name, Invariant($"CREATE SYNONYM {name} FOR {catalog.Synonyms.Single(s => s.Id == obj.Id).BaseObject}"))],
            "SO" => [ScriptSequence(obj)],
            "V" => ScriptModule(obj, "View").Concat(ScriptIndexes(obj)),
            "P" => ScriptModule(obj, "Procedure"),
            "FN" => ScriptModule(obj, "ScalarFunction"),
            "IF" or "TF" => ScriptModule(obj, "TableValuedFunction"),
            _ => [],
        };
    }

    private IEnumerable<ScriptUnit> ScriptModule(ObjectRow obj, string type)
    {
        if (!modules.TryGetValue(obj.Id, out var module) || module.Definition is null)
        {
            unsupported.Add(Invariant($"encrypted module {Q(obj.Schema, obj.Name)}"));
            yield break;
        }

        if (module.NativelyCompiled)
        {
            unsupported.Add(Invariant($"natively compiled module {Q(obj.Schema, obj.Name)}"));
            yield break;
        }

        yield return new ScriptUnit("o:" + Q(obj.Schema, obj.Name), WithName(module.Definition, module.QuotedIdentifier, obj.Schema, obj.Name), module.QuotedIdentifier, module.AnsiNulls, type, obj.Schema, obj.Name);
    }

    private List<ScriptUnit> ScriptTable(ObjectRow obj)
    {
        var name = Q(obj.Schema, obj.Name);
        var table = tables[obj.Id];
        var features = new List<string>();
        if (table.MemoryOptimized) features.Add("memory-optimized");
        if (table.Graph) features.Add("graph");
        if (table.ChangeTracking) features.Add("change tracking");
        if (table.FileTable) features.Add("FileTable");
        if (table.LargeValuesOutOfRow || table.TextInRowLimit != 0) features.Add("table options");
        if (columns[obj.Id].Any(c => c.Unsupported)) features.Add("column features");
        if (indexes[obj.Id].Any(i => i.Partitioned)) features.Add("partitioned");
        if (indexes[obj.Id].Any(i => i.Type is not (0 or 1 or 2) || i.Disabled)) features.Add("index type");
        if (features.Count > 0)
        {
            unsupported.Add(Invariant($"table {name} ({string.Join(", ", features)})"));
            return [];
        }

        var sb = new StringBuilder();
        sb.Append(Invariant($"CREATE TABLE {name}\n(\n"));
        sb.AppendJoin(",\n", columns[obj.Id].Select(ColumnDefinition));
        if (columns[obj.Id].FirstOrDefault(c => c.GeneratedAlways == 1) is { } start && columns[obj.Id].FirstOrDefault(c => c.GeneratedAlways == 2) is { } end)
        {
            sb.Append(Invariant($",\nPERIOD FOR SYSTEM_TIME ({Q(start.Name)}, {Q(end.Name)})"));
        }

        sb.Append("\n)");
        var options = new List<string>();
        var heap = indexes[obj.Id].FirstOrDefault(i => i.Type == 0);
        if (heap is { Compression: { } c } && c != "NONE")
        {
            options.Add(Invariant($"DATA_COMPRESSION = {c}"));
        }

        if (table.TemporalType == 2 && objects.TryGetValue(table.HistoryTableId, out var history))
        {
            var retention = table.RetentionPeriod > 0 ? Invariant($", HISTORY_RETENTION_PERIOD = {table.RetentionPeriod} {table.RetentionUnit}S") : string.Empty;
            options.Add(Invariant($"SYSTEM_VERSIONING = ON (HISTORY_TABLE = {Q(history.Schema, history.Name)}{retention})"));
        }

        if (options.Count > 0)
        {
            sb.Append(" WITH (").AppendJoin(", ", options).Append(')');
        }

        sb.Append('\n');
        if (table.LockEscalation != 0)
        {
            sb.Append(Invariant($"GO\nALTER TABLE {name} SET (LOCK_ESCALATION = {(table.LockEscalation == 1 ? "DISABLE" : "AUTO")})\n"));
        }

        foreach (var key in indexes[obj.Id].Where(i => i.PrimaryKey || i.UniqueConstraint))
        {
            var constraint = key.SystemNamed ? string.Empty : Invariant($"CONSTRAINT {Q(key.Name!)} ");
            sb.Append(Invariant($"GO\nALTER TABLE {name} ADD {constraint}{(key.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {(key.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(key)}){IndexOptions(key)}\n"));
        }

        foreach (var check in checks[obj.Id])
        {
            var constraint = check.SystemNamed ? string.Empty : Invariant($"CONSTRAINT {Q(check.Name)} ");
            sb.Append(Invariant($"GO\nALTER TABLE {name} ADD {constraint}CHECK{(check.NotForReplication ? " NOT FOR REPLICATION" : string.Empty)} {check.Definition}\n"));
            if (check.Disabled)
            {
                sb.Append(Invariant($"GO\nALTER TABLE {name} NOCHECK CONSTRAINT {Q(check.Name)}\n"));
            }
        }

        foreach (var fk in foreignKeys[obj.Id])
        {
            var cols = foreignKeyColumns[fk.Id].OrderBy(c => c.Ordinal).ToList();
            var referenced = objects[fk.ReferencedId];
            var constraint = fk.SystemNamed ? string.Empty : Invariant($"CONSTRAINT {Q(fk.Name)} ");
            sb.Append(Invariant($"GO\nALTER TABLE {name} ADD {constraint}FOREIGN KEY ({string.Join(", ", cols.Select(c => Q(c.Column)))}) REFERENCES {Q(referenced.Schema, referenced.Name)} ({string.Join(", ", cols.Select(c => Q(c.ReferencedColumn)))})"));
            sb.Append(ReferentialAction("DELETE", fk.OnDelete)).Append(ReferentialAction("UPDATE", fk.OnUpdate));
            sb.Append(fk.NotForReplication ? " NOT FOR REPLICATION\n" : "\n");
            if (fk.Disabled)
            {
                sb.Append(Invariant($"GO\nALTER TABLE {name} NOCHECK CONSTRAINT {Q(fk.Name)}\n"));
            }
        }

        var units = new List<ScriptUnit> { new("o:" + name, sb.ToString()) };
        units.AddRange(ScriptIndexes(obj));
        foreach (var trigger in triggersByParent[obj.Id])
        {
            if (modules.TryGetValue(trigger.Id, out var module) && module.Definition is not null)
            {
                var definition = WithName(module.Definition, module.QuotedIdentifier, trigger.Schema, trigger.Name, (obj.Schema, obj.Name));
                var script = module.TriggerDisabled
                    ? Invariant($"{definition}{BatchSeparator}DISABLE TRIGGER {Q(trigger.Schema, trigger.Name)} ON {name}")
                    : definition;
                units.Add(new ScriptUnit("o:" + Q(trigger.Schema, trigger.Name), script, module.QuotedIdentifier, module.AnsiNulls, "DmlTrigger", trigger.Schema, trigger.Name));
            }
            else
            {
                unsupported.Add(Invariant($"trigger {Q(trigger.Schema, trigger.Name)}"));
            }
        }

        return units;
    }

    private IEnumerable<ScriptUnit> ScriptIndexes(ObjectRow obj)
    {
        var name = Q(obj.Schema, obj.Name);
        var sb = new StringBuilder();
        foreach (var index in indexes[obj.Id].Where(i => i.Type is 1 or 2 && !i.PrimaryKey && !i.UniqueConstraint))
        {
            var included = indexColumns[(obj.Id, index.IndexId)].Where(c => c.Included).OrderBy(c => c.IndexColumnId).Select(c => Q(c.Column)).ToList();
            sb.Append(Invariant($"GO\nCREATE {(index.Unique ? "UNIQUE " : string.Empty)}{(index.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")} INDEX {Q(index.Name!)} ON {name} ({KeyColumns(index)})"));
            if (included.Count > 0)
            {
                sb.Append(Invariant($" INCLUDE ({string.Join(", ", included)})"));
            }

            if (index.Filter is { } filter)
            {
                sb.Append(" WHERE ").Append(filter);
            }

            sb.Append(IndexOptions(index)).Append('\n');
        }

        foreach (var stat in statistics[obj.Id])
        {
            var cols = statisticsColumns[(obj.Id, stat.StatsId)].OrderBy(c => c.Ordinal).Select(c => Q(c.Column));
            sb.Append(Invariant($"GO\nCREATE STATISTICS {Q(stat.Name)} ON {name} ({string.Join(", ", cols)})"));
            if (stat.Filter is { } filter)
            {
                sb.Append(" WHERE ").Append(filter);
            }

            sb.Append(stat.NoRecompute ? " WITH NORECOMPUTE\n" : "\n");
        }

        return sb.Length == 0 ? [] : [new ScriptUnit("indexes:" + name, sb.ToString())];
    }

    private ScriptUnit ScriptExternalTable(ObjectRow obj)
    {
        var name = Q(obj.Schema, obj.Name);
        var et = externalTables[obj.Id];
        if (et.Location is not null || et.FileFormatId != 0 || et.Distribution != 255)
        {
            unsupported.Add(Invariant($"external table {name} (not an elastic query table)"));
        }

        var dataSource = catalog.DataSources.First(d => d.Id == et.DataSourceId);
        var options = new List<string> { Invariant($"DATA_SOURCE = {Q(dataSource.Name)}") };
        if (et.RemoteSchema is { } schema)
        {
            options.Add(Invariant($"SCHEMA_NAME = {N(schema)}"));
        }

        if (et.RemoteObject is { } remote)
        {
            options.Add(Invariant($"OBJECT_NAME = {N(remote)}"));
        }

        var body = string.Join(",\n", columns[obj.Id].Select(ColumnDefinition));
        return new ScriptUnit("o:" + name, Invariant($"CREATE EXTERNAL TABLE {name}\n(\n{body}\n)\nWITH\n(\n{string.Join(",\n", options)}\n)"));
    }

    private ScriptUnit ScriptDataSource(DataSourceRow ds)
    {
        var options = new List<string> { Invariant($"LOCATION = {N(ds.Location)}") };
        if (ds.CredentialId > 0 && catalog.Credentials.FirstOrDefault(c => c.Id == ds.CredentialId) is { } credential)
        {
            options.Add(Invariant($"CREDENTIAL = {Q(credential.Name)}"));
        }

        if (ds.TypeDesc is "RDBMS" or "SHARD_MAP_MANAGER" or "BLOB_STORAGE" or "HADOOP")
        {
            options.Add("TYPE = " + ds.TypeDesc);
        }

        if (ds.DatabaseName is { } database)
        {
            options.Add(Invariant($"DATABASE_NAME = {N(database)}"));
        }

        if (ds.ShardMapName is { } shardMap)
        {
            options.Add(Invariant($"SHARD_MAP_NAME = {N(shardMap)}"));
        }

        return new ScriptUnit("datasource:" + ds.Name, Invariant($"CREATE EXTERNAL DATA SOURCE {Q(ds.Name)} WITH\n(\n{string.Join(",\n", options)}\n)"));
    }

    private static ScriptUnit ScriptCredential(CredentialRow credential) =>
        new("credential:" + credential.Name, Invariant($"CREATE DATABASE SCOPED CREDENTIAL {Q(credential.Name)} WITH IDENTITY = {N(credential.Identity)}"));

    private ScriptUnit ScriptSequence(ObjectRow obj)
    {
        var s = catalog.Sequences.Single(q => q.Id == obj.Id);
        var type = s.TypeName is "decimal" or "numeric" ? Invariant($"[{s.TypeName}] ({s.Precision}, 0)") : Invariant($"[{s.TypeName}]");
        var cache = !s.Cached ? "NO CACHE" : s.CacheSize is { } size ? Invariant($"CACHE {size}") : "CACHE";
        return new ScriptUnit("o:" + Q(obj.Schema, obj.Name), Invariant(
            $"CREATE SEQUENCE {Q(obj.Schema, obj.Name)} AS {type} START WITH {s.Start} INCREMENT BY {s.Increment} MINVALUE {s.Minimum} MAXVALUE {s.Maximum} {(s.Cycling ? "CYCLE" : "NO CYCLE")} {cache}"));
    }

    private ScriptUnit ScriptType(TypeRow type)
    {
        var name = Q(type.Schema, type.Name);
        if (type.Clr || type.MemoryOptimized)
        {
            unsupported.Add(Invariant($"type {name}"));
            return new ScriptUnit("t:" + name, string.Empty);
        }

        if (!type.TableType)
        {
            return new ScriptUnit("t:" + name, Invariant($"CREATE TYPE {name} FROM {SystemType(type.BaseType, type.MaxLength, type.Precision, type.Scale)}{(type.Nullable ? " NULL" : " NOT NULL")}"));
        }

        var parts = columns[type.TableObjectId].Select(ColumnDefinition).ToList();
        foreach (var key in indexes[type.TableObjectId].Where(i => i.PrimaryKey || i.UniqueConstraint))
        {
            parts.Add(Invariant($"{(key.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {(key.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(key)}){IndexOptions(key)}"));
        }

        foreach (var index in indexes[type.TableObjectId].Where(i => i.Type is 1 or 2 && !i.PrimaryKey && !i.UniqueConstraint))
        {
            parts.Add(Invariant($"INDEX {Q(index.Name!)} {(index.Unique ? "UNIQUE " : string.Empty)}{(index.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(index)})"));
        }

        parts.AddRange(checks[type.TableObjectId].Select(c => "CHECK " + c.Definition));
        return new ScriptUnit("t:" + name, Invariant($"CREATE TYPE {name} AS TABLE\n(\n{string.Join(",\n", parts)}\n)"));
    }

    private IEnumerable<ScriptUnit> ScriptPermissions(HashSet<Node> scripted)
    {
        var statements = new List<string>();
        foreach (var p in catalog.Permissions)
        {
            if (!principals.TryGetValue(p.GranteeId, out var grantee))
            {
                continue;
            }

            string? on = p.Class switch
            {
                0 => string.Empty,
                1 when scripted.Contains(new Node('o', p.MajorId)) && objects.TryGetValue(p.MajorId, out var o) =>
                    Invariant($" ON {Q(o.Schema, o.Name)}{(p.Column is { } c ? Invariant($" ({Q(c)})") : string.Empty)}"),
                3 when schemasById.TryGetValue(p.MajorId, out var s) => Invariant($" ON SCHEMA::{Q(s.Name)}"),
                4 when principals.TryGetValue(p.MajorId, out var pr) =>
                    Invariant($" ON {(pr.Type == "R" ? "ROLE" : pr.Type == "A" ? "APPLICATION ROLE" : "USER")}::{Q(pr.Name)}"),
                6 when scripted.Contains(new Node('t', p.MajorId)) => Invariant($" ON TYPE::{Q(types[p.MajorId].Schema, types[p.MajorId].Name)}"),
                1 or 3 or 6 => null,
                _ => Unsupported(Invariant($"permission class {p.Class}")),
            };
            if (on is null)
            {
                continue;
            }

            var verb = p.State == "D" ? "DENY" : "GRANT";
            var grantor = p.GrantorId != 1 && principals.TryGetValue(p.GrantorId, out var g) ? " AS " + Q(g.Name) : string.Empty;
            statements.Add(Invariant($"{verb} {p.Name}{on} TO {Q(grantee.Name)}{(p.State == "W" ? " WITH GRANT OPTION" : string.Empty)}{grantor}"));
        }

        return statements.Count == 0 ? [] : [new ScriptUnit("permissions", string.Join(BatchSeparator, statements))];
    }

    private IEnumerable<ScriptUnit> ScriptExtendedProperties(HashSet<Node> scripted)
    {
        var statements = new List<string>();
        foreach (var ep in catalog.ExtendedProperties)
        {
            string? levels = ep.Class switch
            {
                0 => "NULL, NULL, NULL, NULL, NULL, NULL",
                3 when schemasById.TryGetValue(ep.MajorId, out var s) => Invariant($"'SCHEMA', {N(s.Name)}, NULL, NULL, NULL, NULL"),
                1 or 2 or 7 when objects.TryGetValue(ep.MajorId, out var o) && scripted.Contains(new Node('o', TopLevel(o))) => ObjectLevels(ep, o),
                6 when scripted.Contains(new Node('t', ep.MajorId)) => Invariant($"'SCHEMA', {N(types[ep.MajorId].Schema)}, 'TYPE', {N(types[ep.MajorId].Name)}, NULL, NULL"),
                4 when principals.TryGetValue(ep.MajorId, out var p) => Invariant($"'USER', {N(p.Name)}, NULL, NULL, NULL, NULL"),
                1 or 2 or 3 or 6 or 7 => null,
                _ => Unsupported(Invariant($"extended property class {ep.Class}")),
            };
            if (levels is not null)
            {
                statements.Add(Invariant($"EXEC sp_addextendedproperty {N(ep.Name)}, {Literal(ep.Value, ep.BaseType)}, {levels}"));
            }
        }

        return statements.Count == 0 ? [] : [new ScriptUnit("extendedproperties", string.Join(BatchSeparator, statements))];
    }

    private string? ObjectLevels(ExtendedPropertyRow ep, ObjectRow obj)
    {
        var parent = obj.ParentId != 0 && objects.TryGetValue(obj.ParentId, out var p) ? p : null;
        var owner = parent ?? obj;
        var level1 = owner.Type switch
        {
            "U" or "ET" => "TABLE",
            "V" => "VIEW",
            "P" => "PROCEDURE",
            "FN" or "IF" or "TF" => "FUNCTION",
            "SN" => "SYNONYM",
            "SO" => "SEQUENCE",
            _ => null,
        };
        if (level1 is null)
        {
            return Unsupported(Invariant($"extended property on {owner.Type}"));
        }

        var head = Invariant($"'SCHEMA', {N(owner.Schema)}, '{level1}', {N(owner.Name)}");
        if (parent is not null)
        {
            var level2 = obj.Type == "TR" ? "TRIGGER" : "CONSTRAINT";
            return Invariant($"{head}, '{level2}', {N(obj.Name)}");
        }

        return ep.Class switch
        {
            1 when ep.MinorId == 0 => head + ", NULL, NULL",
            1 or 2 or 7 when ep.MinorName is { } minor => Invariant($"{head}, '{(ep.Class == 1 ? "COLUMN" : ep.Class == 2 ? "PARAMETER" : "INDEX")}', {N(minor)}"),
            _ => null,
        };
    }

    private string ColumnDefinition(ColumnRow c)
    {
        var sb = new StringBuilder(Q(c.Name));
        if (c.Computed is { } computed)
        {
            sb.Append(" AS ").Append(computed);
            if (c.Persisted)
            {
                sb.Append(" PERSISTED");
                if (!c.Nullable)
                {
                    sb.Append(" NOT NULL");
                }
            }

            return sb.ToString();
        }

        sb.Append(' ').Append(c.UserType ? Q(c.TypeSchema, c.TypeName) : SystemType(c.TypeName, c.MaxLength, c.Precision, c.Scale));
        if (c.Collation is { } collation && !string.Equals(collation, catalog.Collation, StringComparison.Ordinal))
        {
            sb.Append(" COLLATE ").Append(collation);
        }

        if (c.GeneratedAlways != 0)
        {
            sb.Append(c.GeneratedAlways == 1 ? " GENERATED ALWAYS AS ROW START" : " GENERATED ALWAYS AS ROW END");
            if (c.Hidden)
            {
                sb.Append(" HIDDEN");
            }
        }

        if (c.Sparse)
        {
            sb.Append(" SPARSE");
        }

        sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        if (c.Identity)
        {
            sb.Append(Invariant($" IDENTITY({c.Seed}, {c.Increment})"));
            if (c.IdentityNotForReplication)
            {
                sb.Append(" NOT FOR REPLICATION");
            }
        }

        if (c.RowGuid)
        {
            sb.Append(" ROWGUIDCOL");
        }

        if (c.DefaultDefinition is { } definition)
        {
            if (!c.DefaultSystemNamed)
            {
                sb.Append(" CONSTRAINT ").Append(Q(c.DefaultName!));
            }

            sb.Append(" DEFAULT ").Append(definition);
        }

        return sb.ToString();
    }

    private string KeyColumns(IndexRow index) =>
        string.Join(", ", indexColumns[(index.ObjectId, index.IndexId)]
            .Where(c => !c.Included && c.KeyOrdinal > 0)
            .OrderBy(c => c.KeyOrdinal)
            .Select(c => Q(c.Column) + (c.Descending ? " DESC" : string.Empty)));

    private static string IndexOptions(IndexRow index)
    {
        var options = new List<string>();
        if (index.Padded) options.Add("PAD_INDEX = ON");
        if (index.FillFactor is not (0 or 100)) options.Add(Invariant($"FILLFACTOR = {index.FillFactor}"));
        if (index.IgnoreDupKey) options.Add("IGNORE_DUP_KEY = ON");
        if (index.NoRecompute) options.Add("STATISTICS_NORECOMPUTE = ON");
        if (!index.AllowRowLocks) options.Add("ALLOW_ROW_LOCKS = OFF");
        if (!index.AllowPageLocks) options.Add("ALLOW_PAGE_LOCKS = OFF");
        if (index.Compression is { } c && c != "NONE") options.Add("DATA_COMPRESSION = " + c);
        return options.Count == 0 ? string.Empty : Invariant($" WITH ({string.Join(", ", options)})");
    }

    private static string ReferentialAction(string verb, int action) => action switch
    {
        1 => Invariant($" ON {verb} CASCADE"),
        2 => Invariant($" ON {verb} SET NULL"),
        3 => Invariant($" ON {verb} SET DEFAULT"),
        _ => string.Empty,
    };

    internal static string SystemType(string name, short maxLength, byte precision, byte scale)
    {
        var type = Q(name);
        return name switch
        {
            "sysname" => "[sys].[sysname]",
            "varchar" or "char" or "varbinary" or "binary" => type + (maxLength == -1 ? " (max)" : Invariant($" ({maxLength})")),
            "nvarchar" or "nchar" => type + (maxLength == -1 ? " (max)" : Invariant($" ({maxLength / 2})")),
            "decimal" or "numeric" => type + Invariant($" ({precision}, {scale})"),
            "datetime2" or "time" or "datetimeoffset" => type + Invariant($" ({scale})"),
            "float" when precision != 53 => type + Invariant($" ({precision})"),
            _ => type,
        };
    }

    private string PrincipalName(int id) => principals.TryGetValue(id, out var p) ? p.Name : "dbo";

    private string? Unsupported(string reason)
    {
        unsupported.Add(reason);
        return null;
    }

    private static string Literal(string? value, string? baseType) => value is null
        ? "NULL"
        : baseType switch
        {
            "varchar" or "char" => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
            "int" or "bigint" or "smallint" or "tinyint" or "bit" or "decimal" or "numeric" or "float" or "real" or "money" => value,
            _ => N(value),
        };

    /// <summary>
    /// A module definition with the name in its CREATE statement replaced by the object's actual schema and name.
    /// sys.sql_modules keeps the text as written, so a module created without a schema, or renamed with sp_rename, names something else.
    /// </summary>
    internal static string WithName(string definition, bool quotedIdentifier, string schema, string name, (string Schema, string Name)? table = null)
    {
        var fragment = new TSql170Parser(quotedIdentifier).Parse(new StringReader(definition), out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript { Batches: [{ Statements: [var statement, ..] }, ..] })
        {
            return definition;
        }

        var target = statement switch
        {
            ProcedureStatementBody procedure => procedure.ProcedureReference?.Name,
            FunctionStatementBody function => function.Name,
            ViewStatementBody view => view.SchemaObjectName,
            TriggerStatementBody trigger => trigger.Name,
            _ => null,
        };
        var replacements = new List<(SchemaObjectName Name, string Text)>();
        if (target is not null && !Names(target, schema, name))
        {
            replacements.Add((target, Q(schema, name)));
        }

        if (table is var (tableSchema, tableName)
            && statement is TriggerStatementBody { TriggerObject: { TriggerScope: TriggerScope.Normal, Name: { } on } }
            && !Names(on, tableSchema, tableName))
        {
            replacements.Add((on, Q(tableSchema, tableName)));
        }

        foreach (var (old, text) in replacements.OrderByDescending(r => r.Name.StartOffset))
        {
            definition = string.Concat(definition.AsSpan(0, old.StartOffset), text, definition.AsSpan(old.StartOffset + old.FragmentLength));
        }

        return definition;
    }

    private static bool Names(SchemaObjectName name, string schema, string objectName) =>
        name.SchemaIdentifier?.Value == schema && name.BaseIdentifier?.Value == objectName && name.DatabaseIdentifier is null;

    internal static string Q(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string Q(string schema, string name) => Q(schema) + "." + Q(name);

    private static string N(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
