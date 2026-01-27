using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ThesisExperiment.Commands
{
    /// <summary>Parses Coverlet/Cobertura XML for method-level coverage.</summary>
    public class CoverletService
    {
        /// <summary>Finds all coverage.cobertura.xml files in a directory.</summary>
        public List<string> FindCoberturaFiles(string coverageResultsDir)
        {
            if (!Directory.Exists(coverageResultsDir))
                return new List<string>();

            return Directory.GetFiles(coverageResultsDir, "coverage.cobertura.xml",
                SearchOption.AllDirectories).ToList();
        }

        /// <summary>Computes line and branch coverage for a method's line range.</summary>
        public CoverageResult ParseMethodCoverage(
            List<string> coberturaFiles,
            string methodFilePath,
            int lineStart,
            int lineEnd,
            string repoRootPath)
        {
            var normalizedMethodPath = methodFilePath.Replace('\\', '/');

            var absoluteCandidate = Path.Combine(repoRootPath, methodFilePath)
                .Replace('\\', '/');

            var lineData = new Dictionary<int, LineCoverageInfo>();

            foreach (var coberturaFile in coberturaFiles)
            {
                try
                {
                    var doc = XDocument.Load(coberturaFile);
                    var classes = doc.Descendants("class");

                    foreach (var cls in classes)
                    {
                        var filename = cls.Attribute("filename")?.Value ?? "";
                        var normalizedFilename = filename.Replace('\\', '/');

                        bool matches = EndsWithOnBoundary(normalizedFilename, normalizedMethodPath)
                            || EndsWithOnBoundary(normalizedMethodPath, normalizedFilename)
                            || normalizedFilename.Equals(absoluteCandidate, StringComparison.OrdinalIgnoreCase);

                        if (!matches)
                            continue;

                        var lines = cls.Descendants("line");
                        foreach (var line in lines)
                        {
                            var numberAttr = line.Attribute("number");
                            var hitsAttr = line.Attribute("hits");
                            if (numberAttr == null || hitsAttr == null)
                                continue;

                            int lineNumber = int.Parse(numberAttr.Value);
                            if (lineNumber < lineStart || lineNumber > lineEnd)
                                continue;

                            int hits = int.Parse(hitsAttr.Value);
                            bool isBranch = line.Attribute("branch")?.Value == "true";

                            int coveredBranches = 0;
                            int totalBranches = 0;
                            if (isBranch)
                            {
                                var condCov = line.Attribute("condition-coverage")?.Value;
                                if (condCov != null)
                                {
                                    var match = Regex.Match(condCov, @"\((\d+)/(\d+)\)");
                                    if (match.Success)
                                    {
                                        coveredBranches = int.Parse(match.Groups[1].Value);
                                        totalBranches = int.Parse(match.Groups[2].Value);
                                    }
                                }
                            }

                            if (lineData.TryGetValue(lineNumber, out var existing))
                            {
                                existing.Hits = Math.Max(existing.Hits, hits);
                                existing.CoveredBranches = Math.Max(existing.CoveredBranches, coveredBranches);
                                existing.TotalBranches = Math.Max(existing.TotalBranches, totalBranches);
                            }
                            else
                            {
                                lineData[lineNumber] = new LineCoverageInfo
                                {
                                    Hits = hits,
                                    IsBranch = isBranch,
                                    CoveredBranches = coveredBranches,
                                    TotalBranches = totalBranches
                                };
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    WARNING: Failed to parse {coberturaFile}: {ex.Message}");
                }
            }

            int totalLines = lineData.Count;
            int coveredLines = lineData.Values.Count(l => l.Hits > 0);
            int totalBranchesSum = lineData.Values.Sum(l => l.TotalBranches);
            int coveredBranchesSum = lineData.Values.Sum(l => l.CoveredBranches);

            return new CoverageResult
            {
                Tool = "coverlet",
                Scope = "method",
                LineCovered = coveredLines,
                LineTotal = totalLines,
                LinePercent = totalLines > 0
                    ? Math.Round(100.0 * coveredLines / totalLines, 2)
                    : null,
                BranchCovered = coveredBranchesSum,
                BranchTotal = totalBranchesSum,
                BranchPercent = totalBranchesSum > 0
                    ? Math.Round(100.0 * coveredBranchesSum / totalBranchesSum, 2)
                    : null
            };
        }

        private static bool EndsWithOnBoundary(string haystack, string needle)
        {
            if (!haystack.EndsWith(needle, StringComparison.OrdinalIgnoreCase))
                return false;
            if (haystack.Length == needle.Length)
                return true;
            char preceding = haystack[haystack.Length - needle.Length - 1];
            return preceding == '/' || preceding == '\\';
        }

        private class LineCoverageInfo
        {
            public int Hits { get; set; }
            public bool IsBranch { get; set; }
            public int CoveredBranches { get; set; }
            public int TotalBranches { get; set; }
        }
    }
}
