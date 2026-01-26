using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class ProjectCandidate
    {
        public string RepoUrl { get; set; } = string.Empty;
        public string CloneUrl { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Stars { get; set; }
        public string License { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public DateTime LastCommit { get; set; }
        public bool HasTests { get; set; }
        public bool Builds { get; set; }
        public int PublicMethodCount { get; set; }
        public int LinesOfCode { get; set; }
        public bool Included { get; set; }
        public string ExclusionReason { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public string CommitHash { get; set; } = string.Empty;
    }
}
