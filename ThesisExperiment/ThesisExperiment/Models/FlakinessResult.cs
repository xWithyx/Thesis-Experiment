using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class FlakinessResult
    {
        public int RunsExecuted { get; set; }
        public List<string> RunResults { get; set; } = new List<string>();
        public string Classification { get; set; } = string.Empty;
    }
}
