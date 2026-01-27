using System.Text.RegularExpressions;

namespace ThesisExperiment.Services
{
    /// <summary>Discovered test project metadata.</summary>
    public class TestProjectInfo
    {
        public string TestProjectPath { get; set; } = string.Empty;
        public string TestProjectDir { get; set; } = string.Empty;
        public string Framework { get; set; } = string.Empty;
        public string TestAttribute { get; set; } = string.Empty;
        public string ProductionProjectPath { get; set; } = string.Empty;
    }

    /// <summary>Finds the test project for a given source file.</summary>
    public class TestProjectDiscoveryService
    {
        /// <summary>Locates the test .csproj referencing the focal file's project.</summary>
        public TestProjectInfo? DiscoverTestProject(
            string repoRoot, string focalFileRelativePath)
        {
            var absoluteFocalPath = Path.Combine(repoRoot, focalFileRelativePath);
            var productionCsproj = FindNearestCsproj(absoluteFocalPath);

            if (productionCsproj == null)
            {
                Console.WriteLine($"    No .csproj found for {focalFileRelativePath}");
                return null;
            }

            var allCsprojs = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories);

            var testCsproj = FindTestProjectByReference(allCsprojs, productionCsproj);

            if (testCsproj == null)
                testCsproj = FindTestProjectByConvention(allCsprojs, productionCsproj);

            if (testCsproj == null)
            {
                Console.WriteLine($"    No test project found for {Path.GetFileName(productionCsproj)}");
                return null;
            }

            var (framework, attribute) = DetectTestFramework(testCsproj);

            return new TestProjectInfo
            {
                TestProjectPath = testCsproj,
                TestProjectDir = Path.GetDirectoryName(testCsproj)!,
                Framework = framework,
                TestAttribute = attribute,
                ProductionProjectPath = productionCsproj
            };
        }

        private static string? FindNearestCsproj(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                var csprojs = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
                if (csprojs.Length > 0)
                    return csprojs.OrderBy(f => f).First();

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static string? FindTestProjectByReference(
            string[] allCsprojs, string productionCsproj)
        {
            var productionFullPath = Path.GetFullPath(productionCsproj)
                .Replace('\\', '/');

            var matches = new List<string>();

            foreach (var csproj in allCsprojs)
            {
                if (Path.GetFullPath(csproj).Equals(
                    Path.GetFullPath(productionCsproj), StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var content = File.ReadAllText(csproj);

                    if (!IsTestProject(csproj, content))
                        continue;

                    var references = Regex.Matches(content,
                        @"<ProjectReference\s+Include=""([^""]+)""",
                        RegexOptions.IgnoreCase);

                    foreach (Match refMatch in references)
                    {
                        var refPath = refMatch.Groups[1].Value;
                        var csprojDir = Path.GetDirectoryName(csproj)!;

                        var resolvedPath = Path.GetFullPath(
                            Path.Combine(csprojDir, refPath)).Replace('\\', '/');

                        if (resolvedPath.Equals(productionFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(csproj);
                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            return matches.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private static string? FindTestProjectByConvention(
            string[] allCsprojs, string productionCsproj)
        {
            var productionName = Path.GetFileNameWithoutExtension(productionCsproj);

            var testProjects = allCsprojs
                .Where(c => !Path.GetFullPath(c).Equals(
                    Path.GetFullPath(productionCsproj), StringComparison.OrdinalIgnoreCase))
                .Where(c =>
                {
                    try { return IsTestProject(c, File.ReadAllText(c)); }
                    catch { return false; }
                })
                .OrderBy(c =>
                {
                    var name = Path.GetFileNameWithoutExtension(c);
                    if (name.Contains(productionName, StringComparison.OrdinalIgnoreCase))
                        return 0;
                    return 1;
                })
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return testProjects.FirstOrDefault();
        }

        private static bool IsTestProject(string csprojPath, string content)
        {
            var fileName = Path.GetFileName(csprojPath).ToLowerInvariant();
            var dirName = Path.GetFileName(Path.GetDirectoryName(csprojPath) ?? "").ToLowerInvariant();
            var lowerContent = content.ToLowerInvariant();

            if (fileName.Contains("test") || dirName.Contains("test") ||
                fileName.Contains("spec") || dirName.Contains("spec"))
                return true;

            if (lowerContent.Contains("xunit") ||
                lowerContent.Contains("nunit") ||
                lowerContent.Contains("mstest") ||
                lowerContent.Contains("microsoft.net.test.sdk"))
                return true;

            return false;
        }

        private static (string Framework, string TestAttribute) DetectTestFramework(
            string testCsprojPath)
        {
            try
            {
                var content = File.ReadAllText(testCsprojPath).ToLowerInvariant();

                if (content.Contains("xunit"))
                    return ("xUnit", "Fact");

                if (content.Contains("nunit"))
                    return ("NUnit", "Test");

                if (content.Contains("mstest") ||
                    content.Contains("microsoft.visualstudio.testplatform"))
                    return ("MSTest", "TestMethod");
            }
            catch
            {
            }

            return ("xUnit", "Fact");
        }
    }
}
