using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using NuGet.Versioning;
using RoslynPad.Hosting;
using RoslynPad.Roslyn.Diagnostics;
using RoslynPad.Roslyn.Rename;
using RoslynPad.Runtime;
using RoslynPad.Utilities;

namespace RoslynPad.UI
{
    [Export]
    public class OpenDocumentViewModel : NotificationObject
    {
        private const string DefaultILText = "// Run to view IL";

        private readonly IServiceProvider _serviceProvider;
        private readonly IAppDispatcher _dispatcher;

        private string _workingDirectory;
        private ExecutionHost _executionHost;
        private ObservableCollection<ResultObject> _results;
        private CancellationTokenSource _cts;
        private bool _isRunning;
        private Action<object> _executionHostOnDumped;
        private bool _isDirty;
        private Platform _platform;
        private bool _isSaving;
        private IDisposable _viewDisposable;
        private Action<ExceptionResultObject> _onError;
        private Func<TextSpan> _getSelection;
        private string _ilText;

        public IEnumerable<object> Results => _results;

        private ObservableCollection<ResultObject> ResultsInternal
        {
            // ReSharper disable once UnusedMember.Local
            get => _results; set
            {
                _results = value;
                OnPropertyChanged(nameof(Results));
            }
        }

        public DocumentViewModel Document { get; private set; }

        public string ILText
        {
            get => _ilText;
            private set => SetProperty(ref _ilText, value);
        }

        [ImportingConstructor]
        public OpenDocumentViewModel(IServiceProvider serviceProvider, MainViewModel mainViewModel, ICommandProvider commands, IAppDispatcher appDispatcher)
        {
            _serviceProvider = serviceProvider;
            MainViewModel = mainViewModel;
            CommandProvider = commands;
            NuGet = serviceProvider.GetService<NuGetDocumentViewModel>();
            _dispatcher = appDispatcher;

            var roslynHost = mainViewModel.RoslynHost;

            Platform = Platform.X86;
            _executionHost = new ExecutionHost(GetHostExeName(), new InitializationParameters(
                roslynHost.DefaultReferences.OfType<PortableExecutableReference>().Select(x => x.FilePath).ToImmutableArray(),
                roslynHost.DefaultImports, mainViewModel.NuGetConfiguration, _workingDirectory));

            _executionHost.Error += ExecutionHostOnError;
            _executionHost.Disassembled += ExecutionHostOnDisassembled;

            SaveCommand = commands.CreateAsync(() => Save(promptSave: false));
            RunCommand = commands.CreateAsync(Run, () => !IsRunning);
            CompileAndSaveCommand = commands.CreateAsync(CompileAndSave);
            RestartHostCommand = commands.CreateAsync(RestartHost);
            FormatDocumentCommand = commands.CreateAsync(FormatDocument);
            CommentSelectionCommand = commands.CreateAsync(() => CommentUncommentSelection(CommentAction.Comment));
            UncommentSelectionCommand = commands.CreateAsync(() => CommentUncommentSelection(CommentAction.Uncomment));
            RenameSymbolCommand = commands.CreateAsync(RenameSymbol);

            ILText = DefaultILText;
        }

        private void ExecutionHostOnError(ExceptionResultObject errorResult)
        {
            _dispatcher.InvokeAsync(() => _onError?.Invoke(errorResult));
            ResultsInternal?.Add(errorResult);
        }

        private void ExecutionHostOnDisassembled(string il)
        {
            ILText = il;
        }

        public void SetDocument(DocumentViewModel document)
        {
            Document = document;

            IsDirty = document?.IsAutoSave == true;

            _workingDirectory = Document != null
                ? Path.GetDirectoryName(Document.Path)
                : MainViewModel.DocumentRoot.Path;
        }

        private async Task RenameSymbol()
        {
            var host = MainViewModel.RoslynHost;
            var document = host.GetDocument(DocumentId);
            var symbol = await RenameHelper.GetRenameSymbol(document, _getSelection().Start).ConfigureAwait(true);
            if (symbol == null) return;

            var dialog = _serviceProvider.GetService<IRenameSymbolDialog>();
            dialog.Initialize(symbol.Name);
            dialog.Show();
            if (dialog.ShouldRename)
            {
                var newSolution = await Renamer.RenameSymbolAsync(document.Project.Solution, symbol, dialog.SymbolName, null).ConfigureAwait(true);
                var newDocument = newSolution.GetDocument(DocumentId);
                // TODO: possibly update entire solution
                host.UpdateDocument(newDocument);
            }
            OnEditorFocus();
        }

