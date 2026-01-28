using CliWrap;
using CliWrap.Buffered;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Text;
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    /// <summary>Selects 5 projects and samples 10 public methods each.</summary>
    public class SampleMethodsCommand
    {
        private const int Seed = 42;
        private const int ProjectCount = 5;
        private const int MethodsPerProject = 10;

        private static readonly HashSet<string> ExcludedMethodNames = new(StringComparer.Ordinal)
        {
            "Equals", "GetHashCode", "ToString"
        };

        /// <summary>Runs the sampling pipeline and writes CSV results.</summary>
        public async Task ExecuteAsync(string outputPath)
        {
            var filteredPath = Path.Combine(outputPath, "project_list_filtered.csv");
            Console.WriteLine($"Reading filtered projects from {filteredPath}...");
            var filteredProjects = ReadCsv<ProjectCandidate>(filteredPath);
            Console.WriteLine($"Found {filteredProjects.Count} filtered projects.");

            if (filteredProjects.Count < ProjectCount)
            {
                Console.WriteLine($"ERROR: Need at least {ProjectCount} projects, but only {filteredProjects.Count} available.");
                return;
            }

            Console.WriteLine($"Selecting {ProjectCount} projects (sort by repo_url, shuffle seed {Seed})...");
            var sorted = filteredProjects.OrderBy(p => p.RepoUrl, StringComparer.Ordinal).ToList();
            Shuffle(sorted, Seed);

            // Fix 1: Iterate through ALL shuffled projects until 5 valid ones are found
            var validProjects = new List<ProjectCandidate>();
            var allMethods = new List<SampledMethod>();
            int candidateIndex = 0;

            foreach (var project in sorted)
            {
                if (validProjects.Count >= ProjectCount)
                    break;

                candidateIndex++;
                Console.WriteLine($"\n[Candidate {candidateIndex}/{sorted.Count}] Processing {project.RepoUrl}...");

                if (!Directory.Exists(project.ClonePath))
                {
                    Console.WriteLine("  Clone not found, cloning...");
                    var cloneUrl = project.RepoUrl + ".git";
                    await CloneRepository(cloneUrl, project.ClonePath);
                }

                // Fix 2: CSV Commit Hash als Source of Truth
                string commitHash;
                if (!string.IsNullOrEmpty(project.CommitHash) && project.CommitHash != "unknown")
                {
                    commitHash = project.CommitHash;
                    var currentHead = await GetCommitHash(project.ClonePath);

                    if (currentHead != commitHash)
                    {
                        Console.WriteLine($"  HEAD ({currentHead[..8]}) differs from CSV ({commitHash[..8]}), checking out...");
                        await EnsureCommitAvailable(project.ClonePath, commitHash);
                    }
                    else
                    {
                        Console.WriteLine($"  Commit: {commitHash[..8]} (matches CSV)");
                    }
                }
                else
                {
                    commitHash = await GetCommitHash(project.ClonePath);
                    project.CommitHash = commitHash;
                    Console.WriteLine($"  Commit (from HEAD, no CSV value): {commitHash[..8]}");
                }

                var projectName = project.RepoUrl.Split('/').Last();

                Console.WriteLine("  Extracting public methods...");
                var methods = ExtractPublicMethods(project.ClonePath, projectName, project.RepoUrl, commitHash);
                Console.WriteLine($"  Found {methods.Count} eligible public methods.");

                if (methods.Count < MethodsPerProject)
                {
                    Console.WriteLine($"  SKIPPED: Only {methods.Count} methods, need {MethodsPerProject}. Trying next candidate...");
                    continue;
                }

                var sortedMethods = methods.OrderBy(m => m.Identifier, StringComparer.Ordinal).ToList();
                Shuffle(sortedMethods, Seed);
                var sampled = sortedMethods.Take(MethodsPerProject).ToList();

                Console.WriteLine($"  ACCEPTED: Sampled {sampled.Count} methods:");
                foreach (var m in sampled)
                    Console.WriteLine($"    - {m.Identifier} ({m.FilePath}:{m.LineStart}-{m.LineEnd})");

                validProjects.Add(project);
                allMethods.AddRange(sampled);
            }

            if (validProjects.Count < ProjectCount)
            {
                Console.WriteLine($"\nERROR: Only {validProjects.Count} valid projects found out of {sorted.Count} candidates.");
                Console.WriteLine($"Need at least {ProjectCount} projects with {MethodsPerProject}+ methods each.");
                return;
            }

            Console.WriteLine($"\n=== Selected {validProjects.Count} Projects ===");
            foreach (var p in validProjects)
                Console.WriteLine($"  - {p.RepoUrl}");

            var selectedPath = Path.Combine(outputPath, "project_list_selected.csv");
            WriteCsv(validProjects, selectedPath);
            Console.WriteLine($"\nWrote {validProjects.Count} projects to {selectedPath}");

            var methodsPath = Path.Combine(outputPath, "method_list_all.csv");
            WriteCsv(allMethods, methodsPath);
            Console.WriteLine($"Wrote {allMethods.Count} methods to {methodsPath}");

            Console.WriteLine("\n=== Validation ===");
            Console.WriteLine($"Projects selected: {validProjects.Count} (expected: {ProjectCount})");
            Console.WriteLine($"Methods sampled:   {allMethods.Count} (expected: {ProjectCount * MethodsPerProject})");

            var grouped = allMethods.GroupBy(m => m.ProjectName).ToList();
            foreach (var g in grouped)
                Console.WriteLine($"  {g.Key}: {g.Count()} methods");

            // Fix 3: Duplicate Check auf (RepoUrl, Identifier) statt nur Identifier
            var duplicates = allMethods
                .GroupBy(m => (m.RepoUrl, m.Identifier))
                .Where(g => g.Count() > 1)
                .ToList();
            if (duplicates.Count > 0)
            {
                Console.WriteLine($"WARNING: {duplicates.Count} duplicate identifiers found!");
                foreach (var d in duplicates)
                    Console.WriteLine($"  Duplicate: {d.Key.Identifier} in {d.Key.RepoUrl}");
            }
            else
            {
                Console.WriteLine("No duplicate identifiers. OK");
            }

            if (validProjects.Count == ProjectCount && allMethods.Count == ProjectCount * MethodsPerProject && duplicates.Count == 0)
                Console.WriteLine("\nAll validation checks passed.");
            else
                Console.WriteLine("\nWARNING: Some validation checks failed. Review output above.");

            Console.WriteLine("\nIMPORTANT: Commit these CSV files now.");
            Console.WriteLine("They are frozen and must not change.");
        }

        private List<SampledMethod> ExtractPublicMethods(
            string clonePath, string projectName, string repoUrl, string commitHash)
        {
            var methods = new List<SampledMethod>();
            var csFiles = Directory.GetFiles(clonePath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                if (ShouldExcludeFile(file))
                    continue;

                try
                {
                    var code = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(code);
                    var root = tree.GetRoot();

                    var methodDeclarations = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Modifiers.Any(mod => mod.Text == "public"))
                        .Where(m => !ExcludedMethodNames.Contains(m.Identifier.Text))
                        .Where(IsInPublicTypeHierarchy);

                    foreach (var method in methodDeclarations)
                    {
                        var ns = GetNamespace(method);
                        var typeName = GetContainingTypeName(method);
                        var methodName = method.Identifier.Text;
                        var genericSuffix = method.TypeParameterList?.ToString() ?? "";
                        var paramTypes = string.Join(",",
                            method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
                        var identifier = string.IsNullOrEmpty(ns)
                            ? $"{typeName}.{methodName}{genericSuffix}({paramTypes})"
                            : $"{ns}.{typeName}.{methodName}{genericSuffix}({paramTypes})";

                        var lineSpan = method.GetLocation().GetLineSpan();
                        var relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

                        methods.Add(new SampledMethod
                        {
                            ProjectName = projectName,
                            RepoUrl = repoUrl,
                            CommitHash = commitHash,
                            Namespace = ns,
                            TypeName = typeName,
                            MethodName = methodName,
                            Identifier = identifier,
                            FilePath = relativePath,
                            LineStart = lineSpan.StartLinePosition.Line + 1,
                            LineEnd = lineSpan.EndLinePosition.Line + 1,
                            ReturnType = method.ReturnType.ToString(),
                            ParameterTypes = paramTypes,
                            IsStatic = method.Modifiers.Any(mod => mod.Text == "static"),
                            IsAsync = method.Modifiers.Any(mod => mod.Text == "async")
                        });
                    }
                }
                catch
                {
                }
            }

            return methods;
        }

        private static bool IsInPublicTypeHierarchy(MethodDeclarationSyntax method)
        {
            var parent = method.Parent;
            if (parent is not TypeDeclarationSyntax)
                return false;

            while (parent is TypeDeclarationSyntax type)
            {
                if (!type.Modifiers.Any(mod => mod.Text == "public"))
                    return false;
                parent = parent.Parent;
            }

            return true;
        }

        private static string GetNamespace(Microsoft.CodeAnalysis.SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                if (current is BaseNamespaceDeclarationSyntax ns)
                    return ns.Name.ToString();
                current = current.Parent;
            }
            return string.Empty;
        }

        private static string GetContainingTypeName(Microsoft.CodeAnalysis.SyntaxNode node)
        {
            var parts = new List<string>();
            var current = node.Parent;

            while (current is TypeDeclarationSyntax type)
            {
                var name = type.Identifier.Text;
                if (type.TypeParameterList != null)
                    name += type.TypeParameterList.ToString();
                parts.Add(name);
                current = current.Parent;
            }

            parts.Reverse();
            return string.Join(".", parts);
        }

        private static bool ShouldExcludeFile(string filePath)
        {
            var normalized = filePath.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            foreach (var seg in segments)
            {
                var s = seg.ToLowerInvariant();

                // Standard excludes
                if (s is "bin" or "obj" or "generated" or "migrations")
                    return true;

                // Robust test detection: unittest, *.tests, *tests, *.test, *test
                if (s.Contains("unittest") ||
                    s.EndsWith("tests") ||
                    s.EndsWith("test") ||
                    s.Contains(".tests") ||
                    s.Contains(".test"))
                    return true;

                // Spec detection
                if (s is "spec" or "specs" ||
                    s.EndsWith("spec") ||
                    s.EndsWith("specs") ||
                    s.Contains(".spec"))
                    return true;
            }

            return false;
        }

        private static void Shuffle<T>(List<T> list, int seed)
        {
            var rng = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private async Task CloneRepository(string cloneUrl, string localPath)
        {
            var parentDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            var result = await Cli.Wrap("git")
                .WithArguments($"clone --depth 1 {cloneUrl} {localPath}")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
                throw new Exception($"Git clone failed: {result.StandardError}");
        }

        private async Task<string> GetCommitHash(string localPath)
        {
            var result = await Cli.Wrap("git")
                .WithArguments("rev-parse HEAD")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return result.ExitCode == 0
                ? result.StandardOutput.Trim()
                : "unknown";
        }

        private async Task EnsureCommitAvailable(string localPath, string commitHash)
        {
            // Check if commit is already available locally
            var checkResult = await Cli.Wrap("git")
                .WithArguments($"cat-file -t {commitHash}")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (checkResult.ExitCode != 0)
            {
                // Commit not available in shallow clone → need to fetch full history
                Console.WriteLine($"  Fetching full history (commit not in shallow clone)...");
                await Cli.Wrap("git")
                    .WithArguments("fetch --unshallow")
                    .WithWorkingDirectory(localPath)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }

            // Checkout the specific commit
            var result = await Cli.Wrap("git")
                .WithArguments($"checkout  --detach {commitHash}")
                .WithWorkingDirectory(localPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode == 0)
                Console.WriteLine($"  Checked out {commitHash[..8]} successfully.");
            else
                throw new Exception($"Failed to checkout {commitHash}: {result.StandardError}");
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

        private void WriteCsv<T>(List<T> records, string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.WriteRecords(records);
        }
    }
}
