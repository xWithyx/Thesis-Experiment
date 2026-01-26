using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class OutcomeInfo
    {
        public bool Gate1BuildPassed { get; set; }
        public bool Gate2TestPassed { get; set; }
        public string FinalStatus { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
    }
}
