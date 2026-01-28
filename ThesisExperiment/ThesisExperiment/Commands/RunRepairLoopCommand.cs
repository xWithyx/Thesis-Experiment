using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ThesisExperiment.Models;
using ThesisExperiment.Services;

namespace ThesisExperiment.Commands
{
    /// <summary>Generates tests via repair-loop LLM prompting (Variant C).</summary>
    public class RunRepairLoopCommand
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

        private const string Variant = "C";
        private const string RunsDir = "runs";

        /// <summary>Orchestrates repair-loop generation for all 50 focal methods.</summary>
        public async Task ExecuteAsync(string dataDir, int maxAttempts = 3, int? limit = null, int? skip = null)
        {
            Console.WriteLine($"=== Step 8: Run Repair Loop (Variant C, max {maxAttempts} attempts) ===\n");

            var methodsPath = Path.Combine(dataDir, "method_list_all.csv");
            Console.WriteLine($"Reading methods from {methodsPath}...");
            var methods = ReadCsv<SampledMethod>(methodsPath);
            Console.WriteLine($"Loaded {methods.Count} methods.");

            if (skip.HasValue && skip.Value > 0)
            {
                methods = methods.Skip(skip.Value).ToList();
                Console.WriteLine($"  (--skip {skip.Value}: Skipped first {skip.Value} method(s), {methods.Count} remaining)");
            }

            if (limit.HasValue && limit.Value > 0)
            {
                methods = methods.Take(limit.Value).ToList();
                Console.WriteLine($"  (--limit {limit.Value}: Processing only first {methods.Count} method(s))");
            }

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
                            projectStarsMap, maxAttempts, "clone_failed", "clone_failed", ex.Message);
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
                        var record = await ProcessMethodWithRepairAsync(
                            method, repoPath, projectName, repoUrl, commitHash, projectStarsMap, maxAttempts);

                        var filePath = JsonLogger.GetOutputPath(RunsDir, projectName, method.Identifier, Variant);
                        _jsonLogger.WriteRunRecord(record, filePath);
                        Console.WriteLine($"    Wrote: {Path.GetFileName(filePath)} [{record.Outcome.FinalStatus}] (attempt {record.Experiment.AttemptNumber}/{maxAttempts})");

