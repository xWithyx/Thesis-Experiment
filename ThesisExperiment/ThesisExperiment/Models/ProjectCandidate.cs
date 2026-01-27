using CsvHelper.Configuration.Attributes;

namespace ThesisExperiment.Models
{
    public class ProjectCandidate
    {
        [Name("repo_url")]
        public string RepoUrl { get; set; } = string.Empty;

        [Name("stars")]
        public int Stars { get; set; }

        [Name("default_branch")]
        public string DefaultBranch { get; set; } = string.Empty;

        [Name("commit_hash")]
        public string CommitHash { get; set; } = string.Empty;

        [Name("clone_path")]
        public string ClonePath { get; set; } = string.Empty;

        [Name("build_success")]
        public bool BuildSuccess { get; set; }

        [Name("has_tests")]
        public bool HasTests { get; set; }

        [Name("test_projects_count")]
        public int TestProjectsCount { get; set; }

        [Name("test_frameworks")]
        public string TestFrameworks { get; set; } = string.Empty;

        [Name("public_method_count")]
        public int PublicMethodCount { get; set; }

        [Name("loc_cs")]
        public int LocCs { get; set; }

        [Name("passed_filters")]
        public bool PassedFilters { get; set; }

        [Name("fail_reasons")]
        public string FailReasons { get; set; } = string.Empty;

        // Internal fields (not written to CSV)
        [Ignore]
        public string CloneUrl { get; set; } = string.Empty;

        [Ignore]
        public string Name { get; set; } = string.Empty;
    }
}
