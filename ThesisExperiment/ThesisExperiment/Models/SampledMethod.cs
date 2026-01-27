using CsvHelper.Configuration.Attributes;

namespace ThesisExperiment.Models
{
    public class SampledMethod
    {
        [Name("project_name")]
        public string ProjectName { get; set; } = string.Empty;

        [Name("repo_url")]
        public string RepoUrl { get; set; } = string.Empty;

        [Name("commit_hash")]
        public string CommitHash { get; set; } = string.Empty;

        [Name("namespace")]
        public string Namespace { get; set; } = string.Empty;

        [Name("type_name")]
        public string TypeName { get; set; } = string.Empty;

        [Name("method_name")]
        public string MethodName { get; set; } = string.Empty;

        [Name("identifier")]
        public string Identifier { get; set; } = string.Empty;

        [Name("file_path")]
        public string FilePath { get; set; } = string.Empty;

        [Name("line_start")]
        public int LineStart { get; set; }

        [Name("line_end")]
        public int LineEnd { get; set; }

        [Name("return_type")]
        public string ReturnType { get; set; } = string.Empty;

        [Name("parameter_types")]
        public string ParameterTypes { get; set; } = string.Empty;

        [Name("is_static")]
        public bool IsStatic { get; set; }

        [Name("is_async")]
        public bool IsAsync { get; set; }
    }
}
