using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class ErrorInfo
    {
        public string Category { get; set; } = string.Empty;
        public string Subcategory { get; set; } = string.Empty;
        public string RawMessage { get; set; } = string.Empty;
        public string LabeledBy { get; set; } = string.Empty;
        public DateTime? LabeledAt { get; set; }
    }
}
