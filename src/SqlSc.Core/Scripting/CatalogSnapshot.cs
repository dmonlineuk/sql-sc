using Microsoft.Data.SqlClient;

namespace SqlSc.Core.Scripting;

internal sealed record SchemaRow(int Id, string Name, string Owner);

internal sealed record ObjectRow(int Id, string Schema, string Name, string Type, int ParentId);

internal sealed record TableRow(int Id, int TemporalType, bool MemoryOptimized, bool Graph, int LockEscalation, bool LargeValuesOutOfRow, int TextInRowLimit, bool ChangeTracking, bool FileTable);

internal sealed record ColumnRow(
    int ObjectId,
    int ColumnId,
    string Name,
    string TypeSchema,
    string TypeName,
    bool UserType,
    short MaxLength,
    byte Precision,
    byte Scale,
    string? Collation,
    bool Nullable,
    bool Identity,
    string? Seed,
    string? Increment,
    bool IdentityNotForReplication,
    string? Computed,
    bool Persisted,
    bool Sparse,
    bool RowGuid,
    bool Unsupported,
    string? DefaultName,
    string? DefaultDefinition,
    bool DefaultSystemNamed);

internal sealed record IndexRow(
    int ObjectId,
    int IndexId,
    string? Name,
    int Type,
    bool Unique,
    bool PrimaryKey,
    bool UniqueConstraint,
    bool SystemNamed,
    bool IgnoreDupKey,
    int FillFactor,
    bool Padded,
    bool AllowRowLocks,
    bool AllowPageLocks,
    string? Filter,
    bool Disabled,
    bool NoRecompute,
    string? Compression,
    bool Partitioned);

internal sealed record IndexColumnRow(int ObjectId, int IndexId, string Column, int KeyOrdinal, bool Descending, bool Included, int IndexColumnId);

internal sealed record StatisticsRow(int ObjectId, int StatsId, string Name, string? Filter, bool NoRecompute);

internal sealed record StatisticsColumnRow(int ObjectId, int StatsId, int Ordinal, string Column);

internal sealed record ForeignKeyRow(int Id, string Name, int ParentId, int ReferencedId, int OnDelete, int OnUpdate, bool NotForReplication, bool Disabled, bool NotTrusted, bool SystemNamed);

internal sealed record ForeignKeyColumnRow(int ConstraintId, int Ordinal, string Column, string ReferencedColumn);

internal sealed record CheckRow(int Id, string Name, int ParentId, string Definition, bool Disabled, bool NotTrusted, bool NotForReplication, bool SystemNamed);

internal sealed record ModuleRow(int ObjectId, string? Definition, bool AnsiNulls, bool QuotedIdentifier, bool TriggerDisabled, bool NativelyCompiled);

internal sealed record TypeRow(int Id, string Schema, string Name, string BaseType, short MaxLength, byte Precision, byte Scale, bool Nullable, bool TableType, int TableObjectId, bool Clr, bool MemoryOptimized);

internal sealed record SequenceRow(int Id, string TypeName, byte Precision, string Start, string Increment, string Minimum, string Maximum, bool Cycling, bool Cached, int? CacheSize);

internal sealed record PrincipalRow(int Id, string Name, string Type, int? OwnerId, string? DefaultSchema, int AuthenticationType, string? LoginName, bool FixedRole);

internal sealed record PermissionRow(int Class, int MajorId, int MinorId, int GranteeId, string Name, string State, string? Column, int GrantorId);

internal sealed record ExtendedPropertyRow(int Class, int MajorId, int MinorId, string Name, string? Value, string? BaseType, string? MinorName);

internal sealed record DataSourceRow(int Id, string Name, string Location, string TypeDesc, string? DatabaseName, string? ShardMapName, int CredentialId);

internal sealed record CredentialRow(int Id, string Name, string Identity);

internal sealed record ExternalTableRow(int Id, int DataSourceId, string? Location, int FileFormatId, string? RemoteSchema, string? RemoteObject, int Distribution);

internal sealed record DependencyRow(int From, int To, bool ToType);

/// <summary>Everything <see cref="CatalogScripter"/> needs, read with one bulk query per catalog view.</summary>
internal sealed class CatalogSnapshot
{
    public required string Collation { get; init; }