                        totalRecords++;
                        IncrementStatus(statusCounts, record.Outcome.FinalStatus);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    UNEXPECTED ERROR: {ex.Message}");
                        WriteFailedRecord(projectName, method, repoUrl, commitHash,
                            projectStarsMap, maxAttempts, "internal_error", "internal_error", ex.Message);
                        totalRecords++;
                        IncrementStatus(statusCounts, "internal_error");
                    }
                }
            }

            Console.WriteLine($"\n=== Repair Loop Run Complete ===");
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

        /// <summary>Processes one focal method with repair loop: up to maxAttempts of prompt-generate-gate1-gate2.</summary>
        private async Task<RunRecord> ProcessMethodWithRepairAsync(
            SampledMethod method,
            string repoPath,
            string projectName,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap,
            int maxAttempts)
        {
            var timestampStart = DateTime.UtcNow;

            Console.WriteLine("    Resetting repo to clean state...");
            await _git.ResetToCleanStateAsync(repoPath, commitHash);

            Console.WriteLine("    Discovering test project...");
            var testInfo = _discoveryService.DiscoverTestProject(repoPath, method.FilePath);
            if (testInfo == null)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: 0,
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

            string previousTest = "";
            string lastError = "";

            int cumulativePromptTokens = 0;
            int cumulativeCompletionTokens = 0;
            int cumulativeTotalTokens = 0;
            OpenAiResult? lastAiResult = null;
            PromptPair? lastPrompt = null;
            string lastPromptFile = "";
            BuildResult? lastBuildResult = null;
            TestResult? lastTestResult = null;

            var sanitizedId = SanitizeForFilename(method.Identifier);
            var generatedDir = Path.Combine(testInfo.TestProjectDir, "GeneratedTests");
            var generatedTestPath = Path.Combine(generatedDir, $"Generated__{sanitizedId}__C.cs");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Console.WriteLine($"\n    === Attempt {attempt}/{maxAttempts} ===");

                Console.WriteLine("    Resetting repo...");
                await _git.ResetToCleanStateAsync(repoPath, commitHash);
                Directory.CreateDirectory(generatedDir);

                PromptPair prompt;
                string promptFile;

                if (attempt == 1)
                {
                    Console.WriteLine("    Building single-shot prompt...");
                    prompt = _promptService.BuildSingleShotPrompt(
                        testInfo.Framework, testInfo.TestAttribute,
                        method.Namespace, method.TypeName, signature,
                        extraction.MethodBody, extraction.ClassContext);
                    promptFile = _promptService.SingleShotTemplateFile;
                }
                else
                {
                    Console.WriteLine($"    Building repair prompt (error from attempt {attempt - 1})...");
                    prompt = _promptService.BuildRepairPrompt(
                        testInfo.Framework, testInfo.TestAttribute,
                        method.Namespace, method.TypeName, signature,
                        extraction.MethodBody, previousTest, lastError);
                    promptFile = _promptService.RepairTemplateFile;
                }

                lastPrompt = prompt;
                lastPromptFile = promptFile;

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
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "api_error", stopReason: "api_error",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorMessage: ex.Message,
                        prompt: lastPrompt, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                lastAiResult = aiResult;
                cumulativePromptTokens += aiResult.PromptTokens;
                cumulativeCompletionTokens += aiResult.CompletionTokens;
                cumulativeTotalTokens += aiResult.TotalTokens;

                Console.WriteLine($"    Model: {aiResult.ModelId}, Tokens: {aiResult.TotalTokens} (cumulative: {cumulativeTotalTokens})");

                var code = aiResult.ExtractedCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.WriteLine("    No code block found in response.");
                    lastError = "No code block extracted from LLM response";
                    previousTest = aiResult.RawText;

                    if (attempt < maxAttempts)
                        continue;

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "llm_invalid_output", stopReason: "llm_invalid_output",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorSubcategory: "no_code_extracted",
                        errorMessage: "No code block found in LLM response",
                        prompt: lastPrompt, aiResult: lastAiResult, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                File.WriteAllText(generatedTestPath, code, Encoding.UTF8);
                Console.WriteLine($"    Wrote test file: {Path.GetFileName(generatedTestPath)}");

                var testQualifiedName = ExtractTestQualifiedName(code);
                var testFilter = testQualifiedName != null
                    ? $"FullyQualifiedName~{testQualifiedName}"
                    : null;

                if (testFilter != null)
                    Console.WriteLine($"    Test filter: {testFilter}");

                Console.WriteLine("    Gate 1: Building...");
                var buildResult = await _buildService.BuildAsync(repoPath);
                lastBuildResult = buildResult;
                bool buildPassed = buildResult.ExitCode == 0;
                Console.WriteLine($"    Gate 1: {(buildPassed ? "PASSED" : "FAILED")} (exit {buildResult.ExitCode})");

                if (!buildPassed)
                {
                    lastError = buildResult.Stderr;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "build_failed", stopReason: "max_attempts_reached_gate1",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "build", errorMessage: buildResult.Stderr,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, promptFile: promptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                Console.WriteLine("    Gate 2: Running tests...");
                var testResult = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                lastTestResult = testResult;
                bool testPassed = testResult.ExitCode == 0;
                Console.WriteLine($"    Gate 2: {(testPassed ? "PASSED" : "FAILED")} (exit {testResult.ExitCode})");

                if (!testPassed)
                {
                    lastError = !string.IsNullOrWhiteSpace(testResult.Stderr)
                        ? testResult.Stderr
                        : testResult.Stdout;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "max_attempts_reached_gate2",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: lastError,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: testResult, promptFile: promptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                Console.WriteLine($"    Attempt {attempt} PASSED both gates. Evaluating stability...");

                // Part 2: Flakiness check (no repo reset — generated test file must stay)
                var (isStablePass, flakiness, coverageDir, gate2Result) =
                    await EvaluateFlakinessAsync(repoPath, testFilter);

                if (gate2Result.ExitCode != 0)
                {
                    // Run 1 (with coverage) failed — treat as test_failed
                    var run1Error = !string.IsNullOrWhiteSpace(gate2Result.Stderr)
                        ? gate2Result.Stderr
                        : gate2Result.Stdout;

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "gate2_failed",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: run1Error,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness);
                }

                if (!isStablePass)
                {
                    // Flaky: Run 1 passed but Run 2 or 3 failed
                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "flaky_failure", stopReason: "flaky_test",
                        gate1Passed: true, gate2Passed: true,
                        errorCategory: "flakiness",
                        errorMessage: $"Inconsistent results: {string.Join(", ", flakiness.RunResults)}",
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness);
                }

                // Stable pass: collect coverage + mutation
                Console.WriteLine("    Parsing coverage...");
                var coverage = CollectCoverage(repoPath, method, coverageDir, gate2Result.Stdout);
                Console.WriteLine($"    Coverage: {coverage.LinePercent}% line, {coverage.BranchPercent}% branch (status: {coverage.Status})");

                Console.WriteLine("    Running Stryker mutation testing...");
                var cacheKey = $"{projectName}::{commitHash}::{method.FilePath}";
                var mutation = await CollectMutationAsync(repoPath, method, cacheKey);
                Console.WriteLine($"    Mutation: {mutation.MutationScore}% ({mutation.MutantsKilled}/{mutation.MutantsTotal})");

                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: attempt,
                    finalStatus: "completed", stopReason: "completed",
                    gate1Passed: true, gate2Passed: true,
                    prompt: lastPrompt, aiResult: lastAiResult,
                    buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                    previousTest: attempt > 1 ? previousTest : "",
                    errorFeedback: attempt > 1 ? lastError : "",
                    cumulativePromptTokens: cumulativePromptTokens,
                    cumulativeCompletionTokens: cumulativeCompletionTokens,
                    cumulativeTotalTokens: cumulativeTotalTokens,
                    coverage: coverage, mutation: mutation, flakiness: flakiness);
            }

            return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                maxAttempts: maxAttempts, attemptNumber: maxAttempts,
                finalStatus: "internal_error", stopReason: "unexpected_loop_exit",
                gate1Passed: false, gate2Passed: false,
                errorCategory: "internal", errorMessage: "Attempt loop exited unexpectedly");
        }

        private RunRecord BuildRecord(
            DateTime timestampStart,
            string projectName,
            SampledMethod method,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap,
            int maxAttempts,
            int attemptNumber,
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
            string promptFile = "",
            string previousTest = "",
            string errorFeedback = "",
            int cumulativePromptTokens = 0,
            int cumulativeCompletionTokens = 0,
            int cumulativeTotalTokens = 0,
            CoverageResult? coverage = null,
            MutationResult? mutation = null,
            FlakinessResult? flakiness = null)
        {
            var csvSignature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})";
            var effectivePromptFile = !string.IsNullOrEmpty(promptFile)
                ? promptFile
                : _promptService.SingleShotTemplateFile;

            return new RunRecord
            {
                RunId = $"C__{projectName}__{SanitizeForId(method.Identifier)}",
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
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    PromptVersion = _promptService.TemplateVersion,
                    PromptFile = effectivePromptFile
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
                        TemplateFile = effectivePromptFile,
                        TemplateVersion = _promptService.TemplateVersion,
                        PreviousTest = previousTest,
                        ErrorFeedback = errorFeedback
                    }
                    : new PromptInfo(),
                Response = aiResult != null
                    ? new ResponseInfo
                    {
                        RawText = aiResult.RawText,
                        ExtractedCode = aiResult.ExtractedCode,
                        TokensUsed = cumulativeTotalTokens,
                        PromptTokens = cumulativePromptTokens,
                        CompletionTokens = cumulativeCompletionTokens,
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

        /// <summary>Runs 3 test passes (1 with coverage, 2 without) to check stability.</summary>
        private async Task<(bool IsStablePass, FlakinessResult Flakiness, string CoverageDir, TestResult Gate2Result)>
            EvaluateFlakinessAsync(string repoPath, string? testFilter)
        {
            var results = new List<string>();

            // Run 1: with coverage collection
            Console.WriteLine("    Flakiness run 1 (with coverage)...");
            var (gate2, coverageDir) = await _testService.RunTestsWithCoverageAsync(repoPath, testFilter);
            var run1Result = gate2.ExitCode == 0 ? "pass" : "fail";
            results.Add(run1Result);
            Console.WriteLine($"    Flakiness run 1: {run1Result}");

            // Run 2 and 3: without coverage
            for (int i = 2; i <= 3; i++)
            {
                var rerun = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                var runResult = rerun.ExitCode == 0 ? "pass" : "fail";
                results.Add(runResult);
                Console.WriteLine($"    Flakiness run {i}: {runResult}");
            }

            var stable = results.Distinct().Count() == 1 && results[0] == "pass";
            var classification = stable ? "stable_pass" : "flaky";
            Console.WriteLine($"    Flakiness: {classification}");

            var flakiness = new FlakinessResult
            {
                RunsExecuted = 3,
                RunResults = results,
                Classification = classification
            };

            return (stable, flakiness, coverageDir, gate2);
        }

        /// <summary>Parses Coverlet/Cobertura coverage for the focal method.</summary>
        private CoverageResult CollectCoverage(string repoPath, SampledMethod method, string coverageDir, string? testStdout)
        {
            var coberturaFiles = _coverletService.FindCoberturaFiles(coverageDir);
            return _coverletService.ParseMethodCoverage(
                coberturaFiles, method.FilePath, method.LineStart, method.LineEnd, repoPath, testStdout);
        }

        /// <summary>Runs Stryker mutation testing for the focal method's source file.</summary>
        private async Task<MutationResult> CollectMutationAsync(
            string repoPath, SampledMethod method, string cacheKey)
        {
            try
            {
                var fileResult = await _strykerService.RunStrykerForFileAsync(
                    repoPath, method.FilePath, cacheKey);
                return _strykerService.ExtractMethodMutation(
                    fileResult, method.FilePath, method.LineStart, method.LineEnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker failed: {ex.Message}");
                return new MutationResult();
            }
        }

        private void WriteFailedRecord(
            string projectName, SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap, int maxAttempts, string finalStatus,
            string errorCategory, string errorMessage)
        {
            var record = BuildRecord(
                DateTime.UtcNow, projectName, method, repoUrl, commitHash, starsMap,
                maxAttempts: maxAttempts, attemptNumber: 0,
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
