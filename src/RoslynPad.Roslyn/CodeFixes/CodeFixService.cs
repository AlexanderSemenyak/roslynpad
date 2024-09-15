using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace RoslynPad.Roslyn.CodeFixes;

[Export(typeof(ICodeFixService)), Shared]
[method: ImportingConstructor]
internal sealed class CodeFixService(Microsoft.CodeAnalysis.CodeFixes.ICodeFixService inner) : ICodeFixService
{
    public IAsyncEnumerable<CodeFixCollection> StreamFixesAsync(Document document, TextSpan textSpan, CancellationToken cancellationToken)
    {
        var result = inner.StreamFixesAsync(document, textSpan, cancellationToken);
        return result.Select(x => new CodeFixCollection(x));
    }

    public CodeFixProvider? GetSuppressionFixer(string language, IEnumerable<string> diagnosticIds)
    {
        return inner.GetSuppressionFixer(language, diagnosticIds);
    }
}
