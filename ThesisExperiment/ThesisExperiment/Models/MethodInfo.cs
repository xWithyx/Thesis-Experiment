using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Focal method identification and location.</summary>
    public class MethodInfo
    {
        public string Identifier { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineStart { get; set; }          
        public int LineEnd { get; set; }           
        public string Signature { get; set; } = string.Empty;
        public string BodyHash { get; set; } = string.Empty;
        public string ContainingClass { get; set; } = string.Empty;
        public string ContainingFile { get; set; } = string.Empty;
    }
}
