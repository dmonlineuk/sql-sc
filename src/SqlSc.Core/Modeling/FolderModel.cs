using System.Globalization;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace SqlSc.Core.Modeling;

public sealed class FolderModel(TSqlModel model, SqlServerVersion platform, int fileCount, IReadOnlyList<LoadIssue> issues) : IDisposable
{
    public TSqlModel Model { get; } = model;

    public SqlServerVersion Platform { get; } = platform;

    public int FileCount { get; } = fileCount;

    public IReadOnlyList<LoadIssue> Issues { get; } = issues;

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    public int ObjectCount => Model.GetObjects(DacQueryScopes.UserDefined).Count();

    /// <summary>Writes the model to a .dacpac so it can be used as a schema compare endpoint.</summary>
    public void BuildPackage(string path, string name)
    {
        DacPackageExtensions.BuildPackage(path, Model, new PackageMetadata { Name = name }, new PackageOptions
        {
            IgnoreValidationErrors = FolderModelLoader.UnresolvedReferenceCodes
                .Select(code => string.Create(CultureInfo.InvariantCulture, $"{code}"))
                .ToList(),
        });
    }

    public void Dispose() => Model.Dispose();
}
