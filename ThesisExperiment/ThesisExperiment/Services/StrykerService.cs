using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using CliWrap.Buffered;

using ThesisExperiment.Models;

namespace ThesisExperiment.Services
{
    /// <summary>Runs Stryker.NET mutation testing and parses results.</summary>
    public class StrykerService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private const int MaxConsecutiveTimeouts = 2;

        private readonly Dictionary<string, StrykerFileResult?> _cache = new();
        private readonly HashSet<string> _toolRestoredRepos = new();
        private readonly Dictionary<string, int> _consecutiveTimeouts = new();
        private readonly HashSet<string> _budgetExceededRepos = new();

        /// <summary>Runs Stryker for a source file (cached per key).</summary>
        /// <returns>Tuple of (file result, execution status: "available", "timeout", "skipped_budget", "error")</returns>
        public async Task<(StrykerFileResult? FileResult, string Status)> RunStrykerForFileAsync(
            string workingDirectory, string sourceFilePath, string cacheKey)
        {
            // Check if budget exceeded for this repo
            if (_budgetExceededRepos.Contains(workingDirectory))
            {
                Console.WriteLine($"    Stryker skipped (budget exceeded after {MaxConsecutiveTimeouts} consecutive timeouts)");
                return (null, "skipped_budget");
            }

            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                // Reset timeout counter on cache hit (successful previous run)
                _consecutiveTimeouts[workingDirectory] = 0;
                return (cached, "available");
            }

            var (result, status) = await ExecuteStrykerWithStatusAsync(workingDirectory, sourceFilePath);

            // Track consecutive timeouts
            if (status == "timeout")
            {
                _consecutiveTimeouts.TryGetValue(workingDirectory, out var count);
                count++;
                _consecutiveTimeouts[workingDirectory] = count;

                if (count >= MaxConsecutiveTimeouts)
                {
                    Console.WriteLine($"    Budget limit reached: {count} consecutive timeouts for this repo");
                    _budgetExceededRepos.Add(workingDirectory);
                }
            }
            else if (status == "available")
            {
                // Reset counter on success
                _consecutiveTimeouts[workingDirectory] = 0;
            }

