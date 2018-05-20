// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Experiment;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.LanguageServices.ProjectSystem;

namespace Microsoft.CodeAnalysis.Razor
{
    // Deliberately not exported for now, until this feature is working end to end.
    [Export(typeof(ProjectSnapshotChangeTrigger))]
    internal class BackgroundDocumentGenerator : ProjectSnapshotChangeTrigger
    {
        private readonly ForegroundDispatcher _foregroundDispatcher;
        private readonly IWorkspaceProjectContextFactory _workspaceProjectContextFactory;
        private ProjectSnapshotManagerBase _projectManager;

        private readonly Dictionary<DocumentKey, DocumentSnapshot> _work;
        private readonly Dictionary<string, ProjectEntry> _projects;
        private Timer _timer;

        [ImportingConstructor]
        public BackgroundDocumentGenerator(
            ForegroundDispatcher foregroundDispatcher,
            IWorkspaceProjectContextFactory workspaceProjectContextFactory)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            if (workspaceProjectContextFactory == null)
            {
                throw new ArgumentNullException(nameof(workspaceProjectContextFactory));
            }
            
            _foregroundDispatcher = foregroundDispatcher;
            _workspaceProjectContextFactory = workspaceProjectContextFactory;
            _work = new Dictionary<DocumentKey, DocumentSnapshot>();
            _projects = new Dictionary<string, ProjectEntry>(StringComparer.Ordinal);
        }

        public bool HasPendingNotifications
        {
            get
            {
                lock (_work)
                {
                    return _work.Count > 0;
                }
            }
        }

        // Used in unit tests to control the timer delay.
        public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

        public bool IsScheduledOrRunning => _timer != null;

        // Used in unit tests to ensure we can control when background work starts.
        public ManualResetEventSlim BlockBackgroundWorkStart { get; set; }

        // Used in unit tests to ensure we can know when background work finishes.
        public ManualResetEventSlim NotifyBackgroundWorkStarting { get; set; }

        // Used in unit tests to ensure we can control when background work completes.
        public ManualResetEventSlim BlockBackgroundWorkCompleting { get; set; }

        // Used in unit tests to ensure we can know when background work finishes.
        public ManualResetEventSlim NotifyBackgroundWorkCompleted { get; set; }

        private void OnStartingBackgroundWork()
        {
            if (BlockBackgroundWorkStart != null)
            {
                BlockBackgroundWorkStart.Wait();
                BlockBackgroundWorkStart.Reset();
            }

            if (NotifyBackgroundWorkStarting != null)
            {
                NotifyBackgroundWorkStarting.Set();
            }
        }

        private void OnCompletingBackgroundWork()
        {
            if (BlockBackgroundWorkCompleting != null)
            {
                BlockBackgroundWorkCompleting.Wait();
                BlockBackgroundWorkCompleting.Reset();
            }
        }

        private void OnCompletedBackgroundWork()
        {
            if (NotifyBackgroundWorkCompleted != null)
            {
                NotifyBackgroundWorkCompleted.Set();
            }
        }

        public override void Initialize(ProjectSnapshotManagerBase projectManager)
        {
            if (projectManager == null)
            {
                throw new ArgumentNullException(nameof(projectManager));
            }

            _projectManager = projectManager;
            _projectManager.Changed += ProjectManager_Changed;
        }

        protected virtual Task ProcessDocument(DocumentSnapshot document)
        {
            return document.GetGeneratedOutputAsync();
        }

        public void Enqueue(ProjectSnapshot project, DocumentSnapshot document)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _foregroundDispatcher.AssertForegroundThread();

