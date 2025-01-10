using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;

namespace RoslynPad.Roslyn.CodeRefactorings;

[Export(typeof(ICodeRefactoringService)), Shared]
[method: ImportingConstructor]
internal sealed class CodeRefactoringService(Microsoft.CodeAnalysis.CodeRefactorings.ICodeRefactoringService inner) : ICodeRefactoringService
{
    public Task<bool> HasRefactoringsAsync(Document document, TextSpan textSpan, CancellationToken cancellationToken)
    {
        return inner.HasRefactoringsAsync(document, textSpan, cancellationToken);
    }

    public async Task<IEnumerable<CodeRefactoring>> GetRefactoringsAsync(Document document, TextSpan textSpan, CancellationToken cancellationToken)
    {
        var result = await inner.GetRefactoringsAsync(document, textSpan, CodeActionRequestPriority.Default, cancellationToken).ConfigureAwait(false);
        return result.Select(x => new CodeRefactoring(x)).ToArray();
    }
}
