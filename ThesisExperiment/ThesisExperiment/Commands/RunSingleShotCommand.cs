using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ThesisExperiment.Models;
using ThesisExperiment.Services;

namespace ThesisExperiment.Commands
{
    /// <summary>Generates tests via single-shot LLM prompting (Variant B).</summary>
    public class RunSingleShotCommand
    {
        private readonly GitCleanupService _git = new();
        private readonly DotnetBuildService _buildService = new();
        private readonly DotnetTestService _testService = new();
        private readonly CoverletService _coverletService = new();
        private readonly StrykerService _strykerService = new();
        private readonly JsonLogger _jsonLogger = new();
        private readonly OpenAiService _openAiService = new();
        private readonly RoslynMethodExtractor _roslynExtractor = new();
        private readonly PromptTemplateService _promptService = new();
        private readonly TestProjectDiscoveryService _discoveryService = new();

        private const string Variant = "B";
        private const string RunsDir = "runs";

        /// <summary>Orchestrates single-shot generation for all 50 focal methods.</summary>
        public async Task ExecuteAsync(string dataDir)
        {
            Console.WriteLine("=== Step 5: Run Single-Shot (Variant B) ===\n");

            var methodsPath = Path.Combine(dataDir, "method_list_all.csv");
            Console.WriteLine($"Reading methods from {methodsPath}...");
            var methods = ReadCsv<SampledMethod>(methodsPath);
            Console.WriteLine($"Loaded {methods.Count} methods.");

            var selectedPath = Path.Combine(dataDir, "project_list_selected.csv");
            var projectStarsMap = new Dictionary<string, int>();
            if (File.Exists(selectedPath))
            {
                var projects = ReadCsv<ProjectCandidate>(selectedPath);
                foreach (var p in projects)
                    projectStarsMap[p.RepoUrl] = p.Stars;
            }

            var grouped = methods.GroupBy(m => m.RepoUrl).ToList();
            Console.WriteLine($"Found {grouped.Count} project(s).\n");

            int totalRecords = 0;
            var statusCounts = new Dictionary<string, int>();

            foreach (var projectGroup in grouped)
            {
                var repoUrl = projectGroup.Key;
                var projectMethods = projectGroup.ToList();
                var projectName = projectMethods.First().ProjectName;
                var commitHash = projectMethods.First().CommitHash;

                Console.WriteLine($"=== [{projectName}] {projectMethods.Count} methods, commit {commitHash[..Math.Min(12, commitHash.Length)]} ===");

                string repoPath;
                try
                {
                    var clonePath = Path.Combine("repos", projectName);
                    repoPath = await _git.EnsureRepoAtCommitAsync(repoUrl, clonePath, commitHash);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: Failed to prepare repo: {ex.Message}");
                    foreach (var method in projectMethods)
                    {
                        WriteFailedRecord(projectName, method, repoUrl, commitHash,
                            projectStarsMap, "clone_failed", "clone_failed", ex.Message);
                        totalRecords++;
                        IncrementStatus(statusCounts, "clone_failed");
                    }
                    continue;
                }

                foreach (var method in projectMethods)
                {
                    Console.WriteLine($"\n  --- {method.TypeName}.{method.MethodName} ---");
                    try
                    {
                        var record = await ProcessMethodAsync(
                            method, repoPath, projectName, repoUrl, commitHash, projectStarsMap);

                        var filePath = JsonLogger.GetOutputPath(RunsDir, projectName, method.Identifier, Variant);
                        _jsonLogger.WriteRunRecord(record, filePath);
                        Console.WriteLine($"    Wrote: {Path.GetFileName(filePath)} [{record.Outcome.FinalStatus}]");

                        totalRecords++;
                        IncrementStatus(statusCounts, record.Outcome.FinalStatus);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    UNEXPECTED ERROR: {ex.Message}");
                        WriteFailedRecord(projectName, method, repoUrl, commitHash,
                            projectStarsMap, "internal_error", "internal_error", ex.Message);
                        totalRecords++;
                        IncrementStatus(statusCounts, "internal_error");
                    }
                }
            }

            Console.WriteLine($"\n=== Single-Shot Run Complete ===");
            Console.WriteLine($"Total RunRecords written: {totalRecords}");
            Console.WriteLine($"Projects processed:       {grouped.Count}");
            Console.WriteLine($"\nStatus breakdown:");
            foreach (var kv in statusCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {kv.Key}: {kv.Value}");

            if (totalRecords == methods.Count)
                Console.WriteLine("\nAll methods have RunRecords. OK");
            else
                Console.WriteLine($"\nWARNING: Expected {methods.Count} records, got {totalRecords}.");
        }

        /// <summary>Processes one focal method: prompt, generate, gate1, gate2, flakiness, coverage, mutation.</summary>
        private async Task<RunRecord> ProcessMethodAsync(
            SampledMethod method,
            string repoPath,
            string projectName,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap)
        {
            var timestampStart = DateTime.UtcNow;

            Console.WriteLine("    Resetting repo to clean state...");
            await _git.ResetToCleanStateAsync(repoPath, commitHash);

            Console.WriteLine("    Discovering test project...");
            var testInfo = _discoveryService.DiscoverTestProject(repoPath, method.FilePath);
            if (testInfo == null)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "no_test_project", stopReason: "no_test_project",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "discovery", errorMessage: $"No test project found for {method.FilePath}");
            }