        private enum CommentAction
        {
            Comment,
            Uncomment
        }

        private async Task CommentUncommentSelection(CommentAction action)
        {
            const string singleLineCommentString = "//";
            var document = MainViewModel.RoslynHost.GetDocument(DocumentId);
            var selection = _getSelection();
            var documentText = await document.GetTextAsync().ConfigureAwait(false);
            var changes = new List<TextChange>();
            var lines = documentText.Lines.SkipWhile(x => !x.Span.IntersectsWith(selection))
                .TakeWhile(x => x.Span.IntersectsWith(selection)).ToArray();

            if (action == CommentAction.Comment)
            {
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(documentText.GetSubText(line.Span).ToString()))
                    {
                        changes.Add(new TextChange(new TextSpan(line.Start, 0), singleLineCommentString));
                    }
                }
            }
            else if (action == CommentAction.Uncomment)
            {
                foreach (var line in lines)
                {
                    var text = documentText.GetSubText(line.Span).ToString();
                    if (text.TrimStart().StartsWith(singleLineCommentString, StringComparison.Ordinal))
                    {
                        changes.Add(new TextChange(new TextSpan(
                            line.Start + text.IndexOf(singleLineCommentString, StringComparison.Ordinal),
                            singleLineCommentString.Length), string.Empty));
                    }
                }
            }

            if (changes.Count == 0) return;

