using Microsoft.SqlServer.Dac.Model;

namespace SqlSc.Core.Modeling;

internal static class ModuleScripts
{
    /// <summary>
    /// Replaces each module's script with <paramref name="rewrite"/>(module, script, quotedIdentifier), where the module is
    /// the only object from its source. Returning the same string instance leaves the module alone.
    /// </summary>
    public static void Rewrite(TSqlModel model, Func<TSqlObject, string, bool, string> rewrite)
    {
        var topLevel = model.GetObjects(DacQueryScopes.UserDefined).ToList();
        var perSource = topLevel
            .Select(o => o.GetSourceInformation()?.SourceName)
            .OfType<string>()
            .GroupBy(s => s, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var module in topLevel)
        {
            var options = module.ObjectType.Name switch
            {
                "View" => Options(module, View.QuotedIdentifierOn, View.AnsiNullsOn),
                "Procedure" => Options(module, Procedure.QuotedIdentifierOn, Procedure.AnsiNullsOn),
                "ScalarFunction" => Options(module, ScalarFunction.QuotedIdentifierOn, ScalarFunction.AnsiNullsOn),
                "TableValuedFunction" => Options(module, TableValuedFunction.QuotedIdentifierOn, TableValuedFunction.AnsiNullsOn),
                "DmlTrigger" => Options(module, DmlTrigger.QuotedIdentifierOn, DmlTrigger.AnsiNullsOn),
                _ => null,
            };
            if (options is null
                || module.GetSourceInformation()?.SourceName is not { } source
                || perSource[source] != 1
                || !module.TryGetScript(out var script))
            {
                continue;
            }

            var rewritten = rewrite(module, script, options.QuotedIdentifier ?? true);
            if (!ReferenceEquals(rewritten, script))
            {
                model.AddOrUpdateObjects(rewritten, source, options);
            }
        }
    }

    private static TSqlObjectOptions Options(TSqlObject module, ModelPropertyClass quotedIdentifier, ModelPropertyClass ansiNulls) => new()
    {
        QuotedIdentifier = module.GetProperty<bool?>(quotedIdentifier) ?? true,
        AnsiNulls = module.GetProperty<bool?>(ansiNulls) ?? true,
    };
}