            Console.WriteLine($"    Test project: {Path.GetFileName(testInfo.TestProjectPath)} ({testInfo.Framework})");

            Console.WriteLine("    Extracting method source...");
            var absoluteFilePath = Path.Combine(repoPath, method.FilePath);
            var extraction = _roslynExtractor.ExtractMethod(
                absoluteFilePath, method.MethodName, method.LineStart, method.LineEnd, method.TypeName);

            if (extraction.UsedFallback)
                Console.WriteLine("    (used fallback line-range extraction)");

            var signature = !string.IsNullOrEmpty(extraction.MethodSignature)
                ? extraction.MethodSignature
                : $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})";

            Console.WriteLine("    Building prompt...");
            var prompt = _promptService.BuildSingleShotPrompt(
                testInfo.Framework,
                testInfo.TestAttribute,
                method.Namespace,
                method.TypeName,
                signature,
                extraction.MethodBody,
                extraction.ClassContext);

            Console.WriteLine("    Calling OpenAI API...");
            OpenAiResult aiResult;
            try
            {
                aiResult = await _openAiService.GenerateTestAsync(
                    prompt.SystemMessage, prompt.UserMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    API error: {ex.Message}");
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "api_error", stopReason: "api_error",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "openai", errorMessage: ex.Message,
                    prompt: prompt);
            }

            Console.WriteLine($"    Model: {aiResult.ModelId}, Tokens: {aiResult.TotalTokens}");

            if (string.IsNullOrWhiteSpace(aiResult.ExtractedCode))
            {
                Console.WriteLine("    No code block found in response.");
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "llm_invalid_output", stopReason: "no_code_extracted",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "openai", errorSubcategory: "no_code_extracted",
                    errorMessage: "No code block found in LLM response",
                    prompt: prompt, aiResult: aiResult);
            }

            var sanitizedId = SanitizeForFilename(method.Identifier);
            var generatedDir = Path.Combine(testInfo.TestProjectDir, "GeneratedTests");
            Directory.CreateDirectory(generatedDir);
            var generatedTestPath = Path.Combine(generatedDir, $"Generated__{sanitizedId}.cs");
            File.WriteAllText(generatedTestPath, aiResult.ExtractedCode, Encoding.UTF8);
            Console.WriteLine($"    Wrote test file: {Path.GetFileName(generatedTestPath)}");

            var testQualifiedName = ExtractTestQualifiedName(aiResult.ExtractedCode);
            var testFilter = testQualifiedName != null
                ? $"FullyQualifiedName~{testQualifiedName}"
                : null;

            if (testFilter != null)
                Console.WriteLine($"    Test filter: {testFilter}");

            Console.WriteLine("    Gate 1: Building...");
            var buildResult = await _buildService.BuildAsync(repoPath);
            bool buildPassed = buildResult.ExitCode == 0;
            Console.WriteLine($"    Gate 1: {(buildPassed ? "PASSED" : "FAILED")} (exit {buildResult.ExitCode})");

            if (!buildPassed)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "build_failed", stopReason: "gate1_failed",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "build", errorMessage: buildResult.Stderr,
                    prompt: prompt, aiResult: aiResult, buildResult: buildResult);
            }

            Console.WriteLine("    Gate 2: Running tests with coverage...");
            var (testResult, coverageDir) = await _testService.RunTestsWithCoverageAsync(repoPath, testFilter);
            bool testPassed = testResult.ExitCode == 0;
            Console.WriteLine($"    Gate 2: {(testPassed ? "PASSED" : "FAILED")} (exit {testResult.ExitCode})");

            if (!testPassed)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "test_failed", stopReason: "gate2_failed",
                    gate1Passed: true, gate2Passed: false,
                    errorCategory: "test", errorMessage: testResult.Stderr,
                    prompt: prompt, aiResult: aiResult,
                    buildResult: buildResult, testResult: testResult);
            }

            Console.WriteLine("    Flakiness check: running 2 more test passes...");
            var flakinessResults = new List<string> { "pass" };

            for (int i = 2; i <= 3; i++)
            {
                var rerun = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                var runResult = rerun.ExitCode == 0 ? "pass" : "fail";
                flakinessResults.Add(runResult);
                Console.WriteLine($"    Flakiness run {i}: {runResult}");
            }

            var allConsistent = flakinessResults.Distinct().Count() == 1;
            var flakinessClassification = allConsistent ? "stable_pass" : "flaky";
            Console.WriteLine($"    Flakiness: {flakinessClassification}");

            var flakiness = new FlakinessResult
            {
                RunsExecuted = 3,
                RunResults = flakinessResults,
                Classification = flakinessClassification
            };

            if (!allConsistent)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    finalStatus: "flaky_failure", stopReason: "flaky_test",
                    gate1Passed: true, gate2Passed: true,
                    errorCategory: "flakiness",
                    errorMessage: $"Inconsistent results: {string.Join(", ", flakinessResults)}",
                    prompt: prompt, aiResult: aiResult,
                    buildResult: buildResult, testResult: testResult,
                    flakiness: flakiness);
            }

            Console.WriteLine("    Parsing coverage...");
            var coberturaFiles = _coverletService.FindCoberturaFiles(coverageDir);
            var coverage = _coverletService.ParseMethodCoverage(
                coberturaFiles, method.FilePath, method.LineStart, method.LineEnd, repoPath);
            Console.WriteLine($"    Coverage: {coverage.LinePercent}% line, {coverage.BranchPercent}% branch");

            Console.WriteLine("    Running Stryker mutation testing...");
            var cacheKey = $"{projectName}::{commitHash}::{method.FilePath}";
            StrykerService.StrykerFileResult? strykerResult = null;
            try
            {
                strykerResult = await _strykerService.RunStrykerForFileAsync(
                    repoPath, method.FilePath, cacheKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker failed: {ex.Message}");
            }

            var mutation = _strykerService.ExtractMethodMutation(
                strykerResult, method.FilePath, method.LineStart, method.LineEnd);
            Console.WriteLine($"    Mutation: {mutation.MutationScore}% ({mutation.MutantsKilled}/{mutation.MutantsTotal})");

            return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                finalStatus: "completed", stopReason: "completed",
                gate1Passed: true, gate2Passed: true,
                prompt: prompt, aiResult: aiResult,
                buildResult: buildResult, testResult: testResult,
                coverage: coverage, mutation: mutation, flakiness: flakiness);
        }

        private RunRecord BuildRecord(
            DateTime timestampStart,
            string projectName,
            SampledMethod method,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap,
            string finalStatus,
            string stopReason,
            bool gate1Passed,
            bool gate2Passed,
            string errorCategory = "",
            string errorSubcategory = "",
            string errorMessage = "",
            PromptPair? prompt = null,
            OpenAiResult? aiResult = null,
            BuildResult? buildResult = null,
            TestResult? testResult = null,
            CoverageResult? coverage = null,
            MutationResult? mutation = null,
            FlakinessResult? flakiness = null)
        {
            var csvSignature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})";

            return new RunRecord
            {
                RunId = $"B__{projectName}__{SanitizeForId(method.Identifier)}",
                TimestampStart = timestampStart,
                TimestampEnd = DateTime.UtcNow,
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
                    Signature = csvSignature,
                    ContainingClass = $"{method.Namespace}.{method.TypeName}",
                    ContainingFile = method.FilePath
                },
                Experiment = new ExperimentInfo
                {
                    Variant = Variant,
                    AttemptNumber = 1,
                    MaxAttempts = 1,
                    PromptVersion = _promptService.TemplateVersion,
                    PromptFile = _promptService.SingleShotTemplateFile
                },
                Model = aiResult != null
                    ? new ModelInfo
                    {
                        Provider = "openai",
                        ModelId = aiResult.ModelId,
                        Temperature = 0.2,
                        MaxTokens = 2048,
                        RequestId = aiResult.RequestId
                    }
                    : new ModelInfo(),
                Prompt = prompt != null
                    ? new PromptInfo
                    {
                        SystemMessage = prompt.SystemMessage,
                        UserMessage = prompt.UserMessage,
                        TemplateFile = _promptService.SingleShotTemplateFile,
                        TemplateVersion = _promptService.TemplateVersion
                    }
                    : new PromptInfo(),
                Response = aiResult != null
                    ? new ResponseInfo
                    {
                        RawText = aiResult.RawText,
                        ExtractedCode = aiResult.ExtractedCode,
                        TokensUsed = aiResult.TotalTokens,
                        PromptTokens = aiResult.PromptTokens,
                        CompletionTokens = aiResult.CompletionTokens,
                        FinishReason = aiResult.FinishReason
                    }
                    : new ResponseInfo(),
                Build = buildResult ?? new BuildResult(),
                Test = testResult ?? new TestResult(),
                Coverage = coverage ?? new CoverageResult(),
                Mutation = mutation ?? new MutationResult(),
                Flakiness = flakiness ?? new FlakinessResult(),
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = gate1Passed,
                    Gate2TestPassed = gate2Passed,
                    FinalStatus = finalStatus,
                    StopReason = stopReason
                },
                Error = !string.IsNullOrEmpty(errorCategory)
                    ? new ErrorInfo
                    {
                        Category = errorCategory,
                        Subcategory = errorSubcategory,
                        RawMessage = Truncate(errorMessage, 10_000),
                        LabeledBy = "automation"
                    }
                    : new ErrorInfo()
            };
        }

        private void WriteFailedRecord(
            string projectName, SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap, string finalStatus, string errorCategory,
            string errorMessage)
        {
            var record = BuildRecord(
                DateTime.UtcNow, projectName, method, repoUrl, commitHash, starsMap,
                finalStatus: finalStatus, stopReason: finalStatus,
                gate1Passed: false, gate2Passed: false,
                errorCategory: errorCategory, errorMessage: errorMessage);

            var filePath = JsonLogger.GetOutputPath(RunsDir, projectName, method.Identifier, Variant);
            _jsonLogger.WriteRunRecord(record, filePath);
        }

        private static string? ExtractTestQualifiedName(string code)
        {
            var classMatch = Regex.Match(code, @"class\s+(\w+)");
            if (!classMatch.Success)
                return null;

            var className = classMatch.Groups[1].Value;

            var nsMatch = Regex.Match(code, @"namespace\s+([\w.]+)");
            if (nsMatch.Success)
                return $"{nsMatch.Groups[1].Value}.{className}";

            return className;
        }

        private static string SanitizeForId(string input)
        {
            return input.Replace(' ', '_').Replace('.', '_');
        }

        private static string SanitizeForFilename(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
                sb.Append(invalid.Contains(c) ? '_' : c);

            return sb.ToString()
                .Replace('(', '_')
                .Replace(')', '_')
                .Replace(',', '_')
                .Replace(' ', '_');
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text[..maxLength] + $"\n... [truncated, {text.Length} total chars]";
        }

        private static void IncrementStatus(Dictionary<string, int> counts, string status)
        {
            counts.TryGetValue(status, out var current);
            counts[status] = current + 1;
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
