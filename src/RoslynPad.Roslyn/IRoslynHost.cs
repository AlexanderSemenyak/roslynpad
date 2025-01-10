using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace RoslynPad.Roslyn;

public interface IRoslynHost
{
    ParseOptions ParseOptions { get; }

    TService GetService<TService>();

    TService GetWorkspaceService<TService>(DocumentId documentId) where TService : IWorkspaceService;

    DocumentId AddDocument(DocumentCreationArgs args);

    Document? GetDocument(DocumentId documentId);

    void CloseDocument(DocumentId documentId);

    MetadataReference CreateMetadataReference(string location);
}
