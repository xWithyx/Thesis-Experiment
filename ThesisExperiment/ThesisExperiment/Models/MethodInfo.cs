using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    public class MethodInfo
    {
        public string Identifier { get; set; }       
        public string FilePath { get; set; }         
        public int LineStart { get; set; }          
        public int LineEnd { get; set; }           
        public string Signature { get; set; }       
        public string BodyHash { get; set; }         
        public string ContainingClass { get; set; }  
        public string ContainingFile { get; set; }   
    }
}
