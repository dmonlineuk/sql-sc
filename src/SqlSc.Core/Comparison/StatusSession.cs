using SqlSc.Core.Modeling;
using SqlSc.Core.Scripting;

namespace SqlSc.Core.Comparison;

/// <summary>A status comparison with the two models it was made from.</summary>
internal sealed class StatusSession(FolderModel folder, DatabaseModel database, StatusReport report) : IDisposable
{
    public FolderModel Folder { get; } = folder;

    public DatabaseModel Database { get; } = database;

    public StatusReport Report { get; } = report;

    public void Dispose()
    {
        Database.Dispose();
        Folder.Dispose();
    }
}
