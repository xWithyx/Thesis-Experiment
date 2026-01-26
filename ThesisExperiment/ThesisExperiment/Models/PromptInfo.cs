using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class PromptInfo
    {
        public string SystemMessage { get; set; }
        public string UserMessage { get; set; }
        public string ErrorFeedback { get; set; }     
        public string PreviousTest { get; set; }       
        public string TemplateFile { get; set; }      
        public string TemplateVersion { get; set; }
    }
}
