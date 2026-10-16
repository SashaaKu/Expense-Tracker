using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ExpenseTracker.Api.Categorization;

/// <summary>
/// A failed CLI call. Permanent errors (CLI missing, not logged in) will fail for every batch,
/// so the run should stop; transient errors (crash, timeout, garbled output) count as a failed batch.
/// </summary>
public sealed class ClaudeCliException(string message, bool isPermanent, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsPermanent { get; } = isPermanent;
}

/// <summary>Runs the Claude Code CLI in print mode and returns its schema-constrained JSON output.</summary>
public sealed class ClaudeCli(IOptions<CategorizationOptions> options)
{
    // Neutral working directory so the CLI doesn't pick up this repo's CLAUDE.md or hooks.
    private static readonly string WorkDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "expense-tracker-claude")).FullName;

    // Output that means the CLI can't authenticate; retrying won't help until the user logs in.
    private static readonly Regex AuthError = new(
        @"not logged in|/login|invalid api key|authentication|unauthorized|oauth token",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<JsonElement> RunAsync(string prompt, string jsonSchema, CancellationToken ct)
    {
        var o = options.Value;
        var psi = new ProcessStartInfo(o.ClaudePath)
        {
            WorkingDirectory = WorkDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        // Prompt goes via stdin (no command-line length limit); no tools are needed for classification.
        foreach (var arg in new[]
                 {
                     "-p", "--output-format", "json", "--json-schema", jsonSchema,
                     "--tools", "", "--model", o.Model,
                 })
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ClaudeCliException("Failed to start claude CLI", isPermanent: false);
        }
        catch (Win32Exception ex)
        {
            throw new ClaudeCliException(
                $"claude CLI not found at \"{o.ClaudePath}\". Install Claude Code or set Categorization:ClaudePath.",
                isPermanent: true, ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

            string stdout, stderr;
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The CLI exited before reading the whole prompt; its output below says why.
                }
                await process.WaitForExitAsync(timeout.Token);
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                throw new ClaudeCliException($"claude CLI timed out after {o.TimeoutSeconds}s", isPermanent: false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
            catch (IOException ex)
            {
                Kill(process);
                throw new ClaudeCliException($"claude CLI I/O failed: {ex.Message}", isPermanent: false, ex);
            }

            if (process.ExitCode != 0)
                throw Failure($"claude CLI exited with {process.ExitCode}", stderr + stdout);

            return ParseEnvelope(stdout);
        }
    }

    private static JsonElement ParseEnvelope(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw Failure("claude CLI reported an error", stdout);

            if (root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
                return structured.Clone();

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(result.GetString()!);
                return inner.RootElement.Clone();
            }
        }
        catch (JsonException ex)
        {
            throw new ClaudeCliException($"claude CLI output is not valid JSON: {Truncate(stdout)}", isPermanent: false, ex);
        }

        throw new ClaudeCliException($"claude CLI output has no structured result: {Truncate(stdout)}", isPermanent: false);
    }

    private static ClaudeCliException Failure(string what, string output) =>
        AuthError.IsMatch(output)
            ? new ClaudeCliException($"{what}: Claude Code is not logged in. Run `claude` and /login. ({Truncate(output)})", isPermanent: true)
            : new ClaudeCliException($"{what}: {Truncate(output)}", isPermanent: false);

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // already exited
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";
}
