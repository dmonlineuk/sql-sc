using SqlSc.Core.Database;
using SqlSc.Core.Settings;

namespace SqlSc.Web.Services;

/// <summary>
/// State shared by every page for the life of the process: the selected working folder, SQL-auth passwords typed into the UI
/// (kept in memory only, never written anywhere) and a lock so only one database operation runs at a time.
/// </summary>
public sealed class UiState(string? startFolder) : IDisposable
{
    private readonly Dictionary<string, string> passwords = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim busy = new(1, 1);

    public string? Folder { get; set; } = startFolder is null ? null : Path.GetFullPath(startFolder);

    public LinkStore Links { get; init; } = LinkStore.Default;

    public Link? CurrentLink => Folder is null ? null : Links.FindFor(Folder);

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public void RememberPassword(string folder, string? password)
    {
        lock (passwords)
        {
            if (string.IsNullOrEmpty(password))
            {
                passwords.Remove(Path.GetFullPath(folder));
            }
            else
            {
                passwords[Path.GetFullPath(folder)] = password;
            }
        }
    }

    public bool HasPassword(string folder)
    {
        lock (passwords)
        {
            return passwords.ContainsKey(Path.GetFullPath(folder));
        }
    }

    /// <summary>The linked connection string with any remembered or <c>SQLSC_PASSWORD</c> password added.</summary>
    public string? ConnectionFor(Link link)
    {
        string? password;
        lock (passwords)
        {
            password = passwords.GetValueOrDefault(Path.GetFullPath(link.Folder));
        }

        return ConnectionStrings.WithPassword(link.Connection, password ?? Environment.GetEnvironmentVariable(ConnectionStrings.PasswordVariable));
    }

    public void Dispose() => busy.Dispose();

    /// <summary>Runs <paramref name="work"/> on a background thread, one operation at a time.</summary>
    public async Task<T> RunAsync<T>(Func<T> work)
    {
        await busy.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            busy.Release();
        }
    }
}
