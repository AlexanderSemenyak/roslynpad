﻿using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Host;

namespace RoslynPad.Roslyn.Diagnostics;

public interface IDiagnosticsUpdater : IWorkspaceService
{
    ImmutableHashSet<string> DisabledDiagnostics { get; set; }

    event Action<DiagnosticsChangedArgs>? DiagnosticsChanged;
}