    public required IReadOnlyList<SchemaRow> Schemas { get; init; }

    public required IReadOnlyList<ObjectRow> Objects { get; init; }

    public required IReadOnlyList<TableRow> Tables { get; init; }

    public required IReadOnlyList<ColumnRow> Columns { get; init; }

    public required IReadOnlyList<IndexRow> Indexes { get; init; }

    public required IReadOnlyList<IndexColumnRow> IndexColumns { get; init; }

    public required IReadOnlyList<StatisticsRow> Statistics { get; init; }

    public required IReadOnlyList<StatisticsColumnRow> StatisticsColumns { get; init; }

    public required IReadOnlyList<ForeignKeyRow> ForeignKeys { get; init; }

    public required IReadOnlyList<ForeignKeyColumnRow> ForeignKeyColumns { get; init; }

    public required IReadOnlyList<CheckRow> Checks { get; init; }

    public required IReadOnlyList<ModuleRow> Modules { get; init; }

    public required IReadOnlyList<TypeRow> Types { get; init; }

    public required IReadOnlyList<(int Id, string BaseObject)> Synonyms { get; init; }

    public required IReadOnlyList<SequenceRow> Sequences { get; init; }

    public required IReadOnlyList<PrincipalRow> Principals { get; init; }

    public required IReadOnlyList<(int RoleId, int MemberId)> RoleMembers { get; init; }

    public required IReadOnlyList<PermissionRow> Permissions { get; init; }

    public required IReadOnlyList<ExtendedPropertyRow> ExtendedProperties { get; init; }

    public required IReadOnlyList<DataSourceRow> DataSources { get; init; }

    public required IReadOnlyList<CredentialRow> Credentials { get; init; }

    public required IReadOnlyList<ExternalTableRow> ExternalTables { get; init; }

    public required IReadOnlyList<DependencyRow> Dependencies { get; init; }

    public required IReadOnlyList<(int ObjectId, int TypeId)> ParameterTypes { get; init; }

    /// <summary>Database-level features the scripter does not handle, with how many of each exist.</summary>
    public required IReadOnlyList<(string Feature, int Count)> UnsupportedFeatures { get; init; }

