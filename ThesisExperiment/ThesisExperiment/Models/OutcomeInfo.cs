using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Gate results and final status of a run.</summary>
    public class OutcomeInfo
    {
        public bool Gate1BuildPassed { get; set; }
        public bool Gate2TestPassed { get; set; }
        public string FinalStatus { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
        /// <summary>Failure classification from source Variant B (build_failed, test_failed, not_focal_copy, not_focal).</summary>
        public string FailureClass { get; set; } = string.Empty;
        /// <summary>Success level achieved (compiled, green_not_focal, focal_executed).</summary>
        public string SuccessLevel { get; set; } = string.Empty;
    }
}
