using System.Diagnostics;
using System.Text;

namespace Luau.Tooling;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => StandardOutput + StandardError;
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        bool echo = true,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        if (echo)
        {
            Console.WriteLine($"> {fileName} {string.Join(' ', startInfo.ArgumentList.Select(QuoteForDisplay))}");
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => Append(eventArgs.Data, stdout, echo ? Console.Out : null);
        process.ErrorDataReceived += (_, eventArgs) => Append(eventArgs.Data, stderr, echo ? Console.Error : null);

        try
        {
            if (!process.Start())
            {
                throw new ToolingException($"Failed to start process: {fileName}");
            }
        }
        catch (Exception exception) when (exception is not ToolingException)
        {
            throw new ToolingException($"Unable to start '{fileName}': {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource?.Token ?? CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var reason = timeoutSource?.IsCancellationRequested == true ? $" after {timeout}" : string.Empty;
            throw new ToolingException($"Process '{fileName}' was cancelled{reason}.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<ProcessResult> RequireAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        bool echo = true,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            fileName, arguments, workingDirectory, environment, timeout, echo, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ToolingException($"'{fileName}' exited with code {result.ExitCode}.");
        }

        return result;
    }

    private static void Append(string? line, StringBuilder destination, TextWriter? writer)
    {
        if (line is null)
        {
            return;
        }

        destination.AppendLine(line);
        writer?.WriteLine(line);
    }

    private static string QuoteForDisplay(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
