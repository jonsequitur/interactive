// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Text;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Parsing;
using Microsoft.DotNet.Interactive.ValueSharing;

namespace Microsoft.DotNet.Interactive
{
    public abstract partial class Kernel : 
        IKernelCommandHandler<RequestKernelInfo>, 
        IDisposable
    {
        private static readonly ConcurrentDictionary<Type, HashSet<Type>> _declaredHandledCommandTypesByKernelType = new();

        private readonly Subject<KernelEvent> _kernelEvents = new();
        private readonly CompositeDisposable _disposables;
        private readonly Dictionary<Type, KernelCommandInvocation> _dynamicHandlers = new();
        private readonly HashSet<Type> _supportedCommandTypes;
        private IKernelScheduler<KernelCommand, KernelCommandResult> _fastPathScheduler;
        private FrontendEnvironment _frontendEnvironment;
        private ChooseKernelDirective _chooseKernelDirective;
        private KernelScheduler<KernelCommand, KernelCommandResult> _commandScheduler;
        private readonly ConcurrentQueue<KernelCommand> _deferredCommands = new();
        private readonly SemaphoreSlim _fastPathSchedulerLock = new(1);
        private KernelInvocationContext _inFlightContext;
        private int _countOfLanguageServiceCommandsInFlight = 0;
        private KernelInfo _kernelInfo;

        protected Kernel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
            }

            RootKernel = this;

            Name = name;

            SubmissionParser = new SubmissionParser(this);

            _disposables = new CompositeDisposable();

            Pipeline = new KernelCommandPipeline(this);

            _supportedCommandTypes = _declaredHandledCommandTypesByKernelType.GetOrAdd(
                GetType(), 
                InitializeSupportedCommandTypes);

            _disposables.Add(Disposable.Create(() => _kernelEvents.OnCompleted()));

            RegisterCommandHandlers();

