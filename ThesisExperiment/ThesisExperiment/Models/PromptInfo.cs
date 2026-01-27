using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Prompt content sent to the LLM.</summary>
    public class PromptInfo
    {
        public string SystemMessage { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string ErrorFeedback { get; set; } = string.Empty;
        public string PreviousTest { get; set; } = string.Empty;
        public string TemplateFile { get; set; } = string.Empty;
        public string TemplateVersion { get; set; } = string.Empty;
    }
}
