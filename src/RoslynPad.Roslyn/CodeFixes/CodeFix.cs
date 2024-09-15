using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace RoslynPad.Roslyn.CodeFixes;

public sealed class CodeFix(Microsoft.CodeAnalysis.CodeFixes.CodeFix inner)
{
    public Project Project => inner.Project;

    public CodeAction Action => inner.Action;

    public ImmutableArray<Diagnostic> Diagnostics => inner.Diagnostics;

    public Diagnostic PrimaryDiagnostic => inner.PrimaryDiagnostic;
}
