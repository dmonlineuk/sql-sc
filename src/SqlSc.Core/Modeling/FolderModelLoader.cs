using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Modeling;

/// <summary>
/// Loads every schema script in a working folder into an in-memory DacFx model.
/// Files are split into GO batches with ScriptDom; SET QUOTED_IDENTIFIER / SET ANSI_NULLS batches
/// are tracked per file and applied to the batches that follow them, as SQL Server would.
/// </summary>
public static class FolderModelLoader
{
    /// <summary>
    /// Unresolved-reference codes. In database-first work the live database is the source of truth and a working folder
    /// is often partial (filtered objects, cross-database or server-level references), so these are reported as warnings.
    /// </summary>
    internal static readonly IReadOnlySet<int> UnresolvedReferenceCodes = new HashSet<int> { 71501, 71502, 71561, 71562 };

    public static FolderModel Load(WorkingFolder folder, SqlServerVersion? platform = null)
    {
        var target = platform ?? TargetPlatform.FromRedgateInfo(folder.RedgateInfo);
        var model = SystemDatabase.CreateModel(target, new TSqlModelOptions
        {
            Collation = folder.RedgateInfo?.DefaultCollation,
        });

        var issues = new List<LoadIssue>();
        var files = folder.GetSchemaScripts();
        foreach (var file in files)
        {
            LoadFile(model, file, folder.ReadScript(file), issues);
        }

        var unlocated = model.Validate();
        var located = model.GetModelErrors().ToList();
        foreach (var error in located)
        {
            var source = ScriptSource.Decode(error.SourceName);
            issues.Add(new LoadIssue(
                error.Severity == ModelErrorSeverity.Error && !UnresolvedReferenceCodes.Contains(error.ErrorCode) ? IssueSeverity.Error : IssueSeverity.Warning,
                source?.File,
                source is { } s ? s.StartLine + error.Line - 1 : null,
                $"{error.Prefix}{error.ErrorCode}: {error.Message}"));
        }

        if (located.Count == 0)
        {
            issues.AddRange(unlocated
                .Where(m => m.MessageType != DacMessageType.Message)
                .Select(m => new LoadIssue(
                    m.MessageType == DacMessageType.Error && !UnresolvedReferenceCodes.Contains(m.Number) ? IssueSeverity.Error : IssueSeverity.Warning,
                    null,
                    null,
                    $"{m.Prefix}{m.Number}: {m.Message}")));
        }

        return new FolderModel(model, target, files.Count, issues);
    }

    private static void LoadFile(TSqlModel model, string file, string text, List<LoadIssue> issues)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        using (var reader = new StringReader(text))
        {
            fragment = parser.Parse(reader, out var parseErrors);
            if (parseErrors.Count > 0)
            {
                issues.AddRange(parseErrors.Select(e => new LoadIssue(IssueSeverity.Error, file, e.Line, e.Message)));
                return;
            }
        }

        if (fragment is not TSqlScript script)
        {
            return;
        }

        bool? quotedIdentifier = null;
        bool? ansiNulls = null;
        var groups = new List<BatchGroup>();
        var definedIn = new Dictionary<string, BatchGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in script.Batches)
        {
            if (batch.Statements.Count > 0 && batch.Statements.All(s => s is PredicateSetStatement))
            {
                foreach (var set in batch.Statements.Cast<PredicateSetStatement>())
                {
                    if (set.Options.HasFlag(SetOptions.QuotedIdentifier))
                    {
                        quotedIdentifier = set.IsOn;
                    }

                    if (set.Options.HasFlag(SetOptions.AnsiNulls))
                    {
                        ansiNulls = set.IsOn;
                    }
                }

                continue;
            }

            var batchText = text.Substring(batch.StartOffset, batch.FragmentLength);
            if (groups.Count > 0 && SupportedNames(batch) is { } names)
            {
                var owner = names.Count > 0 && names.All(definedIn.ContainsKey) && names.Select(n => definedIn[n]).Distinct().Count() == 1
                    ? definedIn[names[0]]
                    : groups[^1];
                owner.Texts.Add(batchText);
                continue;
            }

            var group = new BatchGroup(batch.StartLine, quotedIdentifier, ansiNulls, [batchText]);
            groups.Add(group);
            var collector = new DefinedNameCollector();
            batch.Accept(collector);
            foreach (var name in collector.Names)
            {
                definedIn[name] = group;
            }
        }

        foreach (var group in groups)
        {
            try
            {
                model.AddOrUpdateObjects(
                    ColumnNamedAliases.Rewrite(string.Join("\nGO\n", group.Texts), group.QuotedIdentifier ?? true),
                    ScriptSource.Encode(file, group.StartLine),
                    new TSqlObjectOptions { QuotedIdentifier = group.QuotedIdentifier, AnsiNulls = group.AnsiNulls });
            }
            catch (DacModelException ex)
            {
                issues.AddRange(ex.Messages.Count > 0
                    ? ex.Messages.Select(m => new LoadIssue(IssueSeverity.Error, file, group.StartLine, $"{m.Prefix}{m.Number}: {m.Message}"))
                    : [new LoadIssue(IssueSeverity.Error, file, group.StartLine, ex.Message)]);
            }
        }
    }

    /// <summary>
    /// The constraint or trigger names a batch of only <c>ALTER TABLE ... [NO]CHECK CONSTRAINT</c> or <c>ENABLE/DISABLE TRIGGER</c>
    /// statements refers to, or null for any other batch. DacFx only applies these statements when they are added in the same
    /// call as the object they modify, so they join the batch that defines it.
    /// </summary>
    private static List<string>? SupportedNames(TSqlBatch batch)
    {
        var names = new List<string>();
        foreach (var statement in batch.Statements)
        {
            switch (statement)
            {
                case AlterTableConstraintModificationStatement constraint:
                    names.AddRange(constraint.ConstraintNames.Select(n => n.Value));
                    break;
                case EnableDisableTriggerStatement trigger:
                    names.AddRange(trigger.TriggerNames.Select(n => n.BaseIdentifier.Value));
                    break;
                default:
                    return null;
            }
        }

        return batch.Statements.Count > 0 ? names : null;
    }

    private sealed record BatchGroup(int StartLine, bool? QuotedIdentifier, bool? AnsiNulls, List<string> Texts);

    private sealed class DefinedNameCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];

        public override void Visit(ConstraintDefinition node)
        {
            if (node.ConstraintIdentifier is { } name)
            {
                Names.Add(name.Value);
            }
        }

        public override void Visit(TriggerStatementBody node)
        {
            if (node.Name?.BaseIdentifier is { } name)
            {
                Names.Add(name.Value);
            }
        }
    }
}
