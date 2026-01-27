using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThesisExperiment.Commands
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
            return Path.Combine(runsDir, $"{projectName}__{sanitized}__{variant}.json");
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
