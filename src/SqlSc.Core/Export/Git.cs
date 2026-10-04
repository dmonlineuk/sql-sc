using System.Diagnostics;

namespace SqlSc.Core.Export;

/// <summary>Runs the git command line in a folder, with the user's own git configuration and credentials.</summary>
internal static class Git
{
    public static (int ExitCode, string Output, string Error) Run(string folder, params IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidDataException("Couldn't start git.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidDataException($"Couldn't run git: {ex.Message}. Is git installed and on the PATH?", ex);
        }

        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output.Trim(), error.Result.Trim());
        }
    }

    public static string RunOrThrow(string folder, params IEnumerable<string> arguments)
    {
        var (exitCode, output, error) = Run(folder, arguments);
        return exitCode == 0 ? output : throw new InvalidDataException($"git {string.Join(' ', arguments)} failed: {(error.Length > 0 ? error : output)}");
    }
}
