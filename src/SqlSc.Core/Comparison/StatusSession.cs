using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;

namespace SqlSc.Core.Comparison;

/// <summary>A status comparison with the two models it was made from and their packages, which are deleted on dispose.</summary>
internal sealed class StatusSession(FolderModel folder, DatabaseModel database, StatusReport report, string workDirectory) : IDisposable
{
    public FolderModel Folder { get; } = folder;

    public DatabaseModel Database { get; } = database;

    public StatusReport Report { get; } = report;

    /// <summary>The database model as a .dacpac.</summary>
    public string DatabasePackage { get; } = Path.Combine(workDirectory, StatusService.DatabasePackageName);

    /// <summary>The folder model, with the objects copied from the database, as a .dacpac.</summary>
    public string FolderPackage { get; } = Path.Combine(workDirectory, StatusService.FolderPackageName);

    public void Dispose()
    {
        Database.Dispose();
        Folder.Dispose();
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }
}
