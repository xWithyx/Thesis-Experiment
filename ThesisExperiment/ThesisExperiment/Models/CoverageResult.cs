using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Method-level line and branch coverage metrics.</summary>
    public class CoverageResult
    {
        public string Tool { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public int LineCovered { get; set; }        
        public int LineTotal { get; set; }       
        public double? LinePercent { get; set; }
        public int BranchCovered { get; set; }
        public int BranchTotal { get; set; }
        public double? BranchPercent { get; set; }
        public string? Note { get; set; }
    }
}
