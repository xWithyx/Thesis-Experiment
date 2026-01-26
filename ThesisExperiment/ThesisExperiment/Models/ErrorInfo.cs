using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class ErrorInfo
    {
        public string Category { get; set; }
        public string Subcategory { get; set; }
        public string RawMessage { get; set; }
        public string LabeledBy { get; set; }      
        public DateTime? LabeledAt { get; set; }
    }
}