            lock (_work)
            {
                // We only want to store the last 'seen' version of any given document. That way when we pick one to process
                // it's always the best version to use.
                _work[new DocumentKey(project.FilePath, document.FilePath)] = document;

                StartWorker();
            }
        }

        protected virtual void StartWorker()
        {
            // Access to the timer is protected by the lock in Enqueue and in Timer_Tick
            if (_timer == null)
            {
                // Timer will fire after a fixed delay, but only once.
                _timer = new Timer(Timer_Tick, null, Delay, Timeout.InfiniteTimeSpan);
            }
        }

        private async void Timer_Tick(object state) // Yeah I know.
        {
            try
            {
                _foregroundDispatcher.AssertBackgroundThread();

                // Timer is stopped.
                _timer.Change(Timeout.Infinite, Timeout.Infinite);

                OnStartingBackgroundWork();

                KeyValuePair<DocumentKey, DocumentSnapshot>[] work;
                lock (_work)
                {
                    work = _work.ToArray();
                    _work.Clear();
                }

                for (var i = 0; i < work.Length; i++)
                {
                    var document = work[i].Value;
                    try
                    {
                        await ProcessDocument(document);
                    }
                    catch (Exception ex)
                    {
                        ReportError(document, ex);
                    }
                }

                OnCompletingBackgroundWork();

                await Task.Factory.StartNew(
                    () => ReportUpdates(work),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundDispatcher.ForegroundScheduler);

                lock (_work)
                {
                    // Resetting the timer allows another batch of work to start.
                    _timer.Dispose();
                    _timer = null;

                    // If more work came in while we were running start the worker again.
                    if (_work.Count > 0)
                    {
                        StartWorker();
                    }
                }

                OnCompletedBackgroundWork();
            }
            catch (Exception ex)
            {
                // This is something totally unexpected, let's just send it over to the workspace.
                await Task.Factory.StartNew(
                    () => _projectManager.ReportError(ex),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundDispatcher.ForegroundScheduler);
            }
        }

        private void ReportUpdates(KeyValuePair<DocumentKey, DocumentSnapshot>[] work)
        {
            for (var i = 0; i < work.Length; i++)
            {
                var key = work[i].Key;
                var document = work[i].Value;

                if (document.TryGetGeneratedOutput(out var output) &&
                    _projects.TryGetValue(key.ProjectFilePath, out var projectEntry) &&
                    projectEntry.Documents.TryGetValue(key.DocumentFilePath, out var documentEntry))
                {
                    documentEntry.TextContainer.SetText(SourceText.From(output.GetCSharpDocument().GeneratedCode));
                }
            }
        }

        private void ReportError(DocumentSnapshot document, Exception ex)
        {
            GC.KeepAlive(Task.Factory.StartNew(
                () => _projectManager.ReportError(ex),
                CancellationToken.None,
                TaskCreationOptions.None,
                _foregroundDispatcher.ForegroundScheduler));
        }

        private void ProjectManager_Changed(object sender, ProjectChangeEventArgs e)
        {
            switch (e.Kind)
            {
                case ProjectChangeKind.ProjectAdded:
                    {
                        var projectEntry = new ProjectEntry(e.ProjectFilePath);
                        _projects.Add(e.ProjectFilePath, projectEntry);

                        var projectSnapshot = _projectManager.GetLoadedProject(e.ProjectFilePath);
                        if (projectEntry.Context == null && projectSnapshot.IsInitialized)
                        {
                            InitializeProject(projectEntry, projectSnapshot);
                        }

                        foreach (var documentFilePath in projectSnapshot.DocumentFilePaths)
                        {
                            Enqueue(projectSnapshot, projectSnapshot.GetDocument(documentFilePath));
                        }

                        break;
                    }
                case ProjectChangeKind.ProjectChanged:
                    {
                        if (_projects.TryGetValue(e.ProjectFilePath, out var projectEntry))
                        {
                            var projectSnapshot = _projectManager.GetLoadedProject(e.ProjectFilePath);
                            if (projectEntry.Context == null && projectSnapshot.IsInitialized)
                            {
                                InitializeProject(projectEntry, projectSnapshot);
                            }

                            foreach (var documentFilePath in projectSnapshot.DocumentFilePaths)
                            {
                                Enqueue(projectSnapshot, projectSnapshot.GetDocument(documentFilePath));
                            }
                        }

                        break;
                    }

                case ProjectChangeKind.ProjectRemoved:
                    {
                        if (_projects.TryGetValue(e.ProjectFilePath, out var projectEntry))
                        {
                            if (projectEntry.Context != null)
                            {
                                projectEntry.Context.Dispose();
                            }

                            _projects.Remove(e.ProjectFilePath);
                        }

                        break;
                    }

                case ProjectChangeKind.DocumentAdded:
                    {
                        if (_projects.TryGetValue(e.ProjectFilePath, out var projectEntry))
                        {
                            var documentEntry = new DocumentEntry(e.DocumentFilePath);
                            projectEntry.Documents.Add(e.DocumentFilePath, documentEntry);

                            if (projectEntry.Context != null)
                            {
                                InitializeDocument(projectEntry, documentEntry);
                            }

                            var project = _projectManager.GetLoadedProject(e.ProjectFilePath);
                            Enqueue(project, project.GetDocument(e.DocumentFilePath));
                        }
                        
                        break;
                    }

                case ProjectChangeKind.DocumentChanged:
                    {
                        var project = _projectManager.GetLoadedProject(e.ProjectFilePath);
                        Enqueue(project, project.GetDocument(e.DocumentFilePath));

                        break;
                    }

                
                case ProjectChangeKind.DocumentRemoved:
                    {
                        if (_projects.TryGetValue(e.ProjectFilePath, out var projectEntry) &&
                            projectEntry.Documents.TryGetValue(e.DocumentFilePath, out var documentEntry))
                        {
                            if (projectEntry.Context != null)
                            {
                                projectEntry.Context.RemoveSourceFile(documentEntry.FilePath);
                            }

                            projectEntry.Documents.Remove(e.DocumentFilePath);
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Unknown ProjectChangeKind {e.Kind}");
            }
        }

        private void InitializeProject(ProjectEntry projectEntry, ProjectSnapshot projectSnapshot)
        {
            Debug.Assert(projectEntry.Context == null);

            var hierarchy = ((VisualStudioWorkspace)_projectManager.Workspace).GetHierarchy(projectSnapshot.WorkspaceProject.Id);
            projectEntry.Context = _workspaceProjectContextFactory.CreateProjectContext(
                LanguageNames.CSharp,
                projectSnapshot.WorkspaceProject.Name + " (Razor)",
                projectSnapshot.FilePath,
                Guid.NewGuid(),
                hierarchy: hierarchy,
                binOutputPath: null);

            foreach (var document in projectEntry.Documents)
            {
                InitializeDocument(projectEntry, document.Value);
            }
        }

        private void InitializeDocument(ProjectEntry projectEntry, DocumentEntry documentEntry)
        {
            Debug.Assert(projectEntry.Context != null);

            projectEntry.Context.AddSourceFile(
                documentEntry.FilePath,
                container: documentEntry.TextContainer,
                documentServiceFactory: documentEntry);
        }

        private class ProjectEntry
        {
            public ProjectEntry(string filePath)
            {
                FilePath = filePath;
                Documents = new Dictionary<string, DocumentEntry>(StringComparer.Ordinal);
            }

            public string FilePath { get; }

            public Dictionary<string, DocumentEntry> Documents { get; }

            public IWorkspaceProjectContext Context { get; set; }
        }

        private class DocumentEntry : IDocumentServiceFactory, ISpanMapper
        {
            public DocumentEntry(string filePath)
            {
                FilePath = filePath;
                TextContainer = new GeneratedCodeTextContainer();
            }

            public string FilePath { get; }

            public GeneratedCodeTextContainer TextContainer { get; }

            public TService GetService<TService>()
            {
                if (this is TService service)
                {
                    return service;
                }

                return default;
            }

            public Task<ImmutableArray<SpanMapResult>> MapSpansAsync(
                Document document, 
                IEnumerable<TextSpan> spans, 
                CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }

        private class GeneratedCodeTextContainer : SourceTextContainer
        {
            public override event EventHandler<TextChangeEventArgs> TextChanged;

            private SourceText _currentText;
            
            public GeneratedCodeTextContainer()
                : this(SourceText.From(string.Empty))
            {
            }

            public GeneratedCodeTextContainer(SourceText sourceText)
            {
                if (sourceText == null)
                {
                    throw new ArgumentNullException(nameof(sourceText));
                }

                _currentText = sourceText;
            }

            public override SourceText CurrentText => _currentText;

            public void SetText(SourceText sourceText)
            {
                if (sourceText == null)
                {
                    throw new ArgumentNullException(nameof(sourceText));
                }
                
                var e = new TextChangeEventArgs(_currentText, sourceText);
                _currentText = sourceText;

                TextChanged?.Invoke(this, e);
            }
        }
    }
}