﻿using Microsoft.CodeAnalysis;

namespace RoslynPad.Editor;

public class CreatingDocumentEventArgs : RoutedEventArgs
{
    public CreatingDocumentEventArgs(AvalonEditTextContainer textContainer)
    {
        TextContainer = textContainer;
        RoutedEvent = RoslynCodeEditor.CreatingDocumentEvent;
    }

    public AvalonEditTextContainer TextContainer { get; }

    public DocumentId? DocumentId { get; set; }
}
