using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    /// <summary>Project identification for a RunRecord.</summary>
    public class ProjectInfo
    {
        public string Name { get; set; } = string.Empty;
        public string RepoUrl { get; set; } = string.Empty;
        public string CommitHash { get; set; } = string.Empty;
        public int Stars { get; set; }
        public string License { get; set; } = string.Empty;
    }
}
