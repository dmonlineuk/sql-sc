using System.Data.SqlTypes;
using System.Text;
using SqlSc.Core.StaticData;
using SqlSc.Core.WorkingFolders;

namespace SqlSc.Core.Tests;

public class StaticDataTests
{
    [Fact]
    public void RetargetPointsInsertsAtTheTempTableAndDropsIdentityInsert()
    {
        var statements = DataScript.Retarget(
            File.ReadAllText(Path.Combine(FixturePaths.Demo, "Data", "Sales.Customer_Data.sql")),
            "Sales",
            "Customer",
            "#t");

        Assert.Equal(
            [
                "INSERT INTO #t ([CustomerId], [Name], [IsActive]) VALUES (1, N'Contoso', 1)",
                "INSERT INTO #t ([CustomerId], [Name], [IsActive]) VALUES (2, N'Fabrikam', 0)",
            ],
            statements);
    }

    [Fact]
    public void RetargetUsesTheDefaultSchemaAndRejectsOtherStatements()
    {
        Assert.Equal(["INSERT #t VALUES ('[Lookup]')"], DataScript.Retarget("-- rows\nINSERT Lookup VALUES ('[Lookup]')\nGO\n", "dbo", "lookup", "#t"));
        Assert.Throws<InvalidDataException>(() => DataScript.Retarget("INSERT INTO other.Lookup VALUES (1)", "dbo", "Lookup", "#t"));
        Assert.Throws<InvalidDataException>(() => DataScript.Retarget("DELETE FROM dbo.Lookup", "dbo", "Lookup", "#t"));
        Assert.Throws<InvalidDataException>(() => DataScript.Retarget("INSERT INTO dbo.Lookup VALUES (", "dbo", "Lookup", "#t"));
    }

    [Theory]
    [InlineData("int", 0, 42, "42")]
    [InlineData("bit", 0, true, "1")]
    [InlineData("float", 0, 0.1, "0.1E0")]
    [InlineData("float", 0, 1e20, "1E+20")]
    [InlineData("varchar", 0, "it's", "'it''s'")]
    [InlineData("nvarchar", 0, "ü", "N'ü'")]
    [InlineData("xml", 0, "<a/>", "N'<a/>'")]
    public void LiteralsAreLanguageIndependent(string type, int scale, object value, string expected) =>
        Assert.Equal(expected, DataScript.Literal(value, Column(type, scale)));

    [Fact]
    public void DateAndBinaryLiteralsKeepTheirPrecision()
    {
        var at = new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1234567);
        Assert.Equal("'2024-01-02 03:04:05.1234567'", DataScript.Literal(at, Column("datetime2", 7)));
        Assert.Equal("'2024-01-02 03:04:05.12'", DataScript.Literal(at, Column("datetime2", 2)));
        Assert.Equal("'2024-01-02 03:04:05'", DataScript.Literal(at, Column("datetime2", 0)));
        Assert.Equal("'2024-01-02T03:04:05.123'", DataScript.Literal(at, Column("datetime", 3)));
        Assert.Equal("'2024-01-02'", DataScript.Literal(at.Date, Column("date", 0)));
        Assert.Equal("'2024-01-02 03:04:05.123 -08:00'", DataScript.Literal(new DateTimeOffset(at, TimeSpan.FromHours(-8)), Column("datetimeoffset", 3)));
        Assert.Equal("'13:14:15.1234'", DataScript.Literal(new TimeSpan(0, 13, 14, 15).Add(TimeSpan.FromTicks(1234000)), Column("time", 4)));
        Assert.Equal("0x00FF", DataScript.Literal(new byte[] { 0, 255 }, Column("varbinary", 0)));
        Assert.Equal("CAST(0x5B40 AS hierarchyid)", DataScript.Literal(new byte[] { 0x5B, 0x40 }, Column("hierarchyid", 0)));
        Assert.Equal("12345678901234567890123456789.123456789", DataScript.Literal(SqlDecimal.Parse("12345678901234567890123456789.123456789"), Column("decimal", 9)));
        Assert.Equal("NULL", DataScript.Literal(null, Column("int", 0)));
    }

    [Fact]
    public void WriteSortsNothingButWrapsIdentityTablesInIdentityInsert()
    {
        var table = new DataTable("Sales", "Customer", [Column("int", 0, "Id", identity: true), Column("nvarchar", 0, "Name"), Column("int", 0, "Upper", insertable: false)], [Column("int", 0, "Id", identity: true)], 2);

        Assert.Equal(
            "SET IDENTITY_INSERT [Sales].[Customer] ON\r\n"
                + "INSERT INTO [Sales].[Customer] ([Id], [Name]) VALUES (1, N'Contoso')\r\n"
                + "INSERT INTO [Sales].[Customer] ([Id], [Name]) VALUES (2, NULL)\r\n"
                + "SET IDENTITY_INSERT [Sales].[Customer] OFF\r\n",
            DataScript.Write(table, [[1, "Contoso"], [2, null]]));
        Assert.Equal("", DataScript.Write(table, []));
    }

    [Theory]
    [InlineData("Sales.Customer_Data.sql", "Sales", "Customer")]
    [InlineData("dbo.My.Table_Data.sql", "dbo", "My.Table")]
    [InlineData("Customer_Data.sql", null, null)]
    [InlineData("Sales.Customer.sql", null, null)]
    public void DataFileNamesGiveTheirTable(string file, string? schema, string? table) =>
        Assert.Equal(schema is null ? null : (schema, table!), StaticDataService.TableOf(file));

    [Fact]
    public void SetDataFilesRewritesOnlyTheDataFileSet()
    {
        var root = FixturePaths.CreateTempFolder();
        var path = Path.Combine(root, RedgateDatabaseInfo.FileName);
        var original = File.ReadAllText(Path.Combine(FixturePaths.Demo, RedgateDatabaseInfo.FileName));
        File.WriteAllText(path, original, new UTF8Encoding(true));

        RedgateDatabaseInfo.SetDataFiles(path, ["Sales.Customer_Data.sql", "dbo.Lookup_Data.sql"]);

        var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var updated = File.ReadAllText(path);
        Assert.Equal(updated.ReplaceLineEndings(newLine), updated);
        updated = updated.ReplaceLineEndings("\n");
        Assert.Equal(["dbo.Lookup_Data.sql", "Sales.Customer_Data.sql"], RedgateDatabaseInfo.Load(path).DataFiles);
        Assert.Contains("<DataFileSet>\n    <Count>2</Count>\n    <DataFile>dbo.Lookup_Data.sql</DataFile>\n    <DataFile>Sales.Customer_Data.sql</DataFile>\n  </DataFileSet>", updated, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), File.ReadAllBytes(path)[..3]);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>\n<DatabaseInformation Version=\"2\">\n  <ScriptFileEncoding>", updated, StringComparison.Ordinal);

        RedgateDatabaseInfo.SetDataFiles(path, ["Sales.Customer_Data.sql"]);

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void SetDataFilesCreatesAMissingFile()
    {
        var path = Path.Combine(FixturePaths.CreateTempFolder(), RedgateDatabaseInfo.FileName);

        RedgateDatabaseInfo.SetDataFiles(path, ["dbo.Lookup_Data.sql"]);

        var info = RedgateDatabaseInfo.Load(path);
        Assert.Equal(["dbo.Lookup_Data.sql"], info.DataFiles);
        Assert.Equal("Data", info.DataFolder);
    }

    private static DataColumn Column(string type, int scale, string name = "c", bool identity = false, bool insertable = true) =>
        new(name, type, 0, scale, identity, insertable);
}
