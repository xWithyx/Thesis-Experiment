using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    public class MutationResult
    {
        public string Tool { get; set; }           
        public string Scope { get; set; }           
        public string ScopedTo { get; set; }        
        public int MutantsKilled { get; set; }
        public int MutantsSurvived { get; set; }
        public int MutantsTotal { get; set; }
        public double? MutationScore { get; set; }
    }
}
