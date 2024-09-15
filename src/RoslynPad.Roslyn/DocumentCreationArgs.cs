using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynPad.Roslyn;

public record DocumentCreationArgs(SourceTextContainer SourceTextContainer,
                                   string WorkingDirectory,
                                   SourceCodeKind SourceCodeKind,
                                   Action<SourceText>? OnTextUpdated = null,
                                   string? Name = null);
