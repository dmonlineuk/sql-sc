using SqlSc.Core.Modeling;

namespace SqlSc.Core.Tests;

public class TableTypeCollationsTests
{
    [Theory]
    [InlineData(
        "CREATE TYPE [dbo].[T] AS TABLE\n(\n[A] [varchar] (3) COLLATE Latin1_General_CI_AS NOT NULL,\n[B] [sys].[sysname] NULL\n)",
        "CREATE TYPE [dbo].[T] AS TABLE\n(\n[A] [varchar] (3) NOT NULL,\n[B] [sys].[sysname] NULL\n)")]
    [InlineData(
        "CREATE TYPE [dbo].[T] AS TABLE ([A] [varchar] (3) COLLATE Latin1_General_BIN NOT NULL)",
        "CREATE TYPE [dbo].[T] AS TABLE ([A] [varchar] (3) COLLATE Latin1_General_BIN NOT NULL)")]
    [InlineData(
        "CREATE TABLE [dbo].[T] ([A] [varchar] (3) COLLATE Latin1_General_CI_AS NOT NULL)",
        "CREATE TABLE [dbo].[T] ([A] [varchar] (3) COLLATE Latin1_General_CI_AS NOT NULL)")]
    public void DefaultCollationsAreRemovedFromTableTypeColumns(string script, string expected) =>
        Assert.Equal(expected, TableTypeCollations.Rewrite(script, "Latin1_General_CI_AS"));
}
