using CliWrap;
using CliWrap.Buffered;
using CsvHelper;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Octokit;
using System.Globalization;
using System.Text;
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    public class SelectProjectsCommand
    {
        private const int MinStars = 50;
        private const int MaxCandidates = 100;
        private const int MinPublicMethods = 10;
        private const int MinLinesOfCode = 1000;

        private readonly GitHubClient _github;
        private readonly string _reposDir;

        private record TestDetectionResult(bool HasTests, int TestProjectsCount, string TestFrameworks);

        public SelectProjectsCommand(string reposDirectory = "repos")
        {
            _github = new GitHubClient(new ProductHeaderValue("ThesisExperiment"));

            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                _github.Credentials = new Octokit.Credentials(token);
                Console.WriteLine("Using authenticated GitHub access (GITHUB_TOKEN).");
            }
            else
            {
                Console.WriteLine("Warning: No GITHUB_TOKEN set. Using unauthenticated access (60 req/hr limit).");
            }

            _reposDir = reposDirectory;
            if (!Directory.Exists(_reposDir))
            {
                Directory.CreateDirectory(_reposDir);
            }
        }

        /// <summary>
        /// Main entry point. Searches GitHub, evaluates projects, writes CSV files.
        /// </summary>
        public async Task ExecuteAsync(string outputPath)
        {
            Console.WriteLine("Starting project selection...");

            Console.WriteLine("Searching GitHub for C# repositories...");
            var repositories = await SearchGitHubRepositories();
            Console.WriteLine($"Found {repositories.Count} repositories.");

            var candidates = new List<ProjectCandidate>();

            for (int i = 0; i < repositories.Count; i++)
            {
                var repo = repositories[i];
                Console.WriteLine($"[{i + 1}/{repositories.Count}] Evaluating {repo.FullName}...");

                try
                {
                    var candidate = await EvaluateProject(repo);
                    candidates.Add(candidate);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error: {ex.Message}");
                    candidates.Add(new ProjectCandidate
                    {
                        RepoUrl = repo.HtmlUrl,
                        Name = repo.Name,
                        Stars = repo.StargazersCount,
                        DefaultBranch = repo.DefaultBranch ?? "unknown",
                        PassedFilters = false,
                        FailReasons = $"evaluation_error:{ex.Message}"
                    });
                }
            }

            Console.WriteLine("Applying filters...");
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate.FailReasons))
                {
                    ApplyFilters(candidate);
                }
            }

            var fullPath = Path.Combine(outputPath, "project_list_full.csv");
            WriteCsv(candidates, fullPath);
            Console.WriteLine($"Wrote full list to {fullPath}");

            var filtered = candidates.Where(c => c.PassedFilters).ToList();
            var filteredPath = Path.Combine(outputPath, "project_list_filtered.csv");
            WriteCsv(filtered, filteredPath);
            Console.WriteLine($"Wrote filtered list to {filteredPath}");

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Total evaluated: {candidates.Count}");
            Console.WriteLine($"Passed filters:  {filtered.Count}");
            Console.WriteLine();
            Console.WriteLine("IMPORTANT: Commit these CSV files now.");
            Console.WriteLine("They are frozen and must not change.");
        }

        /// <summary>
        /// Search GitHub for C# repositories matching our criteria.
        /// Excludes forks and archived repos.
        /// </summary>
        private async Task<List<Repository>> SearchGitHubRepositories()
        {
            var allRepos = new List<Repository>();

            var request = new SearchRepositoriesRequest
            {
                Language = Language.CSharp,
                Stars = Octokit.Range.GreaterThan(MinStars),
                SortField = RepoSearchSort.Stars,
                Order = SortDirection.Descending,
                PerPage = 100,
                Page = 1
            };

            while (allRepos.Count < MaxCandidates)
            {
                await CheckRateLimit();

                var result = await _github.Search.SearchRepo(request);
                if (result.Items.Count == 0)
                    break;

                // Post-filter: exclude forks and archived repos
                var valid = result.Items
                    .Where(r => !r.Fork && !r.Archived)
                    .ToList();

                allRepos.AddRange(valid);

                if (result.Items.Count < request.PerPage)
                    break;

                request.Page++;
            }

            return allRepos.Take(MaxCandidates).ToList();
        }

        /// <summary>
        /// Check GitHub API rate limit and wait if necessary.
        /// </summary>
        private async Task CheckRateLimit()
        {
            var apiInfo = _github.GetLastApiInfo();
            if (apiInfo?.RateLimit == null)
                return;

            var remaining = apiInfo.RateLimit.Remaining;
            var reset = apiInfo.RateLimit.Reset;

            if (remaining <= 1)
            {
                var waitTime = reset - DateTimeOffset.UtcNow;
                if (waitTime > TimeSpan.Zero)
                {
                    Console.WriteLine($"  Rate limit nearly exhausted. Waiting {waitTime.TotalSeconds:F0}s until reset...");
                    await Task.Delay(waitTime + TimeSpan.FromSeconds(2));
                }
            }
            else if (remaining <= 5)
            {
                Console.WriteLine($"  Warning: Only {remaining} API requests remaining.");
            }
        }

        /// <summary>
        /// Clone a repository and evaluate if it meets our criteria.
        /// </summary>
        private async Task<ProjectCandidate> EvaluateProject(Repository repo)
        {
            var dirName = $"{repo.Owner.Login}__{repo.Name}";
            var localPath = Path.Combine(_reposDir, dirName);

            var candidate = new ProjectCandidate
            {
                RepoUrl = repo.HtmlUrl,
                CloneUrl = repo.CloneUrl,
                Name = repo.Name,
                Stars = repo.StargazersCount,
                DefaultBranch = repo.DefaultBranch ?? "main",
                ClonePath = localPath
            };

            Console.WriteLine($"  Cloning {repo.FullName}...");
            await CloneRepository(repo.CloneUrl, localPath);

            candidate.CommitHash = await GetCurrentCommitHash(localPath);
            Console.WriteLine($"  Commit: {candidate.CommitHash}");

            var testResult = DetectTestProjects(localPath);
            candidate.HasTests = testResult.HasTests;
            candidate.TestProjectsCount = testResult.TestProjectsCount;
            candidate.TestFrameworks = testResult.TestFrameworks;
            Console.WriteLine($"  Tests: {candidate.HasTests} ({candidate.TestProjectsCount} projects, frameworks: {candidate.TestFrameworks})");

            Console.WriteLine($"  Building (Release)...");
            candidate.BuildSuccess = await TryBuild(localPath);
            Console.WriteLine($"  Build success: {candidate.BuildSuccess}");

            candidate.PublicMethodCount = CountPublicMethods(localPath);
            Console.WriteLine($"  Public methods: {candidate.PublicMethodCount}");

            candidate.LocCs = CountLinesOfCode(localPath);
            Console.WriteLine($"  LOC (C#): {candidate.LocCs}");

            return candidate;
        }

        /// <summary>
        /// Clone a Git repository to a local path (shallow clone).
        /// </summary>
        private async Task CloneRepository(string cloneUrl, string localPath)
        {
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, recursive: true);
            }

            var result = await Cli.Wrap("git")
                .WithArguments($"clone --depth 1 {cloneUrl} {localPath}")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                throw new Exception($"Git clone failed: {result.StandardError}");
            }
        }

        /// <summary>
        /// Get the current commit hash of a repository.
        /// </summary>
        private async Task<string> GetCurrentCommitHash(string localPath)
        {
            var result = await Cli.Wrap("git")
                .WithArguments("rev-parse HEAD")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                return "unknown";
            }

            return result.StandardOutput.Trim();
        }

        /// <summary>
        /// Detect test projects: count them and identify frameworks.
        /// </summary>
        private TestDetectionResult DetectTestProjects(string localPath)
        {
            var csprojFiles = Directory.GetFiles(localPath, "*.csproj", SearchOption.AllDirectories);
            int testProjectCount = 0;
            var frameworksFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in csprojFiles)
            {
                var fileName = Path.GetFileName(file).ToLower();
                var content = File.ReadAllText(file).ToLower();
                bool isTestProject = false;

                if (fileName.Contains("test") || fileName.Contains("tests"))
                {
                    isTestProject = true;
                }

                if (content.Contains("xunit"))
                {
                    frameworksFound.Add("xunit");
                    isTestProject = true;
                }
                if (content.Contains("nunit"))
                {
                    frameworksFound.Add("nunit");
                    isTestProject = true;
                }
                if (content.Contains("mstest") || content.Contains("microsoft.visualstudio.testplatform"))
                {
                    frameworksFound.Add("mstest");
                    isTestProject = true;
                }
                if (content.Contains("microsoft.net.test.sdk"))
                {
                    isTestProject = true;
                }

                if (isTestProject)
                    testProjectCount++;
            }

            var frameworks = string.Join(",", frameworksFound.OrderBy(f => f));
            return new TestDetectionResult(testProjectCount > 0, testProjectCount, frameworks);
        }

        /// <summary>
        /// Try to build the project with dotnet build (Release configuration).
        /// </summary>
        private async Task<bool> TryBuild(string localPath)
        {
            var restoreResult = await Cli.Wrap("dotnet")
                .WithArguments("restore")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (restoreResult.ExitCode != 0)
            {
                return false;
            }

            var buildResult = await Cli.Wrap("dotnet")
                .WithArguments("build --no-restore -c Release")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return buildResult.ExitCode == 0;
        }

        /// <summary>
        /// Check if a file path should be excluded from analysis.
        /// Excludes test/spec directories and build output.
        /// </summary>
        private static bool ShouldExcludeFile(string filePath)
        {
            var normalized = filePath.Replace('\\', '/').ToLower();
            return normalized.Contains("/test") ||
                   normalized.Contains("/spec") ||
                   normalized.Contains("/bin/") ||
                   normalized.Contains("/obj/");
        }

        /// <summary>
        /// Count public methods in C# production files using Roslyn.
        /// </summary>
        private int CountPublicMethods(string localPath)
        {
            var count = 0;
            var csFiles = Directory.GetFiles(localPath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                if (ShouldExcludeFile(file))
                    continue;

                try
                {
                    var code = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(code);
                    var root = tree.GetRoot();

                    var methods = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Modifiers.Any(mod => mod.Text == "public"));

                    count += methods.Count();
                }
                catch
                {
                    // Skip files that cannot be parsed
                }
            }

            return count;
        }

        /// <summary>
        /// Count non-empty, non-comment lines in C# production files.
        /// </summary>
        private int CountLinesOfCode(string localPath)
        {
            var count = 0;
            var csFiles = Directory.GetFiles(localPath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                if (ShouldExcludeFile(file))
                    continue;

                try
                {
                    var lines = File.ReadAllLines(file);

                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) &&
                            !trimmed.StartsWith("//") &&
                            !trimmed.StartsWith("/*") &&
                            !trimmed.StartsWith("*"))
                        {
                            count++;
                        }
                    }
                }
                catch
                {
                    // Skip files that cannot be read
                }
            }

            return count;
        }

        /// <summary>
        /// Apply selection filters to a candidate.
        /// Collects ALL fail reasons (pipe-separated).
        /// </summary>
        private void ApplyFilters(ProjectCandidate candidate)
        {
            var reasons = new List<string>();

            if (!candidate.BuildSuccess)
                reasons.Add("build_failed");

            if (!candidate.HasTests)
                reasons.Add("no_tests");

            if (candidate.PublicMethodCount < MinPublicMethods)
                reasons.Add($"too_few_methods({candidate.PublicMethodCount}<{MinPublicMethods})");

            if (candidate.LocCs < MinLinesOfCode)
                reasons.Add($"loc_too_low({candidate.LocCs}<{MinLinesOfCode})");

            candidate.PassedFilters = reasons.Count == 0;
            candidate.FailReasons = string.Join("|", reasons);
        }

        /// <summary>
        /// Write a list of records to a CSV file.
        /// </summary>
        private void WriteCsv<T>(List<T> records, string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.WriteRecords(records);
        }
    }
}
