using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    public class CollectBaselineCommand
    {
        private readonly GitCleanupService _git = new();
        private readonly DotnetBuildService _buildService = new();
        private readonly DotnetTestService _testService = new();
        private readonly CoverletService _coverletService = new();
        private readonly StrykerService _strykerService = new();
        private readonly JsonLogger _jsonLogger = new();

        public async Task ExecuteAsync(string outputPath)
        {
            // Phase 1: Read input
            var methodsPath = Path.Combine(outputPath, "method_list_all.csv");
            Console.WriteLine($"Reading methods from {methodsPath}...");
            var methods = ReadCsv<SampledMethod>(methodsPath);
            Console.WriteLine($"Loaded {methods.Count} methods.");

            // Optionally read project_list_selected.csv for Stars info
            var selectedPath = Path.Combine(outputPath, "project_list_selected.csv");
            var projectStarsMap = new Dictionary<string, int>();
            if (File.Exists(selectedPath))
            {
                var projects = ReadCsv<ProjectCandidate>(selectedPath);
                foreach (var p in projects)
                    projectStarsMap[p.RepoUrl] = p.Stars;
            }

            // Phase 2: Group by project
            var grouped = methods.GroupBy(m => m.RepoUrl).ToList();
            Console.WriteLine($"Found {grouped.Count} project(s).\n");

            var runsDir = "runs";
            int totalRecords = 0;
            int failedProjects = 0;

            // Phase 3: Process each project
            foreach (var projectGroup in grouped)
            {
                var repoUrl = projectGroup.Key;
                var projectMethods = projectGroup.ToList();
                var projectName = projectMethods.First().ProjectName;
                var commitHash = projectMethods.First().CommitHash;

                Console.WriteLine($"=== [{projectName}] {projectMethods.Count} methods, commit {commitHash[..Math.Min(12, commitHash.Length)]} ===");

                // Step A: Ensure repo cloned and at correct commit
                string repoPath;
                try
                {
                    // Derive clone path from project name (matching Step 2 convention)
                    var clonePath = Path.Combine("repos", projectName);
                    repoPath = await _git.EnsureRepoAtCommitAsync(repoUrl, clonePath, commitHash);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: Failed to prepare repo: {ex.Message}");
                    foreach (var method in projectMethods)
                    {
                        WriteFailedRecord(runsDir, projectName, method, repoUrl, commitHash,
                            projectStarsMap, "clone_failed", ex.Message);
                        totalRecords++;
                    }
                    failedProjects++;
                    continue;
                }

                // Step B: Build
                Console.WriteLine("  Building...");
                var buildResult = await _buildService.BuildAsync(repoPath);
                bool buildPassed = buildResult.ExitCode == 0;
                Console.WriteLine($"  Build: {(buildPassed ? "PASSED" : "FAILED")} (exit {buildResult.ExitCode})");

                if (!buildPassed)
                {
                    foreach (var method in projectMethods)
                    {
                        WriteRecordWithBuildOnly(runsDir, projectName, method, repoUrl,
                            commitHash, projectStarsMap, buildResult);
                        totalRecords++;
                    }
                    failedProjects++;
                    continue;
                }

                // Step C: Test with coverage
                Console.WriteLine("  Running tests with coverage collection...");
                var (testResult, coverageDir) = await _testService.RunTestsWithCoverageAsync(repoPath);
                bool testPassed = testResult.ExitCode == 0;
                Console.WriteLine($"  Tests: {(testPassed ? "PASSED" : "FAILED")} (exit {testResult.ExitCode})");

                // Step D: Find Cobertura files
                var coberturaFiles = _coverletService.FindCoberturaFiles(coverageDir);
                Console.WriteLine($"  Found {coberturaFiles.Count} coverage report(s).");

                // Step E: Run Stryker per unique source file (cached by project::commit::file)
                var uniqueFiles = projectMethods
                    .Select(m => m.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Console.WriteLine($"  Running Stryker for {uniqueFiles.Count} unique source file(s)...");

                var strykerResults = new Dictionary<string, StrykerService.StrykerFileResult?>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var file in uniqueFiles)
                {
                    var cacheKey = $"{projectName}::{commitHash}::{file}";
                    Console.WriteLine($"    Stryker: {file}");
                    try
                    {
                        strykerResults[file] = await _strykerService.RunStrykerForFileAsync(
                            repoPath, file, cacheKey);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Stryker failed for {file}: {ex.Message}");
                        strykerResults[file] = null;
                    }
                }

                // Step F: Write RunRecord per method
                Console.WriteLine($"  Writing {projectMethods.Count} RunRecords...");
                foreach (var method in projectMethods)
                {
                    // Coverage: parse for this method's line range
                    var coverage = _coverletService.ParseMethodCoverage(
                        coberturaFiles, method.FilePath, method.LineStart, method.LineEnd, repoPath);
                    if (!testPassed)
                        coverage.Note = "collected from failing test run";

                    // Mutation: look up from pre-fetched results (no redundant async call)
                    strykerResults.TryGetValue(method.FilePath, out var fileResult);
                    var mutation = _strykerService.ExtractMethodMutation(
                        fileResult, method.FilePath, method.LineStart, method.LineEnd);

                    // Build RunRecord
                    var record = BuildRunRecord(
                        projectName, method, repoUrl, commitHash, projectStarsMap,
                        buildResult, testResult, coverage, mutation);

                    // Write JSON
                    var filePath = JsonLogger.GetOutputPath(runsDir, projectName, method.Identifier, "A");
                    _jsonLogger.WriteRunRecord(record, filePath);
                    Console.WriteLine($"    Wrote: {Path.GetFileName(filePath)}");
                    totalRecords++;
                }
            }

            // Summary
            Console.WriteLine($"\n=== Baseline Collection Complete ===");
            Console.WriteLine($"Total RunRecords written: {totalRecords}");
            Console.WriteLine($"Projects processed:       {grouped.Count}");
            Console.WriteLine($"Projects failed:          {failedProjects}");
            Console.WriteLine($"Output directory:         {Path.GetFullPath(runsDir)}");

            if (totalRecords == methods.Count)
                Console.WriteLine("\nAll methods have RunRecords. OK");
            else
                Console.WriteLine($"\nWARNING: Expected {methods.Count} records, got {totalRecords}.");
        }

        private RunRecord BuildRunRecord(
            string projectName, SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap,
            BuildResult buildResult, TestResult testResult,
            CoverageResult coverage, MutationResult mutation)
        {
            var now = DateTime.UtcNow;
            bool buildPassed = buildResult.ExitCode == 0;
            bool testPassed = testResult.ExitCode == 0;

            string finalStatus;
            if (!buildPassed) finalStatus = "build_failed";
            else if (!testPassed) finalStatus = "test_failed";
            else finalStatus = "baseline_complete";

            return new RunRecord
            {
                RunId = $"A__{projectName}__{SanitizeForId(method.MethodName)}",
                TimestampStart = now,
                TimestampEnd = now,
                Project = new ProjectInfo
                {
                    Name = projectName,
                    RepoUrl = repoUrl,
                    CommitHash = commitHash,
                    Stars = starsMap.GetValueOrDefault(repoUrl, 0)
                },
                Method = new MethodInfo
                {
                    Identifier = method.Identifier,
                    FilePath = method.FilePath,
                    LineStart = method.LineStart,
                    LineEnd = method.LineEnd,
                    Signature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})",
                    ContainingClass = $"{method.Namespace}.{method.TypeName}",
                    ContainingFile = method.FilePath
                },
                Experiment = new ExperimentInfo
                {
                    Variant = "A",
                    AttemptNumber = 0,
                    MaxAttempts = 0
                },
                Build = buildResult,
                Test = testResult,
                Coverage = coverage,
                Mutation = mutation,
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = buildPassed,
                    Gate2TestPassed = testPassed,
                    FinalStatus = finalStatus,
                    StopReason = finalStatus == "baseline_complete" ? "completed" : finalStatus
                },
                Error = !buildPassed || !testPassed
                    ? new ErrorInfo
                    {
                        Category = !buildPassed ? "build" : "test",
                        RawMessage = !buildPassed ? buildResult.Stderr : testResult.Stderr,
                        LabeledBy = "automation"
                    }
                    : new ErrorInfo()
            };
        }

        private void WriteFailedRecord(string runsDir, string projectName,
            SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap, string status, string errorMessage)
        {
            var now = DateTime.UtcNow;
            var record = new RunRecord
            {
                RunId = $"A__{projectName}__{SanitizeForId(method.MethodName)}",
                TimestampStart = now,
                TimestampEnd = now,
                Project = new ProjectInfo
                {
                    Name = projectName,
                    RepoUrl = repoUrl,
                    CommitHash = commitHash,
                    Stars = starsMap.GetValueOrDefault(repoUrl, 0)
                },
                Method = new MethodInfo
                {
                    Identifier = method.Identifier,
                    FilePath = method.FilePath,
                    LineStart = method.LineStart,
                    LineEnd = method.LineEnd,
                    Signature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})",
                    ContainingClass = $"{method.Namespace}.{method.TypeName}",
                    ContainingFile = method.FilePath
                },
                Experiment = new ExperimentInfo
                {
                    Variant = "A",
                    AttemptNumber = 0,
                    MaxAttempts = 0
                },
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = false,
                    Gate2TestPassed = false,
                    FinalStatus = status,
                    StopReason = status
                },
                Error = new ErrorInfo
                {
                    Category = status,
                    RawMessage = errorMessage,
                    LabeledBy = "automation"
                }
            };

            var filePath = JsonLogger.GetOutputPath(runsDir, projectName, method.Identifier, "A");
            _jsonLogger.WriteRunRecord(record, filePath);
        }

        private void WriteRecordWithBuildOnly(string runsDir, string projectName,
            SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap, BuildResult buildResult)
        {
            var now = DateTime.UtcNow;
            var record = new RunRecord
            {
                RunId = $"A__{projectName}__{SanitizeForId(method.MethodName)}",
                TimestampStart = now,
                TimestampEnd = now,
                Project = new ProjectInfo
                {
                    Name = projectName,
                    RepoUrl = repoUrl,
                    CommitHash = commitHash,
                    Stars = starsMap.GetValueOrDefault(repoUrl, 0)
                },
                Method = new MethodInfo
                {
                    Identifier = method.Identifier,
                    FilePath = method.FilePath,
                    LineStart = method.LineStart,
                    LineEnd = method.LineEnd,
                    Signature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})",
                    ContainingClass = $"{method.Namespace}.{method.TypeName}",
                    ContainingFile = method.FilePath
                },
                Experiment = new ExperimentInfo
                {
                    Variant = "A",
                    AttemptNumber = 0,
                    MaxAttempts = 0
                },
                Build = buildResult,
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = false,
                    Gate2TestPassed = false,
                    FinalStatus = "build_failed",
                    StopReason = "build_failed"
                },
                Error = new ErrorInfo
                {
                    Category = "build",
                    RawMessage = buildResult.Stderr,
                    LabeledBy = "automation"
                }
            };

            var filePath = JsonLogger.GetOutputPath(runsDir, projectName, method.Identifier, "A");
            _jsonLogger.WriteRunRecord(record, filePath);
        }

        private static string SanitizeForId(string input)
        {
            return input.Replace(' ', '_').Replace('.', '_');
        }

        private List<T> ReadCsv<T>(string filePath)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HeaderValidated = null,
                MissingFieldFound = null
            };

            using var reader = new StreamReader(filePath, Encoding.UTF8);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<T>().ToList();
        }
    }
}