            MainViewModel.RoslynHost.UpdateDocument(document.WithText(documentText.WithChanges(changes)));
            if (action == CommentAction.Uncomment)
            {
                await FormatDocument().ConfigureAwait(false);
            }
        }

        private async Task FormatDocument()
        {
            var document = MainViewModel.RoslynHost.GetDocument(DocumentId);
            var formattedDocument = await Formatter.FormatAsync(document).ConfigureAwait(false);
            MainViewModel.RoslynHost.UpdateDocument(formattedDocument);
        }

        private string GetHostExeName()
        {
            switch (Platform)
            {
                case Platform.X86:
                    return "RoslynPad.Host32.exe";
                case Platform.X64:
                    return "RoslynPad.Host64.exe";
                default:
                    throw new ArgumentOutOfRangeException(nameof(Platform));
            }
        }

        public Platform Platform
        {
            get => _platform; set
            {
                if (SetProperty(ref _platform, value))
                {
                    if (_executionHost != null)
                    {
                        _executionHost.HostPath = GetHostExeName();
                        RestartHostCommand.Execute();
                    }
                }
            }
        }

        private async Task RestartHost()
        {
            Reset();
            await _executionHost.ResetAsync().ConfigureAwait(false);
            SetIsRunning(false);
        }

        private void SetIsRunning(bool value)
        {
            _dispatcher.InvokeAsync(() => IsRunning = value);
        }

        public async Task AutoSave()
        {
            if (!IsDirty) return;
            if (Document == null)
            {
                var index = 1;
                string path;
                do
                {
                    path = Path.Combine(_workingDirectory, DocumentViewModel.GetAutoSaveName("Program" + index++));
                } while (File.Exists(path));
                Document = DocumentViewModel.CreateAutoSave(path);
            }

            await SaveDocument(Document.GetAutoSavePath()).ConfigureAwait(false);
        }

        public async Task<SaveResult> Save(bool promptSave)
        {
            if (_isSaving) return SaveResult.Cancel;
            if (!IsDirty && promptSave) return SaveResult.Save;

            _isSaving = true;
            try
            {
                var result = SaveResult.Save;
                if (Document == null || Document.IsAutoSaveOnly)
                {
                    var dialog = _serviceProvider.GetService<ISaveDocumentDialog>();
                    dialog.ShowDontSave = promptSave;
                    dialog.AllowNameEdit = true;
                    dialog.FilePathFactory = s => DocumentViewModel.GetDocumentPathFromName(_workingDirectory, s);
                    dialog.Show();
                    result = dialog.Result;
                    if (result == SaveResult.Save)
                    {
                        Document?.DeleteAutoSave();
                        Document = MainViewModel.AddDocument(dialog.DocumentName);
                        OnPropertyChanged(nameof(Title));
                    }
                }
                else if (promptSave)
                {
                    var dialog = _serviceProvider.GetService<ISaveDocumentDialog>();
                    dialog.ShowDontSave = true;
                    dialog.DocumentName = Document.Name;
                    dialog.Show();
                    result = dialog.Result;
                }

                if (result == SaveResult.Save)
                {
                    // ReSharper disable once PossibleNullReferenceException
                    await SaveDocument(Document.GetSavePath()).ConfigureAwait(true);
                    IsDirty = false;
                }

                if (result != SaveResult.Cancel)
                {
                    Document?.DeleteAutoSave();
                }

                return result;
            }
            finally
            {
                _isSaving = false;
            }
        }

        private async Task SaveDocument(string path)
        {
            if (DocumentId == null) return;

            var text = await MainViewModel.RoslynHost.GetDocument(DocumentId).GetTextAsync().ConfigureAwait(false);
            using (var writer = File.CreateText(path))
            {
                for (int lineIndex = 0; lineIndex < text.Lines.Count - 1; ++lineIndex)
                {
                    var lineText = text.Lines[lineIndex].ToString();
                    await writer.WriteLineAsync(lineText).ConfigureAwait(false);
                }
                await writer.WriteAsync(text.Lines[text.Lines.Count - 1].ToString()).ConfigureAwait(false);
            }
        }

        internal async Task Initialize(SourceTextContainer sourceTextContainer,
            Action<DiagnosticsUpdatedArgs> onDiagnosticsUpdated, Action<SourceText> onTextUpdated,
            Action<ExceptionResultObject> onError,
            Func<TextSpan> getSelection, IDisposable viewDisposable)
        {
            _viewDisposable = viewDisposable;
            _onError = onError;
            _getSelection = getSelection;
            var roslynHost = MainViewModel.RoslynHost;
            // ReSharper disable once AssignNullToNotNullAttribute
            DocumentId = roslynHost.AddDocument(sourceTextContainer, _workingDirectory, onDiagnosticsUpdated,
                onTextUpdated);
            await _executionHost.ResetAsync().ConfigureAwait(false);
        }

        public DocumentId DocumentId { get; private set; }

        public MainViewModel MainViewModel { get; }
        public ICommandProvider CommandProvider { get; }

        public NuGetDocumentViewModel NuGet { get; }

        public string Title => Document != null && !Document.IsAutoSaveOnly ? Document.Name : "New";

        public IDelegateCommand SaveCommand { get; }

        public IDelegateCommand RunCommand { get; }

        public IDelegateCommand CompileAndSaveCommand { get; }

        public IDelegateCommand RestartHostCommand { get; }

        public IDelegateCommand FormatDocumentCommand { get; }

        public IDelegateCommand CommentSelectionCommand { get; }

        public IDelegateCommand UncommentSelectionCommand { get; }

        public IDelegateCommand RenameSymbolCommand { get; }

        public bool IsRunning
        {
            get => _isRunning; private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    _dispatcher.InvokeAsync(() => RunCommand.RaiseCanExecuteChanged());
                }
            }
        }

        private async Task CompileAndSave()
        {
            var saveDialog = _serviceProvider.GetService<ISaveFileDialog>();
            saveDialog.OverwritePrompt = true;
            saveDialog.AddExtension = true;
            saveDialog.Filter = "Libraries|*.dll|Executables|*.exe";
            saveDialog.DefaultExt = "dll";
            if (saveDialog.Show() != true) return;

            var code = await GetCode(CancellationToken.None).ConfigureAwait(true);

            var results = new ObservableCollection<ResultObject>();
            ResultsInternal = results;

            HookDumped(results, CancellationToken.None);

            try
            {
                await Task.Run(() => _executionHost.CompileAndSave(code, saveDialog.FileName)).ConfigureAwait(true);
            }
            catch (CompilationErrorException ex)
            {
                foreach (var diagnostic in ex.Diagnostics)
                {
                    results.Add(ResultObject.Create(diagnostic));
                }
            }
            catch (Exception ex)
            {
                AddResult(ex, results, CancellationToken.None);
            }
        }

        private async Task Run()
        {
            if (IsRunning) return;

            try
            {
                await EnsureNuGetPackages().ConfigureAwait(true);
            }
            catch (Exception)
            {
                IsRunning = false;
                throw;
            }

            Reset();

            await MainViewModel.AutoSaveOpenDocuments().ConfigureAwait(true);

            SetIsRunning(true);

            var results = new ObservableCollection<ResultObject>();
            ResultsInternal = results;

            if (!ShowIL)
            {
                ILText = DefaultILText;
            }

            var cancellationToken = _cts.Token;
            HookDumped(results, cancellationToken);
            try
            {
                var code = await GetCode(cancellationToken).ConfigureAwait(true);
                await _executionHost.ExecuteAsync(code, ShowIL).ConfigureAwait(true);
            }
            catch (CompilationErrorException ex)
            {
                foreach (var diagnostic in ex.Diagnostics)
                {
                    results.Add(ResultObject.Create(diagnostic));
                }
            }
            catch (Exception ex)
            {
                AddResult(ex, results, cancellationToken);
            }
            finally
            {
                SetIsRunning(false);
            }
        }

        private async Task EnsureNuGetPackages()
        {
            var nugetVariable = MainViewModel.NuGetConfiguration.PathVariableName;
            var pathToRepository = MainViewModel.NuGetConfiguration.PathToRepository;
            foreach (var directive in MainViewModel.RoslynHost.GetReferencesDirectives(DocumentId))
            {
                if (directive.StartsWith(nugetVariable, StringComparison.OrdinalIgnoreCase))
                {
                    var directiveWithoutRoot = directive.Substring(nugetVariable.Length + 1);
                    if (!File.Exists(Path.Combine(pathToRepository, directiveWithoutRoot)))
                    {
                        var sections = directiveWithoutRoot.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        NuGetVersion version;
                        if (sections.Length > 2 && NuGetVersion.TryParse(sections[1], out version))
                        {
                            await NuGet.InstallPackage(sections[0], version, reportInstalled: false).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private void HookDumped(ObservableCollection<ResultObject> results, CancellationToken cancellationToken)
        {
            if (_executionHostOnDumped != null)
            {
                _executionHost.Dumped -= _executionHostOnDumped;
            }
            _executionHostOnDumped = o => AddResult(o, results, cancellationToken);
            _executionHost.Dumped += _executionHostOnDumped;
        }

        private async Task<string> GetCode(CancellationToken cancellationToken)
        {
            return (await MainViewModel.RoslynHost.GetDocument(DocumentId).GetTextAsync(cancellationToken)
                .ConfigureAwait(false)).ToString();
        }

        private void Reset()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = new CancellationTokenSource();
        }

        private void AddResult(object o, ObservableCollection<ResultObject> results, CancellationToken cancellationToken)
        {
            _dispatcher.InvokeAsync(() =>
            {
                if (o is IList<ResultObject> list)
                {
                    foreach (var resultObject in list)
                    {
                        results.Add(resultObject);
                    }
                }
                else
                {
                    results.Add(ResultObject.Create(o));
                }
            }, AppDispatcherPriority.Low, cancellationToken);
        }

        public async Task<string> LoadText()
        {
            if (Document == null)
            {
                return string.Empty;
            }
            using (var fileStream = File.OpenText(Document.Path))
            {
                return await fileStream.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        public void Close()
        {
            _viewDisposable?.Dispose();
            _executionHost?.Dispose();
            _executionHost = null;
        }

        public bool IsDirty
        {
            get => _isDirty; private set => SetProperty(ref _isDirty, value);
        }

        public bool ShowIL { get; set; }

        public event EventHandler EditorFocus;

        private void OnEditorFocus()
        {
            EditorFocus?.Invoke(this, EventArgs.Empty);
        }

        public void SetDirty()
        {
            IsDirty = true;
        }
    }
}