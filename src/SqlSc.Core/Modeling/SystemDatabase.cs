using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace SqlSc.Core.Modeling;

/// <summary>
/// Gives models a reference to Microsoft's master.dacpac, as SQL projects have. Without it, views and functions that read
/// sys.* or INFORMATION_SCHEMA.* have unresolved references, and DacFx refuses to save the model as a .dacpac.
/// TSqlModel has no API for adding a reference, so it is written into a package's model.xml header, where a SQL
/// project build puts it, and the model is loaded from that package.
/// </summary>
public static class SystemDatabase
{
    private static readonly Lazy<string> SqlServerMaster = new(() => Extract("SqlSc.Core.master.dacpac"));
    private static readonly Lazy<string> AzureMaster = new(() => Extract("SqlSc.Core.azure-master.dacpac"));

    /// <summary>An empty, script-backed model that references master.dacpac.</summary>
    public static TSqlModel CreateModel(SqlServerVersion platform, TSqlModelOptions options) =>
        WithPackage(path =>
        {
            using (var empty = new TSqlModel(platform, options))
            {
                DacPackageExtensions.BuildPackage(path, empty, new PackageMetadata { Name = "sql-sc" });
            }

            return Load(path, platform);
        });

    /// <summary>Extracts a database into a script-backed model that references master.dacpac.</summary>
    public static TSqlModel LoadFromDatabase(string connectionString, string databaseName, SqlServerVersion platform, DacExtractOptions options) =>
        WithPackage(path =>
        {
            new DacServices(connectionString).Extract(path, databaseName, "sql-sc", new Version(1, 0), extractOptions: options);
            return Load(path, platform);
        });

    /// <summary>
    /// Adds a login for every user whose login the model lacks. Azure SQL Database models never contain logins (they live in
    /// the logical server's master), and a user with an unresolved login stops the model being saved as a .dacpac.
    /// </summary>
    public static void AddMissingLogins(TSqlModel model)
    {
        var logins = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.User)
            .SelectMany(user => user.GetReferencedRelationshipInstances(User.Login, DacQueryScopes.All))
            .Where(reference => reference.Object is null && reference.ObjectName is { HasName: true, Parts.Count: 1 })
            .Select(reference => reference.ObjectName.Parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var login in logins)
        {
            model.AddOrUpdateObjects($"CREATE LOGIN [{login.Replace("]", "]]", StringComparison.Ordinal)}] FROM EXTERNAL PROVIDER", "login:" + login, new TSqlObjectOptions());
        }
    }

    /// <summary>
    /// DacFx won't save a database scoped credential without a master key, and its extract never includes the master key,
    /// so a model with credentials gets one (passwordless, which is enough for modelling; never reported).
    /// </summary>
    public static void AddMissingMasterKey(TSqlModel model)
    {
        if (model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DatabaseCredential).Any()
            && !model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.MasterKey).Any())
        {
            model.AddOrUpdateObjects("CREATE MASTER KEY", "masterkey:", new TSqlObjectOptions());
        }
    }

    internal static string MasterPath(SqlServerVersion platform) =>
        (platform == SqlServerVersion.SqlAzure ? AzureMaster : SqlServerMaster).Value;

    private static TSqlModel WithPackage(Func<string, TSqlModel> create)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sql-sc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            return create(Path.Combine(directory, "model.dacpac"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TSqlModel Load(string path, SqlServerVersion platform)
    {
        AddReference(path, MasterPath(platform));
        return TSqlModel.LoadFromDacpac(path, new ModelLoadOptions(DacSchemaModelStorageType.Memory, loadAsScriptBackedModel: true));
    }

    private static void AddReference(string packagePath, string masterPath)
    {
        using var package = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        var model = Read(package, "model.xml");
        var ns = model.Root!.Name.Namespace;
        var header = model.Root.Element(ns + "Header");
        if (header is null)
        {
            header = new XElement(ns + "Header");
            model.Root.AddFirst(header);
        }

        header.Add(new XElement(
            ns + "CustomData",
            new XAttribute("Category", "Reference"),
            new XAttribute("Type", "SqlSchema"),
            Metadata(ns, "FileName", masterPath),
            Metadata(ns, "LogicalName", "master.dacpac"),
            Metadata(ns, "ExternalParts", "[master]"),
            Metadata(ns, "SuppressMissingDependenciesErrors", "False")));
        var modelBytes = Write(package, "model.xml", model);

        var origin = Read(package, "Origin.xml");
        foreach (var checksum in origin.Descendants().Where(e => e.Name.LocalName == "Checksum" && (string?)e.Attribute("Uri") == "/model.xml"))
        {
            checksum.Value = Convert.ToHexString(SHA256.HashData(modelBytes));
        }

        Write(package, "Origin.xml", origin);
    }

    private static XElement Metadata(XNamespace ns, string name, string value) =>
        new(ns + "Metadata", new XAttribute("Name", name), new XAttribute("Value", value));

    private static XDocument Read(ZipArchive package, string name)
    {
        using var stream = package.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static byte[] Write(ZipArchive package, string name, XDocument document)
    {
        using var buffer = new MemoryStream();
        document.Save(buffer);
        var bytes = buffer.ToArray();
        package.GetEntry(name)!.Delete();
        using var stream = package.CreateEntry(name).Open();
        stream.Write(bytes);
        return bytes;
    }

    /// <summary>Writes an embedded master.dacpac to a per-version folder under the temp directory, once.</summary>
    private static string Extract(string resource)
    {
        using var source = typeof(SystemDatabase).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"sql-sc was built without {resource}.");
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "sql-sc", Convert.ToHexString(SHA256.HashData(bytes))[..16]);
        var path = Path.Combine(directory, "master.dacpac");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporary);
            }
        }

        return path;
    }
}
