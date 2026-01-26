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

        public SelectProjectsCommand(string reposDirectory = "repos")
        {
            _github = new GitHubClient(new ProductHeaderValue("ThesisExperiment"));
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
                Console.WriteLine($"[{i + 1}/{repositories.Count}] Evaluating {repo.Name}...");

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
                        Included = false,
                        ExclusionReason = $"evaluation_error: {ex.Message}"
                    });
                }
            }

            Console.WriteLine("Applying filters...");
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate.ExclusionReason))
                {
                    ApplyFilters(candidate);
                }
            }

            var fullPath = Path.Combine(outputPath, "project_list_full.csv");
            WriteCsv(candidates, fullPath);
            Console.WriteLine($"Wrote full list to {fullPath}");

            var filtered = candidates.Where(c => c.Included).ToList();
            var filteredPath = Path.Combine(outputPath, "project_list_filtered.csv");
            WriteCsv(filtered, filteredPath);
            Console.WriteLine($"Wrote filtered list to {filteredPath}");

            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Total evaluated: {candidates.Count}");
            Console.WriteLine($"Passed filters: {filtered.Count}");
            Console.WriteLine();
            Console.WriteLine("IMPORTANT: Commit these CSV files now.");
            Console.WriteLine("They are frozen and must not change.");
        }

        /// <summary>
        /// Search GitHub for C# repositories matching our criteria.
        /// </summary>
        private async Task<List<Repository>> SearchGitHubRepositories()
        {
            var request = new SearchRepositoriesRequest
            {
                Language = Language.CSharp,
                Stars = Octokit.Range.GreaterThan(MinStars),
                SortField = RepoSearchSort.Stars,
                Order = SortDirection.Descending
            };

            var result = await _github.Search.SearchRepo(request);

            return result.Items.Take(MaxCandidates).ToList();
        }

        /// <summary>
        /// Clone a repository and evaluate if it meets our criteria.
        /// </summary>
        private async Task<ProjectCandidate> EvaluateProject(Repository repo)
        {
            var candidate = new ProjectCandidate
            {
                RepoUrl = repo.HtmlUrl,
                CloneUrl = repo.CloneUrl,
                Name = repo.Name,
                Stars = repo.StargazersCount,
                License = repo.License?.Name ?? "Unknown",
                Language = repo.Language ?? "Unknown",
                LastCommit = repo.UpdatedAt.DateTime
            };

            var localPath = Path.Combine(_reposDir, repo.Name);
            candidate.LocalPath = localPath;

            Console.WriteLine($"  Cloning {repo.Name}...");
            await CloneRepository(repo.CloneUrl, localPath);

            candidate.CommitHash = await GetCurrentCommitHash(localPath);
            Console.WriteLine($"  Commit: {candidate.CommitHash}");

            candidate.HasTests = HasTestProjects(localPath);
            Console.WriteLine($"  Has tests: {candidate.HasTests}");

            Console.WriteLine($"  Building...");
            candidate.Builds = await TryBuild(localPath);
            Console.WriteLine($"  Builds: {candidate.Builds}");

            candidate.PublicMethodCount = CountPublicMethods(localPath);
            Console.WriteLine($"  Public methods: {candidate.PublicMethodCount}");

            candidate.LinesOfCode = CountLinesOfCode(localPath);
            Console.WriteLine($"  Lines of code: {candidate.LinesOfCode}");

            return candidate;
        }

        /// <summary>
        /// Clone a Git repository to a local path.
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

            return result.StandardOutput.Trim().Substring(0, 12);
        }

        /// <summary>
        /// Check if the repository contains test projects.
        /// </summary>
        private bool HasTestProjects(string localPath)
        {
            var csprojFiles = Directory.GetFiles(localPath, "*.csproj", SearchOption.AllDirectories);

            foreach (var file in csprojFiles)
            {
                var fileName = Path.GetFileName(file).ToLower();

                if (fileName.Contains("test") || fileName.Contains("tests"))
                {
                    return true;
                }

                var content = File.ReadAllText(file).ToLower();
                if (content.Contains("xunit") ||
                    content.Contains("nunit") ||
                    content.Contains("mstest") ||
                    content.Contains("microsoft.net.test.sdk"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Try to build the project with dotnet build.
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
                .WithArguments("build --no-restore")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return buildResult.ExitCode == 0;
        }

        /// <summary>
        /// Count public methods in C# files using Roslyn.
        /// Excludes test files.
        /// </summary>
        private int CountPublicMethods(string localPath)
        {
            var count = 0;
            var csFiles = Directory.GetFiles(localPath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                var filePath = file.ToLower();
                if (filePath.Contains("test") || filePath.Contains("spec"))
                {
                    continue;
                }

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
                    //TODO Skip files that cannot be parsed
                }
            }

            return count;
        }

        /// <summary>
        /// Count lines of code in C# files.
        /// Excludes test files.
        /// </summary>
        private int CountLinesOfCode(string localPath)
        {
            var count = 0;
            var csFiles = Directory.GetFiles(localPath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                var filePath = file.ToLower();
                if (filePath.Contains("test") || filePath.Contains("spec"))
                {
                    continue;
                }

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
                    //TODO Skip files that cannot be read
                }
            }

            return count;
        }

        /// <summary>
        /// Apply selection filters to a candidate.
        /// Sets Included to true or false and sets ExclusionReason if excluded.
        /// </summary>
        private void ApplyFilters(ProjectCandidate candidate)
        {
            if (!candidate.Builds)
            {
                candidate.Included = false;
                candidate.ExclusionReason = "build_failed";
                return;
            }

            if (!candidate.HasTests)
            {
                candidate.Included = false;
                candidate.ExclusionReason = "no_tests";
                return;
            }

            if (candidate.PublicMethodCount < MinPublicMethods)
            {
                candidate.Included = false;
                candidate.ExclusionReason = $"too_few_methods ({candidate.PublicMethodCount} < {MinPublicMethods})";
                return;
            }

            if (candidate.LinesOfCode < MinLinesOfCode)
            {
                candidate.Included = false;
                candidate.ExclusionReason = $"too_small ({candidate.LinesOfCode} < {MinLinesOfCode})";
                return;
            }

            candidate.Included = true;
            candidate.ExclusionReason = string.Empty;
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