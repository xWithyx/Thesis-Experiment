using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ThesisExperiment.Commands
{
    public class CoverletService
    {
        /// <summary>
        /// Find all coverage.cobertura.xml files in the results directory.
        /// dotnet test creates them in GUID-named subdirectories.
        /// </summary>
        public List<string> FindCoberturaFiles(string coverageResultsDir)
        {
            if (!Directory.Exists(coverageResultsDir))
                return new List<string>();

            return Directory.GetFiles(coverageResultsDir, "coverage.cobertura.xml",
                SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// Parse Cobertura XML and extract per-method coverage for a specific
        /// source file and line range [lineStart, lineEnd].
        /// Merges coverage from all provided Cobertura files.
        /// </summary>
        public CoverageResult ParseMethodCoverage(
            List<string> coberturaFiles,
            string methodFilePath,
            int lineStart,
            int lineEnd,
            string repoRootPath)
        {
            // Normalize the method file path for matching (repo-relative, forward slashes)
            var normalizedMethodPath = methodFilePath.Replace('\\', '/');

            // Build absolute candidate for comparison
            var absoluteCandidate = Path.Combine(repoRootPath, methodFilePath)
                .Replace('\\', '/');

            // Collect all line data across all Cobertura files
            // Key: line number, Value: (maxHits, isBranch, coveredBranches, totalBranches)
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

                        // Multi-strategy match (all normalized to forward slashes):
                        // 1. Cobertura path ends with repo-relative method path on a / boundary
                        // 2. Repo-relative method path ends with Cobertura path on a / boundary
                        // 3. Absolute path exact match
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
                                    // Parse "50% (1/2)" format
                                    var match = Regex.Match(condCov, @"\((\d+)/(\d+)\)");
                                    if (match.Success)
                                    {
                                        coveredBranches = int.Parse(match.Groups[1].Value);
                                        totalBranches = int.Parse(match.Groups[2].Value);
                                    }
                                }
                            }

                            // Merge: take max hits, union branch info
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

            // Compute coverage metrics
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

        /// <summary>
        /// Check if 'haystack' ends with 'needle' and the match starts at a path separator
        /// (or needle == haystack). Prevents "XBar.cs" matching "Bar.cs".
        /// </summary>
        private static bool EndsWithOnBoundary(string haystack, string needle)
        {
            if (!haystack.EndsWith(needle, StringComparison.OrdinalIgnoreCase))
                return false;
            if (haystack.Length == needle.Length)
                return true;
            // The character just before the match must be a path separator
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