            HashSet<Type> InitializeSupportedCommandTypes(Type kernelType)
            {
                var types = kernelType.GetInterfaces()
                                      .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IKernelCommandHandler<>))
                                      .SelectMany(i => i.GenericTypeArguments)
                                      .ToArray();

                return new HashSet<Type>(types);
            }
        }

        private void RegisterCommandHandlers()
        {
            if (this is ISupportGetValue)
            {
                RegisterCommandHandler<RequestValueInfos>((command, context) => command.InvokeAsync(context));

                RegisterCommandHandler<RequestValue>((command, context) => command.InvokeAsync(context));
            }
        }
      
        internal KernelCommandPipeline Pipeline { get; }

        public CompositeKernel ParentKernel { get; internal set; }

        public Kernel RootKernel { get; internal set; }

        public SubmissionParser SubmissionParser { get; }

        public void AddMiddleware(
            KernelCommandPipelineMiddleware middleware,
            [CallerMemberName] string caller = null) => Pipeline.AddMiddleware(middleware, caller);

        public void DeferCommand(KernelCommand command)
        {
            if (command is null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            command.SetToken($"deferredCommand::{Guid.NewGuid():N}");

            _deferredCommands.Enqueue(command);
        }

        private bool TryPreprocessCommands(
            KernelCommand originalCommand,
            KernelInvocationContext context,
            out IReadOnlyList<KernelCommand> commands)
        {
            switch (originalCommand)
            {
                case SubmitCode { LanguageNode: null } submitCode:
                    commands = SubmissionParser.SplitSubmission(submitCode);
                    break;
                case RequestDiagnostics { LanguageNode: null } requestDiagnostics:
                    commands = SubmissionParser.SplitSubmission(requestDiagnostics);
                    break;
                case LanguageServiceCommand { LanguageNode: null } languageServiceCommand:
                    if (!TryPreprocessLanguageServiceCommand(languageServiceCommand, context, out commands))
                    {
                        return false;
                    }
                    break;
                default:
                    commands = new[] { originalCommand };
                    break;
            }

            foreach (var command in commands)
            {
                var handlingKernel = GetHandlingKernel(command, context);

                command.SchedulingScope ??= handlingKernel.SchedulingScope;
                command.TargetKernelName ??= handlingKernel.Name;

                if (command.Parent is null && 
                    !CommandEqualityComparer.Instance.Equals(command, originalCommand))
                {
                    command.Parent = originalCommand;
                }

                if (handlingKernel is ProxyKernel proxyKernel)
                {
                    var kernelInfo = proxyKernel.KernelInfo;
                }
            }

            return true;
        }

        private bool TryPreprocessLanguageServiceCommand(LanguageServiceCommand command, KernelInvocationContext context, out IReadOnlyList<KernelCommand> commands)
        {
            var postProcessCommands = new List<KernelCommand>();
            var tree = SubmissionParser.Parse(command.Code, command.TargetKernelName);
            var rootNode = tree.GetRoot();
            var sourceText = SourceText.From(command.Code);
            var lines = sourceText.Lines;
            if (command.LinePosition.Line < 0
                || command.LinePosition.Line >= lines.Count
                || command.LinePosition.Character < 0
                || command.LinePosition.Character > lines[command.LinePosition.Line].Span.Length)
            {
                context.Fail(command, message: $"The specified position {command.LinePosition}");
                commands = null;
                return false;
            }

            // TextSpan.Contains only checks `[start, end)`, but we need to allow for `[start, end]`
            var absolutePosition = tree.GetAbsolutePosition(command.LinePosition);

            if (absolutePosition >= tree.Length)
            {
                absolutePosition--;
            }
            else if (char.IsWhiteSpace(rootNode.Text[absolutePosition]))
            {
                absolutePosition--;
            }

            if (rootNode.FindNode(absolutePosition) is LanguageNode node)
            {
                var nodeStartLine = sourceText.Lines.GetLinePosition(node.Span.Start).Line;
                var offsetNodeLine = command.LinePosition.Line - nodeStartLine;
                var position = new LinePosition(offsetNodeLine, command.LinePosition.Character);

                // create new command
                var offsetLanguageServiceCommand = command.With(
                    node,
                    position);

                offsetLanguageServiceCommand.TargetKernelName = node switch
                {
                    DirectiveNode => Name,
                    _ => node.KernelName,
                };

                postProcessCommands.Add(offsetLanguageServiceCommand);
            }

            commands = postProcessCommands;

            return true;
        }

        public FrontendEnvironment FrontendEnvironment
        {
            get => _frontendEnvironment ??=
                       ParentKernel?.FrontendEnvironment ??
                       new AutomationEnvironment();
            set => _frontendEnvironment = value;
        }

        protected bool IsDisposed => _disposables.IsDisposed;

        public IObservable<KernelEvent> KernelEvents => _kernelEvents;
        
        public abstract string LanguageName { get; }
        
        public virtual string LanguageVersion { get; }

        public string Name { get; }

        public KernelInfo KernelInfo
        {
            get
            {
                return _kernelInfo ??= CreateKernelInfo();
            }
        }

        protected virtual KernelInfo CreateKernelInfo() =>
            new(Name, LanguageName, LanguageVersion)
            {
                SupportedKernelCommands = _supportedCommandTypes.Select(t => new KernelCommandInfo(t.Name)).ToArray(),
                SupportedDirectives = Directives.Select(d => new DirectiveInfo(d.Name)).ToArray(),
            };

        public IReadOnlyCollection<Command> Directives => SubmissionParser.Directives;

        public void AddDirective(Command command) => SubmissionParser.AddDirective(command);

        public void RegisterCommandHandler<TCommand>(Func<TCommand, KernelInvocationContext, Task> handler)
            where TCommand : KernelCommand
        {
            if (this is IKernelCommandHandler<TCommand>)
            {
                throw new InvalidOperationException($"Command {typeof(TCommand)} is already directly implemented and registration of an alternative handler is not supported.");
            }
            RegisterCommandType<TCommand>();
            _dynamicHandlers[typeof(TCommand)] = (command, context) => handler((TCommand)command, context);
        }

        public void RegisterCommandType<TCommand>()
            where TCommand : KernelCommand
        {
            // FIX: (RegisterCommandType) why is this a separate gesture from RegisterCommand?
            if (_supportedCommandTypes.Add(typeof(TCommand)))
            {
                var defaultHandler = CreateDefaultHandlerForCommandType<TCommand>() ?? throw new InvalidOperationException("CreateDefaultHandlerForCommandType should not return null");
                
                _dynamicHandlers[typeof(TCommand)] = (command, context) => defaultHandler((TCommand)command, context);
            }
        }

        protected virtual Func<TCommand, KernelInvocationContext, Task> CreateDefaultHandlerForCommandType<TCommand>() where TCommand : KernelCommand
        {
            return (_,_) => Task.CompletedTask;
        }

        internal virtual async Task HandleAsync(
            KernelCommand command,
            KernelInvocationContext context)
        {
            TrySetHandler(command, context);
            await command.InvokeAsync(context);
        }

        protected internal virtual void DelegatePublication(KernelEvent kernelEvent)
        {
            // FIX: (DelegatePublication) only overrides are used... does this need to be here?
            if (kernelEvent is null)
            {
                throw new ArgumentNullException(nameof(kernelEvent));
            }

            PublishEvent(kernelEvent);
        }

        public async Task<KernelCommandResult> SendAsync(
            KernelCommand command,
            CancellationToken cancellationToken)
        {
            if (command is null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            command.ShouldPublishCompletionEvent ??= true;

            var context = KernelInvocationContext.Establish(command);

            // only subscribe for the root command 
            var currentCommandOwnsContext = CommandEqualityComparer.Instance.Equals(context.Command, command);

            IDisposable disposable;

            if (currentCommandOwnsContext)
            {
                disposable = context.KernelEvents.Subscribe(PublishEvent);

                if (cancellationToken != CancellationToken.None &&
                    cancellationToken != default)
                {
                    cancellationToken.Register(() =>
                    {
                        context.Cancel();
                    });
                }
            }
            else
            {
                disposable = Disposable.Empty;
            }

            using (disposable)
            {
                if (TryPreprocessCommands(command, context, out var commands))
                {
                    SetHandlingKernel(command, context);

                    foreach (var c in commands)
                    {
                        switch (c)
                        {
                            case Quit quit:
                                quit.SchedulingScope = SchedulingScope;
                                quit.TargetKernelName = Name;
                                await InvokePipelineAndCommandHandler(quit);
                                break;

                            case Cancel cancel:
                                cancel.SchedulingScope = SchedulingScope;
                                cancel.TargetKernelName = Name;
                                Scheduler.CancelCurrentOperation((inflight) =>
                                {
                                    context.Publish(new CommandCancelled(cancel, inflight));
                                });
                                await InvokePipelineAndCommandHandler(cancel);
                                break;

                            case RequestDiagnostics _:
                                {
                                    if (_countOfLanguageServiceCommandsInFlight > 0)
                                    {
                                        context.CancelWithSuccess();
                                        return context.Result;
                                    }

                                    if (_inFlightContext is { } inflight)
                                    {
                                        inflight.Complete(inflight.Command);
                                    }

                                    _inFlightContext = context;

                                    await RunOnFastPath(context, c, cancellationToken);

                                    _inFlightContext = null;
                                }
                                break;
                            case RequestHoverText _:
                            case RequestCompletions _:
                            case RequestSignatureHelp _:
                                {
                                    if (_inFlightContext is { } inflight)
                                    {
                                        inflight.CancelWithSuccess();
                                    }

                                    Interlocked.Increment(ref _countOfLanguageServiceCommandsInFlight);

                                    await RunOnFastPath(context, c, cancellationToken);

                                    Interlocked.Decrement(ref _countOfLanguageServiceCommandsInFlight);
                                }
                                break;

                            default:
                                await Scheduler.RunAsync(
                                    c,
                                    InvokePipelineAndCommandHandler,
                                    c.SchedulingScope.ToString(),
                                    cancellationToken: cancellationToken)
                                    .ContinueWith(t =>
                                    {
                                        if (t.IsCanceled)
                                        {
                                            context.Cancel();
                                        }
                                    }, cancellationToken);
                                break;
                        }
                    }

                    if (currentCommandOwnsContext)
                    {
                        await context.DisposeAsync();
                    }
                }
            }

            return context.ResultFor(command);
        }

        internal SchedulingScope SchedulingScope =>
            ParentKernel is null
                ? SchedulingScope.Parse(Name)
                : ParentKernel.SchedulingScope.Append($"{Name}");

        private async Task RunOnFastPath(KernelInvocationContext context,
            KernelCommand command, CancellationToken cancellationToken)
        {
            var fastPathScheduler = await context.HandlingKernel.GetFastPathSchedulerAsync(context);
            await fastPathScheduler.RunAsync(
                    command,
                    InvokePipelineAndCommandHandler,
                    command.SchedulingScope.ToString(),
                    cancellationToken: cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled)
                    {
                        context.Cancel();
                    }
                }, cancellationToken);
        }

        private async Task<IKernelScheduler<KernelCommand, KernelCommandResult>> GetFastPathSchedulerAsync(
            KernelInvocationContext invocationContext)
        {
            await _fastPathSchedulerLock.WaitAsync();
            try
            {
                if (_fastPathScheduler is null)
                {
                    await SendAsync(
                        new AnonymousKernelCommand((_, _) => Task.CompletedTask, invocationContext.HandlingKernel.Name,
                            invocationContext.Command), invocationContext.CancellationToken);
                    _fastPathScheduler = new ImmediateScheduler<KernelCommand, KernelCommandResult>();

                }
            }
            finally
            {
                _fastPathSchedulerLock.Release();
            }

            return _fastPathScheduler;
        }

        internal async Task<KernelCommandResult> InvokePipelineAndCommandHandler(KernelCommand command)
        {
            var context = KernelInvocationContext.Establish(command);

            try
            {
                SetHandlingKernel(command, context);

                await Pipeline.SendAsync(command, context);

                if (!CommandEqualityComparer.Instance.Equals(command, context.Command))
                {
                    context.Complete(command);
                }
                
                return context.ResultFor(command);
            }
            catch (Exception exception)
            {
                if (!context.IsComplete)
                {
                    context.Fail(command, exception);
                }

                throw;
            }
        }

        protected internal KernelScheduler<KernelCommand, KernelCommandResult> Scheduler
        {
            get
            {
                if (_commandScheduler is null)
                {
                    SetScheduler(new KernelScheduler<KernelCommand, KernelCommandResult>());
                }

                return _commandScheduler;
            }
        }

        protected internal void SetScheduler(KernelScheduler<KernelCommand, KernelCommandResult> scheduler)
        {
            _commandScheduler = scheduler;

            _commandScheduler.RegisterDeferredOperationSource(GetDeferredOperations, InvokePipelineAndCommandHandler);
        }

        protected IReadOnlyList<KernelCommand> GetDeferredOperations(KernelCommand command, string scope)
        {
            if (!command.SchedulingScope.Contains(SchedulingScope))
            {
                return Array.Empty<KernelCommand>();
            }

            var splitCommands = new List<KernelCommand>();

            while (_deferredCommands.TryDequeue(out var kernelCommand))
            {
                kernelCommand.TargetKernelName = Name;
                kernelCommand.SchedulingScope = SchedulingScope;

                var currentInvocationContext = KernelInvocationContext.Current;

                if (TryPreprocessCommands(kernelCommand, currentInvocationContext, out var commands))
                {
                    splitCommands.AddRange(commands);
                }
            }

            return splitCommands;
        }

        public virtual Task HandleAsync(
            RequestKernelInfo command, 
            KernelInvocationContext context)
        {
            context.Publish(new KernelInfoProduced(KernelInfo, command));

            return Task.CompletedTask;
        }

        protected virtual bool CanHandle(KernelCommand command)
        {
            if (command.TargetKernelName is not null &&
                command.TargetKernelName != Name)
            {

            }

            if (command.DestinationUri is not null)
            {

            }

            // FIX: (CompositeCanHandle) 
            return true;
        }

        private protected virtual Kernel GetHandlingKernel(KernelCommand command, KernelInvocationContext invocationContext)
        {
            if (CanHandle(command))
            {
                return this;
            }

            return null;
        }

        protected internal void PublishEvent(KernelEvent kernelEvent)
        {
            if (kernelEvent is null)
            {
                throw new ArgumentNullException(nameof(kernelEvent));
            }

            _kernelEvents.OnNext(kernelEvent);
        }

        public void RegisterForDisposal(Action dispose) => RegisterForDisposal(Disposable.Create(dispose));

        public void RegisterForDisposal(IDisposable disposable)
        {
            if (disposable is null)
            {
                throw new ArgumentNullException(nameof(disposable));
            }

            _disposables.Add(disposable);
        }

        private Task HandleRequestCompletionsAsync(
            RequestCompletions command,
            KernelInvocationContext context)
        {
            if (command.LanguageNode is DirectiveNode directiveNode)
            {
                var requestPosition = SourceText.From(command.Code)
                                                .Lines
                                                .GetPosition(command.LinePosition.ToCodeAnalysisLinePosition());

                var completions = GetDirectiveCompletionItems(
                    directiveNode,
                    requestPosition);

                var upToCursor =
                    directiveNode.Text[..command.LinePosition.Character];

                var indexOfPreviousSpace =
                    Math.Max(
                        0,
                        upToCursor.LastIndexOf(" ", StringComparison.CurrentCultureIgnoreCase) + 1);

                var resultRange = new LinePositionSpan(
                    new LinePosition(command.LinePosition.Line, indexOfPreviousSpace),
                    command.LinePosition);

                context.Publish(
                    new CompletionsProduced(
                        completions, command, resultRange));
            }

            return Task.CompletedTask;
        }

        private IEnumerable<CompletionItem> GetDirectiveCompletionItems(
            DirectiveNode directiveNode,
            int requestPosition)
        {
            var directiveParsers = new List<Parser>();

            directiveParsers.AddRange(
                GetDirectiveParsersForCompletion(directiveNode, requestPosition));

            var result = directiveNode.GetDirectiveParseResult();
            if (result.CommandResult.Command == ChooseKernelDirective)
            {
                return result.GetCompletions()
                             .Select(s => SubmissionParser.CompletionItemFor(s.Label, result));
            }

            var allCompletions = new List<CompletionItem>();
            var topDirectiveParser = SubmissionParser.GetDirectiveParser();
            var prefix = topDirectiveParser.Configuration.RootCommand.Name + " ";
            requestPosition += prefix.Length;

            foreach (var parser in directiveParsers)
            {
                var effectiveText = $"{prefix}{directiveNode.Text}";

                var parseResult = parser.Parse(effectiveText);

                var suggestions = parseResult.GetCompletions(requestPosition);

                var completions = suggestions
                                  .Select(s => SubmissionParser.CompletionItemFor(s.Label, parseResult))
                                  .ToArray();

                allCompletions.AddRange(completions);
            }

            return allCompletions
                   .Distinct(CompletionItemComparer.Instance)
                   .ToArray();
        }

        private protected virtual IEnumerable<Parser> GetDirectiveParsersForCompletion(
            DirectiveNode directiveNode,
            int requestPosition)
        {
            yield return SubmissionParser.GetDirectiveParser();
        }

        private protected void TrySetHandler(
            KernelCommand command,
            KernelInvocationContext context)
        {
            if (command.Handler is null)
            {
                switch (command, this)
                {
                    case (SubmitCode submitCode, IKernelCommandHandler<SubmitCode> submitCodeHandler):
                        SetHandler(submitCode, submitCodeHandler);
                        break;

                    case (RequestCompletions {LanguageNode: DirectiveNode} rq, _):
                        rq.Handler = (__, ___) => HandleRequestCompletionsAsync(rq, context);
                        break;

                    case (RequestCompletions requestCompletion, IKernelCommandHandler<RequestCompletions>
                        requestCompletionHandler):
                        SetHandler(requestCompletion, requestCompletionHandler);
                        break;

                    case (RequestDiagnostics requestDiagnostics, IKernelCommandHandler<RequestDiagnostics>
                        requestDiagnosticsHandler):
                        SetHandler(requestDiagnostics, requestDiagnosticsHandler);
                        break;

                    case (RequestHoverText hoverCommand, IKernelCommandHandler<RequestHoverText> requestHoverTextHandler
                        ):
                        SetHandler(hoverCommand, requestHoverTextHandler);
                        break;

                    case (RequestSignatureHelp requestSignatureHelp, IKernelCommandHandler<RequestSignatureHelp>
                        requestSignatureHelpHandler):
                        SetHandler(requestSignatureHelp, requestSignatureHelpHandler);
                        break;

                    case (ChangeWorkingDirectory changeWorkingDirectory, IKernelCommandHandler<ChangeWorkingDirectory> changeWorkingDirectoryHandler):
                        SetHandler(changeWorkingDirectory, changeWorkingDirectoryHandler);
                        break;

                    case (RequestKernelInfo requestKernelInfo, IKernelCommandHandler<RequestKernelInfo> requestKernelInfoHandler):
                        SetHandler(requestKernelInfo, requestKernelInfoHandler);
                        break;

                    default:
                        TrySetDynamicHandler(command);
                        break;
                }
            }
        }

        private void TrySetDynamicHandler(KernelCommand command)
        {
            if (_dynamicHandlers.TryGetValue(command.GetType(), out var handler))
            {
                command.Handler = handler;
            }
        }

        private static void SetHandler<T>(T command, IKernelCommandHandler<T> handler)
            where T : KernelCommand =>
            command.Handler = (_, context) =>
                handler.HandleAsync(command, context);

        protected virtual void SetHandlingKernel(
            KernelCommand command,
            KernelInvocationContext context) => context.HandlingKernel = this;

        public void Dispose() => _disposables.Dispose();

        public virtual ChooseKernelDirective ChooseKernelDirective => _chooseKernelDirective ??= new(this);

        public bool SupportsCommand<T>() where T : KernelCommand
        {
            return this is IKernelCommandHandler<T> || _dynamicHandlers.ContainsKey(typeof(T));
        }

        public virtual IKernelValueDeclarer GetValueDeclarer(object value) => KernelValueDeclarer.Default;

        public override string ToString()
        {
            var value = $"{base.ToString()}: {Name}";

            var kernelInfoUri = KernelInfo.Uri;
            if (kernelInfoUri is { } uri)
            {
                value += $" ({uri})";
            }

            return value;
        }
    }
}
