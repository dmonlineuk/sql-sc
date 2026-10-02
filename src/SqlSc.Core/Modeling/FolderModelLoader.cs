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
        var model = new TSqlModel(target, new TSqlModelOptions
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
            try
            {
                model.AddOrUpdateObjects(
                    batchText,
                    ScriptSource.Encode(file, batch.StartLine),
                    new TSqlObjectOptions { QuotedIdentifier = quotedIdentifier, AnsiNulls = ansiNulls });
            }
            catch (DacModelException ex)
            {
                issues.AddRange(ex.Messages.Count > 0
                    ? ex.Messages.Select(m => new LoadIssue(IssueSeverity.Error, file, batch.StartLine, $"{m.Prefix}{m.Number}: {m.Message}"))
                    : [new LoadIssue(IssueSeverity.Error, file, batch.StartLine, ex.Message)]);
            }
        }
    }
}