            _cache[cacheKey] = result;
            return (result, status);
        }

        /// <summary>Extracts mutation score for a specific method's line range.</summary>
        /// <param name="fileResult">Stryker file result (can be null)</param>
        /// <param name="executionStatus">Execution status from RunStrykerForFileAsync</param>
        /// <param name="sourceFilePath">Source file path</param>
        /// <param name="lineStart">Method start line</param>
        /// <param name="lineEnd">Method end line</param>
        public MutationResult ExtractMethodMutation(
            StrykerFileResult? fileResult, string executionStatus,
            string sourceFilePath, int lineStart, int lineEnd)
        {
            // Handle non-available statuses
            if (executionStatus != "available")
            {
                var note = executionStatus switch
                {
                    "timeout" => "Stryker timed out",
                    "skipped_budget" => $"Skipped after {MaxConsecutiveTimeouts} consecutive timeouts for this repo",
                    "error" => "Stryker encountered an error",
                    _ => $"Stryker status: {executionStatus}"
                };

                return new MutationResult
                {
                    Status = executionStatus,
                    Tool = "stryker",
                    Scope = "method",
                    ScopedTo = sourceFilePath,
                    MutationScore = null,
                    Note = note
                };
            }

            if (fileResult == null)
            {
                return new MutationResult
                {
                    Status = "error",
                    Tool = "stryker",
                    Scope = "method",
                    ScopedTo = sourceFilePath,
                    MutationScore = null,
                    Note = "No Stryker result available"
                };
            }

            var normalizedPath = sourceFilePath.Replace('\\', '/');
            var matchingEntry = fileResult.Files
                .FirstOrDefault(f => f.Key.Replace('\\', '/').EndsWith(normalizedPath,
                    StringComparison.OrdinalIgnoreCase));

            if (matchingEntry.Value == null)
            {
                return new MutationResult
                {
                    Status = "not_in_scope",
                    Tool = "stryker",
                    Scope = "method",
                    ScopedTo = sourceFilePath,
                    MutationScore = null,
                    Note = "File not found in Stryker report"
                };
            }

            var methodMutants = matchingEntry.Value.Mutants
                .Where(m => m.Location?.Start?.Line >= lineStart
                         && m.Location?.Start?.Line <= lineEnd)
                .ToList();

            int killed = methodMutants.Count(m =>
                m.Status.Equals("Killed", StringComparison.OrdinalIgnoreCase));
            int survived = methodMutants.Count(m =>
                m.Status.Equals("Survived", StringComparison.OrdinalIgnoreCase));
            int noCoverage = methodMutants.Count(m =>
                m.Status.Equals("NoCoverage", StringComparison.OrdinalIgnoreCase));
            int timeout = methodMutants.Count(m =>
                m.Status.Equals("Timeout", StringComparison.OrdinalIgnoreCase));
            int total = methodMutants.Count;

            return new MutationResult
            {
                Status = "available",
                Tool = "stryker",
                Scope = "method",
                ScopedTo = $"{sourceFilePath}:{lineStart}-{lineEnd}",
                MutantsKilled = killed,
                MutantsSurvived = survived + noCoverage,
                MutantsTotal = total,
                MutationScore = total > 0 ? Math.Round(100.0 * killed / total, 2) : null
            };
        }

        /// <summary>Runs dotnet tool restore if .config/dotnet-tools.json exists (once per repo).</summary>
        private async Task EnsureToolRestoreAsync(string workingDirectory)
        {
            // Skip if already restored for this repo
            if (_toolRestoredRepos.Contains(workingDirectory))
                return;

            var toolsManifest = Path.Combine(workingDirectory, ".config", "dotnet-tools.json");
            if (!File.Exists(toolsManifest))
            {
                _toolRestoredRepos.Add(workingDirectory);
                return;
            }

            Console.WriteLine("    Running dotnet tool restore...");
            try
            {
                var result = await Cli.Wrap("dotnet")
                    .WithArguments(new[] { "tool", "restore" })
                    .WithWorkingDirectory(workingDirectory)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode == 0)
                {
                    Console.WriteLine("    Tool restore completed.");
                }
                else
                {
                    Console.WriteLine($"    Tool restore exited with code {result.ExitCode} (continuing anyway)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Tool restore failed: {ex.Message} (continuing anyway)");
            }

            _toolRestoredRepos.Add(workingDirectory);
        }

        private async Task<(StrykerFileResult? Result, string Status)> ExecuteStrykerWithStatusAsync(
            string workingDirectory, string sourceFilePath)
        {
            // Ensure dotnet tools are restored (once per repo)
            await EnsureToolRestoreAsync(workingDirectory);

            // Initial delay to allow file handles to be released after tests
            await Task.Delay(2000);

            var (result, status, errorMessage) = await TryExecuteStrykerAsync(workingDirectory, sourceFilePath);

            // Retry once if file lock error
            if (status == "error" && IsFileLockError(errorMessage))
            {
                Console.WriteLine("    File lock detected, attempting recovery...");

                // Shutdown build server to release locks
                await ShutdownBuildServerAsync(workingDirectory);
                await Task.Delay(2000);

                // Retry
                (result, status, _) = await TryExecuteStrykerAsync(workingDirectory, sourceFilePath);
            }

            return (result, status);
        }

        private async Task<(StrykerFileResult? Result, string Status, string? ErrorMessage)> TryExecuteStrykerAsync(
            string workingDirectory, string sourceFilePath)
        {
            var strykerOutputDir = Path.Combine(workingDirectory, "StrykerOutput");
            if (Directory.Exists(strykerOutputDir))
            {
                try { Directory.Delete(strykerOutputDir, recursive: true); }
                catch { }
            }

            var relativeSourcePath = Path.IsPathRooted(sourceFilePath)
                ? Path.GetRelativePath(workingDirectory, sourceFilePath)
                : sourceFilePath;

            var args = new List<string>
            {
                "stryker",
                "--mutate", relativeSourcePath,
                "--reporter", "json"
            };

            var solutionFile = FindSolutionFile(workingDirectory);
            if (solutionFile != null)
            {
                args.Add("--solution");
                args.Add(solutionFile);
            }

            using var cts = new CancellationTokenSource(Timeout);

            try
            {
                var result = await Cli.Wrap("dotnet")
                    .WithArguments(args)
                    .WithWorkingDirectory(workingDirectory)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cts.Token);

                if (result.ExitCode != 0)
                {
                    Console.WriteLine($"    Stryker exited with code {result.ExitCode}");
                    Console.WriteLine($"    stderr: {Truncate(result.StandardError, 500)}");
                    return (null, "error", result.StandardError);
                }

                var report = ParseLatestReport(workingDirectory);
                return (report, report != null ? "available" : "error", null);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"    Stryker timed out after {Timeout.TotalMinutes} minutes for {sourceFilePath}");
                return (null, "timeout", null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker error: {ex.Message}");
                return (null, "error", ex.Message);
            }
        }

        private static bool IsFileLockError(string? errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return false;
            return errorMessage.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ShutdownBuildServerAsync(string workingDirectory)
        {
            try
            {
                Console.WriteLine("    Running dotnet build-server shutdown...");
                await Cli.Wrap("dotnet")
                    .WithArguments(new[] { "build-server", "shutdown" })
                    .WithWorkingDirectory(workingDirectory)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync();
            }
            catch { /* Ignore errors */ }
        }

        private StrykerFileResult? ParseLatestReport(string workingDirectory)
        {
            var strykerOutputDir = Path.Combine(workingDirectory, "StrykerOutput");
            if (!Directory.Exists(strykerOutputDir))
            {
                Console.WriteLine("    No StrykerOutput directory found.");
                return null;
            }

            var reportFiles = Directory.GetFiles(strykerOutputDir,
                "mutation-report.json", SearchOption.AllDirectories);

            if (reportFiles.Length == 0)
            {
                Console.WriteLine("    No mutation-report.json found.");
                return null;
            }

            var latestReport = reportFiles
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();

            try
            {
                var json = File.ReadAllText(latestReport);
                var report = JsonSerializer.Deserialize<StrykerReport>(json, JsonOptions);

                if (report?.Files == null || report.Files.Count == 0)
                {
                    Console.WriteLine("    Stryker report contains no files.");
                    return null;
                }

                return new StrykerFileResult { Files = report.Files };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Failed to parse Stryker report: {ex.Message}");
                return null;
            }
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text[..maxLength] + "...";
        }

        private static string? FindSolutionFile(string repoRoot)
        {
            var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
                return Path.GetFileName(slnFiles[0]);

            var slnxFiles = Directory.GetFiles(repoRoot, "*.slnx", SearchOption.TopDirectoryOnly);
            if (slnxFiles.Length > 0)
                return Path.GetFileName(slnxFiles[0]);

            return null;
        }

        public class StrykerFileResult
        {
            public Dictionary<string, StrykerFileEntry> Files { get; set; } = new();
        }

        private class StrykerReport
        {
            [JsonPropertyName("files")]
            public Dictionary<string, StrykerFileEntry> Files { get; set; } = new();
        }

        public class StrykerFileEntry
        {
            [JsonPropertyName("mutants")]
            public List<StrykerMutant> Mutants { get; set; } = new();
        }

        public class StrykerMutant
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("mutatorName")]
            public string MutatorName { get; set; } = string.Empty;

            [JsonPropertyName("status")]
            public string Status { get; set; } = string.Empty;

            [JsonPropertyName("location")]
            public StrykerLocation? Location { get; set; }
        }

        public class StrykerLocation
        {
            [JsonPropertyName("start")]
            public StrykerPosition? Start { get; set; }

            [JsonPropertyName("end")]
            public StrykerPosition? End { get; set; }
        }

        public class StrykerPosition
        {
            [JsonPropertyName("line")]
            public int Line { get; set; }

            [JsonPropertyName("column")]
            public int Column { get; set; }
        }
    }
}
