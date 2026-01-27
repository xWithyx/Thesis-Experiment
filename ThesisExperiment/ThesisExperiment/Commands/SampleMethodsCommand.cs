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
    public class SampleMethodsCommand
    {
        private const int Seed = 42;
        private const int ProjectCount = 5;
        private const int MethodsPerProject = 10;

        private static readonly HashSet<string> ExcludedMethodNames = new(StringComparer.Ordinal)
        {
            "Equals", "GetHashCode", "ToString"
        };

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

            // Select 5 projects: sort by repo_url, then shuffle with seed 42
            Console.WriteLine($"Selecting {ProjectCount} projects (sort by repo_url, shuffle seed {Seed})...");
            var sorted = filteredProjects.OrderBy(p => p.RepoUrl, StringComparer.Ordinal).ToList();
            Shuffle(sorted, Seed);
            var selected = sorted.Take(ProjectCount).ToList();

            Console.WriteLine("Selected projects:");
            foreach (var p in selected)
                Console.WriteLine($"  - {p.RepoUrl}");

            // For each project: ensure clone exists, extract and sample methods
            var allMethods = new List<SampledMethod>();

            for (int i = 0; i < selected.Count; i++)
            {
                var project = selected[i];
                Console.WriteLine($"\n[{i + 1}/{selected.Count}] Processing {project.RepoUrl}...");

                // Ensure repo is cloned
                if (!Directory.Exists(project.ClonePath))
                {
                    Console.WriteLine("  Clone not found, cloning...");
                    var cloneUrl = project.RepoUrl + ".git";
                    await CloneRepository(cloneUrl, project.ClonePath);
                }

                // Get current commit hash from the clone
                var commitHash = await GetCommitHash(project.ClonePath);
                project.CommitHash = commitHash;
                Console.WriteLine($"  Commit: {commitHash}");

                // Derive project name from repo URL (last segment)
                var projectName = project.RepoUrl.Split('/').Last();

                // Extract all eligible public methods via Roslyn
                Console.WriteLine("  Extracting public methods...");
                var methods = ExtractPublicMethods(project.ClonePath, projectName, project.RepoUrl, commitHash);
                Console.WriteLine($"  Found {methods.Count} eligible public methods.");

                if (methods.Count < MethodsPerProject)
                {
                    Console.WriteLine($"  WARNING: Only {methods.Count} methods found, need {MethodsPerProject}. Project skipped.");
                    continue;
                }

                // Sample 10 methods: sort by identifier, then shuffle with seed 42
                var sortedMethods = methods.OrderBy(m => m.Identifier, StringComparer.Ordinal).ToList();
                Shuffle(sortedMethods, Seed);
                var sampled = sortedMethods.Take(MethodsPerProject).ToList();

                Console.WriteLine($"  Sampled {sampled.Count} methods:");
                foreach (var m in sampled)
                    Console.WriteLine($"    - {m.Identifier} ({m.FilePath}:{m.LineStart}-{m.LineEnd})");

                allMethods.AddRange(sampled);
            }

            // Write project_list_selected.csv
            var selectedPath = Path.Combine(outputPath, "project_list_selected.csv");
            WriteCsv(selected, selectedPath);
            Console.WriteLine($"\nWrote {selected.Count} projects to {selectedPath}");

            // Write method_list_all.csv
            var methodsPath = Path.Combine(outputPath, "method_list_all.csv");
            WriteCsv(allMethods, methodsPath);
            Console.WriteLine($"Wrote {allMethods.Count} methods to {methodsPath}");

            // Validation
            Console.WriteLine("\n=== Validation ===");
            Console.WriteLine($"Projects selected: {selected.Count} (expected: {ProjectCount})");
            Console.WriteLine($"Methods sampled:   {allMethods.Count} (expected: {ProjectCount * MethodsPerProject})");

            var grouped = allMethods.GroupBy(m => m.ProjectName).ToList();
            foreach (var g in grouped)
                Console.WriteLine($"  {g.Key}: {g.Count()} methods");

            var duplicates = allMethods.GroupBy(m => m.Identifier).Where(g => g.Count() > 1).ToList();
            if (duplicates.Count > 0)
            {
                Console.WriteLine($"WARNING: {duplicates.Count} duplicate identifiers found!");
                foreach (var d in duplicates)
                    Console.WriteLine($"  Duplicate: {d.Key}");
            }
            else
            {
                Console.WriteLine("No duplicate identifiers. OK");
            }

            if (allMethods.Count == ProjectCount * MethodsPerProject && duplicates.Count == 0)
                Console.WriteLine("\nAll validation checks passed.");
            else
                Console.WriteLine("\nWARNING: Some validation checks failed. Review output above.");

            Console.WriteLine("\nIMPORTANT: Commit these CSV files now.");
            Console.WriteLine("They are frozen and must not change.");
        }

        /// <summary>
        /// Extract all eligible public methods from a cloned repository using Roslyn.
        /// </summary>
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
                            LineStart = lineSpan.StartLinePosition.Line + 1, // 1-based
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
                    // Skip files that cannot be parsed
                }
            }

            return methods;
        }

        /// <summary>
        /// Check if a method is inside a fully public type hierarchy.
        /// All containing types must be public.
        /// </summary>
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

        /// <summary>
        /// Get the namespace containing a syntax node.
        /// Handles both classic and file-scoped namespaces.
        /// </summary>
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

        /// <summary>
        /// Get the containing type name, including nested type path (e.g., "Outer.Inner").
        /// Includes generic type parameters.
        /// </summary>
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

        /// <summary>
        /// Check if a file path should be excluded from analysis.
        /// </summary>
        private static bool ShouldExcludeFile(string filePath)
        {
            var normalized = filePath.Replace('\\', '/').ToLower();
            return normalized.Contains("/bin/") ||
                   normalized.Contains("/obj/") ||
                   normalized.Contains("/test") ||
                   normalized.Contains("/spec") ||
                   normalized.Contains("/generated/") ||
                   normalized.Contains("/migrations/");
        }

        /// <summary>
        /// Fisher-Yates shuffle with a fixed seed for reproducibility.
        /// </summary>
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
