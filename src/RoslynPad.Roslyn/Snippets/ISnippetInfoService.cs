﻿namespace RoslynPad.Roslyn.Snippets;

public interface ISnippetInfoService
{
    IEnumerable<SnippetInfo> GetSnippets();
}