    public static CatalogSnapshot Read(SqlConnection connection) => new()
    {
        Collation = Read(connection, "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))", r => r.GetString(0)).Single(),
        Schemas = Read(connection, """
            SELECT s.schema_id, s.name, p.name
            FROM sys.schemas AS s JOIN sys.database_principals AS p ON p.principal_id = s.principal_id
            WHERE s.schema_id BETWEEN 5 AND 16383
            """, r => new SchemaRow(r.GetInt32(0), r.GetString(1), r.GetString(2))),
        Objects = Read(connection, """
            SELECT o.object_id, s.name, o.name, RTRIM(o.type), o.parent_object_id
            FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            """, r => new ObjectRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4))),
        Tables = Read(connection, """
            SELECT t.object_id, t.temporal_type, t.is_memory_optimized, CAST(t.is_node | t.is_edge AS bit), t.lock_escalation,
                   t.large_value_types_out_of_row, t.text_in_row_limit,
                   CAST(CASE WHEN ct.object_id IS NULL THEN 0 ELSE 1 END AS bit), t.is_filetable
            FROM sys.tables AS t LEFT JOIN sys.change_tracking_tables AS ct ON ct.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            """, r => new TableRow(r.GetInt32(0), r.GetByte(1), r.GetBoolean(2), r.GetBoolean(3), r.GetByte(4), r.GetBoolean(5), r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8))),
        Columns = Read(connection, """
            SELECT c.object_id, c.column_id, c.name, ts.name, t.name, t.is_user_defined, c.max_length, c.precision, c.scale,
                   c.collation_name, c.is_nullable, c.is_identity,
                   CAST(ic.seed_value AS nvarchar(64)), CAST(ic.increment_value AS nvarchar(64)), ISNULL(ic.is_not_for_replication, 0),
                   cc.definition, ISNULL(cc.is_persisted, 0), c.is_sparse, c.is_rowguidcol,
                   CAST(CASE WHEN c.is_filestream = 1 OR c.is_column_set = 1 OR c.generated_always_type <> 0
                             OR c.encryption_type IS NOT NULL OR c.is_masked = 1 OR c.xml_collection_id <> 0 THEN 1 ELSE 0 END AS bit),
                   dc.name, dc.definition, ISNULL(dc.is_system_named, 0)
            FROM sys.columns AS c
            JOIN sys.objects AS o ON o.object_id = c.object_id
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id
            LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            WHERE (o.is_ms_shipped = 0 OR o.type = 'TT') AND o.type IN ('U', 'TT', 'ET')
            ORDER BY c.object_id, c.column_id
            """, r => new ColumnRow(
                r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetInt16(6), r.GetByte(7), r.GetByte(8),
                NullableString(r, 9), r.GetBoolean(10), r.GetBoolean(11), NullableString(r, 12), NullableString(r, 13), r.GetBoolean(14),
                NullableString(r, 15), r.GetBoolean(16), r.GetBoolean(17), r.GetBoolean(18), r.GetBoolean(19),
                NullableString(r, 20), NullableString(r, 21), r.GetBoolean(22))),
        Indexes = Read(connection, """
            SELECT i.object_id, i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint,
                   ISNULL(kc.is_system_named, 0), i.ignore_dup_key, i.fill_factor, i.is_padded, i.allow_row_locks, i.allow_page_locks,
                   i.filter_definition, i.is_disabled, ISNULL(st.no_recompute, 0), p.data_compression_desc,
                   CAST(CASE WHEN ds.type = 'PS' THEN 1 ELSE 0 END AS bit)
            FROM sys.indexes AS i
            JOIN sys.objects AS o ON o.object_id = i.object_id
            LEFT JOIN sys.key_constraints AS kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
                AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
            LEFT JOIN sys.stats AS st ON st.object_id = i.object_id AND st.stats_id = i.index_id
            LEFT JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
            LEFT JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
            WHERE (o.is_ms_shipped = 0 OR o.type = 'TT') AND o.type IN ('U', 'V', 'TT') AND i.is_hypothetical = 0
            """, r => new IndexRow(
                r.GetInt32(0), r.GetInt32(1), NullableString(r, 2), r.GetByte(3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6),
                r.GetBoolean(7), r.GetBoolean(8), r.GetByte(9), r.GetBoolean(10), r.GetBoolean(11), r.GetBoolean(12),
                NullableString(r, 13), r.GetBoolean(14), r.GetBoolean(15), NullableString(r, 16), r.GetBoolean(17))),
        IndexColumns = Read(connection, """
            SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column, ic.index_column_id
            FROM sys.index_columns AS ic
            JOIN sys.objects AS o ON o.object_id = ic.object_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE (o.is_ms_shipped = 0 OR o.type = 'TT') AND o.type IN ('U', 'V', 'TT')
            """, r => new IndexColumnRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetByte(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt32(6))),
        Statistics = Read(connection, """
            SELECT st.object_id, st.stats_id, st.name, st.filter_definition, st.no_recompute
            FROM sys.stats AS st JOIN sys.objects AS o ON o.object_id = st.object_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V') AND st.user_created = 1
            """, r => new StatisticsRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), NullableString(r, 3), r.GetBoolean(4))),
        StatisticsColumns = Read(connection, """
            SELECT sc.object_id, sc.stats_id, sc.stats_column_id, c.name
            FROM sys.stats_columns AS sc
            JOIN sys.stats AS st ON st.object_id = sc.object_id AND st.stats_id = sc.stats_id
            JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
            WHERE st.user_created = 1
            """, r => new StatisticsColumnRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3))),
        ForeignKeys = Read(connection, """
            SELECT fk.object_id, fk.name, fk.parent_object_id, fk.referenced_object_id, fk.delete_referential_action,
                   fk.update_referential_action, fk.is_not_for_replication, fk.is_disabled, fk.is_not_trusted, fk.is_system_named
            FROM sys.foreign_keys AS fk WHERE fk.is_ms_shipped = 0
            """, r => new ForeignKeyRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetByte(4), r.GetByte(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9))),
        ForeignKeyColumns = Read(connection, """
            SELECT fkc.constraint_object_id, fkc.constraint_column_id, pc.name, rc.name
            FROM sys.foreign_key_columns AS fkc
            JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            """, r => new ForeignKeyColumnRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3))),
        Checks = Read(connection, """
            SELECT cc.object_id, cc.name, cc.parent_object_id, cc.definition, cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication, cc.is_system_named
            FROM sys.check_constraints AS cc WHERE cc.is_ms_shipped = 0 OR cc.parent_object_id IN (SELECT tt.type_table_object_id FROM sys.table_types AS tt)
            """, r => new CheckRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7))),
        Modules = Read(connection, """
            SELECT m.object_id, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier, ISNULL(tr.is_disabled, 0), m.uses_native_compilation
            FROM sys.sql_modules AS m
            JOIN sys.objects AS o ON o.object_id = m.object_id
            LEFT JOIN sys.triggers AS tr ON tr.object_id = m.object_id
            WHERE o.is_ms_shipped = 0
            """, r => new ModuleRow(r.GetInt32(0), NullableString(r, 1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5))),
        Types = Read(connection, """
            SELECT t.user_type_id, s.name, t.name, ISNULL(bt.name, N''), t.max_length, t.precision, t.scale, t.is_nullable,
                   t.is_table_type, ISNULL(tt.type_table_object_id, 0), t.is_assembly_type, ISNULL(tt.is_memory_optimized, 0)
            FROM sys.types AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            LEFT JOIN sys.types AS bt ON bt.user_type_id = t.system_type_id AND t.is_table_type = 0
            LEFT JOIN sys.table_types AS tt ON tt.user_type_id = t.user_type_id
            WHERE t.is_user_defined = 1
            """, r => new TypeRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6), r.GetBoolean(7), r.GetBoolean(8), r.GetInt32(9), r.GetBoolean(10), r.GetBoolean(11))),
        Synonyms = Read(connection, "SELECT object_id, base_object_name FROM sys.synonyms WHERE is_ms_shipped = 0", r => (r.GetInt32(0), r.GetString(1))),
        Sequences = Read(connection, """
            SELECT sq.object_id, t.name, sq.precision, CAST(sq.start_value AS nvarchar(64)), CAST(sq.increment AS nvarchar(64)),
                   CAST(sq.minimum_value AS nvarchar(64)), CAST(sq.maximum_value AS nvarchar(64)), sq.is_cycling, sq.is_cached, sq.cache_size
            FROM sys.sequences AS sq JOIN sys.types AS t ON t.user_type_id = sq.user_type_id
            WHERE sq.is_ms_shipped = 0
            """, r => new SequenceRow(r.GetInt32(0), r.GetString(1), r.GetByte(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetInt32(9))),
        Principals = Read(connection, """
            SELECT p.principal_id, p.name, RTRIM(p.type), p.owning_principal_id, p.default_schema_name, p.authentication_type,
                   SUSER_SNAME(p.sid), p.is_fixed_role
            FROM sys.database_principals AS p
            """, r => new PrincipalRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), NullableString(r, 4), r.GetInt32(5), NullableString(r, 6), r.GetBoolean(7))),
        RoleMembers = Read(connection, "SELECT role_principal_id, member_principal_id FROM sys.database_role_members", r => (r.GetInt32(0), r.GetInt32(1))),
        Permissions = Read(connection, """
            SELECT p.class, p.major_id, p.minor_id, p.grantee_principal_id, p.permission_name, p.state,
                   CASE WHEN p.class = 1 AND p.minor_id > 0 THEN COL_NAME(p.major_id, p.minor_id) END, p.grantor_principal_id
            FROM sys.database_permissions AS p
            WHERE p.state IN ('G', 'D', 'W')
            """, r => new PermissionRow(r.GetByte(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetString(5), NullableString(r, 6), r.GetInt32(7))),
        ExtendedProperties = Read(connection, """
            SELECT ep.class, ep.major_id, ep.minor_id, ep.name, CAST(ep.value AS nvarchar(max)),
                   CAST(SQL_VARIANT_PROPERTY(ep.value, 'BaseType') AS nvarchar(128)),
                   CASE ep.class
                       WHEN 1 THEN COL_NAME(ep.major_id, ep.minor_id)
                       WHEN 2 THEN (SELECT pa.name FROM sys.parameters AS pa WHERE pa.object_id = ep.major_id AND pa.parameter_id = ep.minor_id)
                       WHEN 7 THEN (SELECT ix.name FROM sys.indexes AS ix WHERE ix.object_id = ep.major_id AND ix.index_id = ep.minor_id)
                   END
            FROM sys.extended_properties AS ep
            """, r => new ExtendedPropertyRow(r.GetByte(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), NullableString(r, 4), NullableString(r, 5), NullableString(r, 6))),
        DataSources = Read(connection, """
            SELECT data_source_id, name, location, type_desc, database_name, shard_map_name, credential_id
            FROM sys.external_data_sources
            """, r => new DataSourceRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), NullableString(r, 4), NullableString(r, 5), r.GetInt32(6))),
        Credentials = Read(connection, "SELECT credential_id, name, credential_identity FROM sys.database_scoped_credentials", r => new CredentialRow(r.GetInt32(0), r.GetString(1), r.GetString(2))),
        ExternalTables = Read(connection, """
            SELECT object_id, data_source_id, location, file_format_id, remote_schema_name, remote_object_name, ISNULL(distribution_type, 255)
            FROM sys.external_tables
            """, r => new ExternalTableRow(r.GetInt32(0), r.GetInt32(1), NullableString(r, 2), r.GetInt32(3), NullableString(r, 4), NullableString(r, 5), r.GetByte(6))),
        Dependencies = Read(connection, """
            SELECT DISTINCT d.referencing_id, d.referenced_id, CAST(CASE WHEN d.referenced_class = 6 THEN 1 ELSE 0 END AS bit)
            FROM sys.sql_expression_dependencies AS d
            WHERE d.referencing_class = 1 AND d.referenced_class IN (1, 6) AND d.referenced_id IS NOT NULL
              AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL
            """, r => new DependencyRow(r.GetInt32(0), r.GetInt32(1), r.GetBoolean(2))),
        ParameterTypes = Read(connection, """
            SELECT DISTINCT pa.object_id, pa.user_type_id
            FROM sys.parameters AS pa JOIN sys.types AS t ON t.user_type_id = pa.user_type_id
            WHERE t.is_user_defined = 1
            """, r => (r.GetInt32(0), r.GetInt32(1))),
        UnsupportedFeatures = Read(connection, """
            SELECT feature, n FROM (VALUES
                (N'assemblies', (SELECT COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1)),
                (N'XML schema collections', (SELECT COUNT(*) FROM sys.xml_schema_collections WHERE schema_id <> 4)),
                (N'partition functions', (SELECT COUNT(*) FROM sys.partition_functions)),
                (N'full-text catalogs', (SELECT COUNT(*) FROM sys.fulltext_catalogs)),
                (N'security policies', (SELECT COUNT(*) FROM sys.security_policies)),
                (N'column master keys', (SELECT COUNT(*) FROM sys.column_master_keys)),
                (N'certificates', (SELECT COUNT(*) FROM sys.certificates WHERE name NOT LIKE N'##%')),
                (N'asymmetric keys', (SELECT COUNT(*) FROM sys.asymmetric_keys WHERE name NOT LIKE N'##%')),
                (N'symmetric keys', (SELECT COUNT(*) FROM sys.symmetric_keys WHERE name NOT LIKE N'##%')),
                (N'database DDL triggers', (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0)),
                (N'external file formats', (SELECT COUNT(*) FROM sys.external_file_formats)),
                (N'rules and bound defaults', (SELECT COUNT(*) FROM sys.objects WHERE type IN ('R', 'D') AND parent_object_id = 0 AND is_ms_shipped = 0)),
                (N'Service Broker queues', (SELECT COUNT(*) FROM sys.service_queues WHERE is_ms_shipped = 0)),
                (N'plan guides', (SELECT COUNT(*) FROM sys.plan_guides))
            ) AS f(feature, n)
            WHERE n > 0
            """, r => (r.GetString(0), r.GetInt32(1))),
    };

    private static string? NullableString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static List<T> Read<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> map)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
