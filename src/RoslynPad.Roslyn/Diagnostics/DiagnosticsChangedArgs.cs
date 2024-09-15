﻿using Microsoft.CodeAnalysis;

namespace RoslynPad.Roslyn.Diagnostics;

public record DiagnosticsChangedArgs(DocumentId DocumentId, IReadOnlySet<DiagnosticData> AddedDiagnostics, IReadOnlySet<DiagnosticData> RemovedDiagnostics);
