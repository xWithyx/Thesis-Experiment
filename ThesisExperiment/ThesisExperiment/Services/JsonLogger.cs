using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using ThesisExperiment.Models;

namespace ThesisExperiment.Services
{
    /// <summary>Serializes RunRecords to JSON files.</summary>
    public class JsonLogger
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Writes a RunRecord as indented JSON.</summary>
        public void WriteRunRecord(RunRecord record, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(record, Options);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>Builds the output file path for a RunRecord.</summary>
        public static string GetOutputPath(string runsDir, string projectName,
            string identifier, string variant)
        {
            var sanitized = SanitizeForFilename(identifier);

            // Windows MAX_PATH = 260, leave margin for runsDir + separators
            // Format: {runsDir}\{project}__{identifier}__{variant}.json
            var baseFilename = $"{projectName}__{sanitized}__{variant}.json";
            var fullPath = Path.Combine(runsDir, baseFilename);

            // If path too long, truncate identifier and add hash suffix
            const int maxPathLength = 250; // Leave some margin
            if (fullPath.Length > maxPathLength)
            {
                var hash = ComputeShortHash(identifier);
                // Calculate max identifier length
                var overhead = runsDir.Length + 1 + projectName.Length + 4 + variant.Length + 5 + 9; // __..__.json + _hash
                var maxIdLength = maxPathLength - overhead;
                if (maxIdLength < 20) maxIdLength = 20; // Minimum readable portion

                var truncated = sanitized.Length > maxIdLength
                    ? sanitized.Substring(0, maxIdLength)
                    : sanitized;
                baseFilename = $"{projectName}__{truncated}_{hash}__{variant}.json";
            }

            return Path.Combine(runsDir, baseFilename);
        }

        /// <summary>Computes a short 8-character hash for uniqueness.</summary>
        private static string ComputeShortHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
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
    }
}
