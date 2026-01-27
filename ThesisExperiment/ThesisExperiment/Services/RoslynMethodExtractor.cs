using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace ThesisExperiment.Commands
{
    /// <summary>Extracted method source code and context.</summary>
    public class ExtractionResult
    {
        public string MethodBody { get; set; } = string.Empty;
        public string MethodSignature { get; set; } = string.Empty;
        public string ClassContext { get; set; } = string.Empty;
        public bool UsedFallback { get; set; }
    }

    /// <summary>Extracts method body and class context using Roslyn.</summary>
    public class RoslynMethodExtractor
    {
        private const int MaxClassContextLength = 4000;

        /// <summary>Locates a method by name and line range, extracts body and context.</summary>
        public ExtractionResult ExtractMethod(
            string absoluteFilePath,
            string methodName,
            int lineStart,
            int lineEnd,
            string typeName)
        {
            try
            {
                var code = File.ReadAllText(absoluteFilePath);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var targetMethod = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m =>
                    {
                        if (m.Identifier.Text != methodName)
                            return false;

                        var span = m.GetLocation().GetLineSpan();
                        int roslynStart = span.StartLinePosition.Line + 1;
                        int roslynEnd = span.EndLinePosition.Line + 1;

                        return roslynStart <= lineEnd && roslynEnd >= lineStart;
                    });

                if (targetMethod == null)
                    return FallbackExtraction(absoluteFilePath, lineStart, lineEnd);

                string methodBody;
                if (targetMethod.Body != null)
                {
                    methodBody = ExtractBlockBodyContent(targetMethod.Body);
                }
                else if (targetMethod.ExpressionBody != null)
                {
                    methodBody = targetMethod.ExpressionBody.ToFullString().Trim();
                }
                else
                {
                    methodBody = string.Empty;
                }

                var signature = BuildMethodSignature(targetMethod);
                var classContext = ExtractClassContext(targetMethod);

                return new ExtractionResult
                {
                    MethodBody = methodBody,
                    MethodSignature = signature,
                    ClassContext = classContext,
                    UsedFallback = false
                };
            }
            catch
            {
                return FallbackExtraction(absoluteFilePath, lineStart, lineEnd);
            }
        }

        private static string ExtractBlockBodyContent(BlockSyntax body)
        {
            var fullText = body.ToFullString();
            var trimmed = fullText.Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                trimmed = trimmed[1..^1];

            var lines = trimmed.Split('\n');
            int minIndent = lines
                .Where(l => l.Trim().Length > 0)
                .Select(l => l.Length - l.TrimStart().Length)
                .DefaultIfEmpty(0)
                .Min();

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0)
                {
                    sb.AppendLine();
                    continue;
                }
                sb.AppendLine(minIndent <= line.Length ? line[minIndent..] : line.TrimStart());
            }

            return sb.ToString().Trim();
        }

        private static string BuildMethodSignature(MethodDeclarationSyntax method)
        {
            var sb = new StringBuilder();

            if (method.Modifiers.Any())
                sb.Append(string.Join(" ", method.Modifiers.Select(m => m.Text)) + " ");

            sb.Append(method.ReturnType.ToString() + " ");
            sb.Append(method.Identifier.Text);

            if (method.TypeParameterList != null)
                sb.Append(method.TypeParameterList.ToString());

            sb.Append(method.ParameterList.ToString());

            return sb.ToString();
        }

        private static string ExtractClassContext(MethodDeclarationSyntax targetMethod)
        {
            var containingType = targetMethod.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault();

            if (containingType == null)
                return string.Empty;

            var sb = new StringBuilder();

            foreach (var member in containingType.Members)
            {
                if (member == targetMethod)
                    continue;

                var summary = GetMemberSummary(member);
                if (!string.IsNullOrEmpty(summary))
                {
                    sb.AppendLine(summary);

                    if (sb.Length > MaxClassContextLength)
                    {
                        sb.AppendLine("// ... (truncated)");
                        break;
                    }
                }
            }

            return sb.ToString().Trim();
        }

        private static string GetMemberSummary(MemberDeclarationSyntax member)
        {
            return member switch
            {
                MethodDeclarationSyntax m =>
                    $"{BuildMethodSignature(m)} {{ ... }}",

                ConstructorDeclarationSyntax c =>
                    $"{string.Join(" ", c.Modifiers.Select(m => m.Text))} {c.Identifier.Text}{c.ParameterList} {{ ... }}",

                PropertyDeclarationSyntax p =>
                    $"{string.Join(" ", p.Modifiers.Select(m => m.Text))} {p.Type} {p.Identifier} {{ get; set; }}",

                FieldDeclarationSyntax f =>
                    f.ToString().TrimEnd().TrimEnd(';') + ";",

                _ => string.Empty
            };
        }

        private static ExtractionResult FallbackExtraction(
            string absoluteFilePath, int lineStart, int lineEnd)
        {
            try
            {
                var allLines = File.ReadAllLines(absoluteFilePath);
                int start = Math.Max(0, lineStart - 1);
                int end = Math.Min(allLines.Length, lineEnd);

                var lines = allLines[start..end];
                return new ExtractionResult
                {
                    MethodBody = string.Join(Environment.NewLine, lines),
                    MethodSignature = lines.Length > 0 ? lines[0].Trim() : string.Empty,
                    ClassContext = string.Empty,
                    UsedFallback = true
                };
            }
            catch
            {
                return new ExtractionResult
                {
                    MethodBody = string.Empty,
                    MethodSignature = string.Empty,
                    ClassContext = string.Empty,
                    UsedFallback = true
                };
            }
        }
    }
}
