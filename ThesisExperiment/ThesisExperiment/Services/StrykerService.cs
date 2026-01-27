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

        private readonly Dictionary<string, StrykerFileResult?> _cache = new();

        /// <summary>Runs Stryker for a source file (cached per key).</summary>
        public async Task<StrykerFileResult?> RunStrykerForFileAsync(
            string workingDirectory, string sourceFilePath, string cacheKey)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var result = await ExecuteStrykerAsync(workingDirectory, sourceFilePath);
            _cache[cacheKey] = result;
            return result;
        }

        /// <summary>Extracts mutation score for a specific method's line range.</summary>
        public MutationResult ExtractMethodMutation(
            StrykerFileResult? fileResult, string sourceFilePath, int lineStart, int lineEnd)
        {
            if (fileResult == null)
            {
                return new MutationResult
                {
                    Tool = "stryker",
                    Scope = "method",
                    ScopedTo = sourceFilePath,
                    MutationScore = null
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
                    Tool = "stryker",
                    Scope = "method",
                    ScopedTo = sourceFilePath,
                    MutationScore = null
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
                Tool = "stryker",
                Scope = "method",
                ScopedTo = $"{sourceFilePath}:{lineStart}-{lineEnd}",
                MutantsKilled = killed,
                MutantsSurvived = survived + noCoverage,
                MutantsTotal = total,
                MutationScore = total > 0 ? Math.Round(100.0 * killed / total, 2) : null
            };
        }

        private async Task<StrykerFileResult?> ExecuteStrykerAsync(
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
                    return null;
                }

                return ParseLatestReport(workingDirectory);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"    Stryker timed out after {Timeout.TotalMinutes} minutes for {sourceFilePath}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker error: {ex.Message}");
                return null;
            }
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
