using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    public class ProjectInfo
    {
        public string Name { get; set; }
        public string RepoUrl { get; set; }
        public string CommitHash { get; set; }
        public int Stars { get; set; }
        public string License { get; set; }
    }
}
