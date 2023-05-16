// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pocket;

namespace Microsoft.DotNet.Interactive;

public class KernelScheduler<T, TResult> : IDisposable, IKernelScheduler<T, TResult>
{
    private static readonly Logger Log = new("KernelScheduler");

    private readonly CompositeDisposable _disposables;
    private readonly List<DeferredOperationSource> _deferredOperationSources = new();
    private readonly CancellationTokenSource _schedulerDisposalSource = new();
    private readonly Task _runLoopTask;

    private readonly BlockingCollection<ScheduledOperation> _topLevelScheduledOperations = new();
    private ScheduledOperation _currentlyRunningTopLevelOperation;

    public KernelScheduler()
    {
        _runLoopTask = Task.Factory.StartNew(
            ScheduledOperationRunLoop,
            TaskCreationOptions.LongRunning,
            _schedulerDisposalSource.Token);

        _disposables = new CompositeDisposable
        {
            _schedulerDisposalSource.Cancel,
            _schedulerDisposalSource,
            _topLevelScheduledOperations,
        };
    }

    public void CancelCurrentOperation(Action<T> onCancellation = null)
    {
        if (_currentlyRunningTopLevelOperation is { } operation)
        {
            onCancellation?.Invoke(operation.Value);
            operation.TaskCompletionSource.TrySetCanceled(_schedulerDisposalSource.Token);
            _currentlyRunningTopLevelOperation = null;
        }
    }

    public Task<TResult> RunAsync(
        T value,
        KernelSchedulerDelegate<T, TResult> onExecuteAsync,
        string scope = "default",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ScheduledOperation operation;

        if (_currentlyRunningTopLevelOperation is { } topLevelOperation &&
            IsChildOperation(topLevelOperation.Value, value))
        {
            operation = new ScheduledOperation(
                value,
                onExecuteAsync,
                isDeferred: false,
                isChildOperation: true,
                executionContext: null,
                scope,
                cancellationToken);
            RunBeforeCurrentOperationIsCompleted(operation);
        }
        else
        {
            operation = new ScheduledOperation(
                value,
                onExecuteAsync,
                isDeferred: false,
                isChildOperation: false,
                ExecutionContext.Capture(),
                scope: scope,
                cancellationToken: cancellationToken);
            RunAfterCurrentOperations(cancellationToken, operation);
        }

        return operation.TaskCompletionSource.Task;
    }

    private void ScheduledOperationRunLoop(object _)
    {
        foreach (var operation in _topLevelScheduledOperations.GetConsumingEnumerable(_schedulerDisposalSource.Token))
        {
            _currentlyRunningTopLevelOperation = operation;

            var executionContext = operation.ExecutionContext;

            if (executionContext is null)
            {
                Log.Warning($"{nameof(operation.ExecutionContext)} was null for operation {operation}");
                executionContext = ExecutionContext.Capture();
            }

            _currentlyRunningTopLevelOperation = operation;

            try
            {
                ExecutionContext.Run(
                    executionContext!.CreateCopy(),
                    _ => RunBeforeCurrentOperationIsCompleted(operation),
                    operation);

                operation.TaskCompletionSource.Task.Wait(_schedulerDisposalSource.Token);
            }
            catch (Exception e)
            {
                Log.Error("while executing {operation}", e, operation);
            }
            finally
            {
                _currentlyRunningTopLevelOperation = default;
            }
        }
    }

    private void Run(ScheduledOperation operation)
    {
        _currentlyRunningTopLevelOperation ??= operation;

        using var logOp = Log.OnEnterAndConfirmOnExit();
        logOp.Info("{value}", operation.Value);

        try
        {
            var operationTask = operation
                .ExecuteAsync()
                .ContinueWith(t =>
                {
                    if (!operation.TaskCompletionSource.Task.IsCompleted)
                    {
                        if (t.GetIsCompletedSuccessfully())
                        {
                            operation.TaskCompletionSource.TrySetResult(t.Result);
                        }
                        else if (t.Exception is { })
                        {
                            operation.TaskCompletionSource.SetException(t.Exception);
                        }
                    }
                });

            Task.WaitAny(new[]
            {
                operationTask,
                operation.TaskCompletionSource.Task
            }, _schedulerDisposalSource.Token);

            logOp.Succeed();
        }
        catch (Exception exception)
        {
            if (!operation.TaskCompletionSource.Task.IsCompleted)
            {
                operation.TaskCompletionSource.SetException(exception);
            }
        }
    }

    private void RunAfterCurrentOperations(CancellationToken cancellationToken, ScheduledOperation operation)
    {
        _topLevelScheduledOperations.Add(operation, cancellationToken);
    }

    private void RunBeforeCurrentOperationIsCompleted(ScheduledOperation operation)
    {
        try
        {
            foreach (ScheduledOperation deferredOperation in OperationsToRunBefore(operation))
            {
                Run(deferredOperation);
            }

            Run(operation);

            operation.TaskCompletionSource.Task.Wait(_schedulerDisposalSource.Token);
        }
        catch (Exception exception)
        {
            Log.Error(exception);
        }
    }

    private IEnumerable<ScheduledOperation> OperationsToRunBefore(
        ScheduledOperation operation)
    {
        for (var i = 0; i < _deferredOperationSources.Count; i++)
        {
            var source = _deferredOperationSources[i];

            var deferredOperations = source.GetDeferredOperations(
                operation.Value,
                operation.Scope);

            for (var j = 0; j < deferredOperations.Count; j++)
            {
                var deferred = deferredOperations[j];

                var deferredOperation = new ScheduledOperation(
                    deferred,
                    source.OnExecuteAsync,
                    true,
                    false,
                    scope: operation.Scope);

                yield return deferredOperation;
            }
        }
    }

    public void RegisterDeferredOperationSource(
        GetDeferredOperationsDelegate getDeferredOperations,
        KernelSchedulerDelegate<T, TResult> kernelSchedulerOnExecuteAsync)
    {
        ThrowIfDisposed();

        _deferredOperationSources.Add(new DeferredOperationSource(kernelSchedulerOnExecuteAsync, getDeferredOperations));
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_schedulerDisposalSource.IsCancellationRequested)
        {
            throw new ObjectDisposedException($"{nameof(KernelScheduler<T, TResult>)} has been disposed.");
        }
    }

    public delegate IReadOnlyList<T> GetDeferredOperationsDelegate(T operationToExecute, string queueName);

    private class ScheduledOperation
    {
        private static readonly Action<Action, CancellationToken> _runWithControlledExecution = default;

        static ScheduledOperation()
        {
            try
            {
                // todo: this is still a problem with fsi

                // ControlledExecution.Run isn't available in .NET Standard but since we're most likely actually running in .NET 7+, we can try to bind a delegate to it.

                //if (Type.GetType("System.Runtime.ControlledExecution, System.Private.CoreLib", false) is { } controlledExecutionType &&
                //    controlledExecutionType.GetMethod("Run", BindingFlags.Static | BindingFlags.Public) is { } runMethod)
                //{
                //    var actionParameter = Expression.Parameter(typeof(Action), "action");

                //    var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

                //    _runWithControlledExecution = Expression.Lambda<Action<Action, CancellationToken>>(
                //                                                Expression.Call(runMethod, actionParameter, cancellationTokenParameter),
                //                                                actionParameter,
                //                                                cancellationTokenParameter)
                //                                            .Compile();
                //}
            }
            catch
            {
            }
        }

        private readonly KernelSchedulerDelegate<T, TResult> _onExecuteAsync;
        private readonly CancellationToken _cancellationToken;

        public ScheduledOperation(
            T value,
            KernelSchedulerDelegate<T, TResult> onExecuteAsync,
            bool isDeferred,
            bool isChildOperation,
            ExecutionContext executionContext = default,
            string scope = "default",
            CancellationToken cancellationToken = default)
        {
            Value = value;
            IsDeferred = isDeferred;
            IsChildOperation = isChildOperation;
            ExecutionContext = executionContext;
            _onExecuteAsync = onExecuteAsync;
            _cancellationToken = cancellationToken;
            Scope = scope;

            TaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    TaskCompletionSource.TrySetCanceled();
                });
            }
        }

        public TaskCompletionSource<TResult> TaskCompletionSource { get; }

        public T Value { get; }

        public bool IsDeferred { get; }

        public bool IsChildOperation { get; }

        public ExecutionContext ExecutionContext { get; }

        public string Scope { get; }

        public Task<TResult> ExecuteAsync()
        {
            if (_runWithControlledExecution is not null &&
                _cancellationToken.CanBeCanceled)
            {
                try
                {
                    TResult result = default;

                    _runWithControlledExecution(() =>
                    {
                        var r = _onExecuteAsync(Value).GetAwaiter().GetResult();
                        result = r;
                    }, _cancellationToken);

                    return Task.FromResult(result);
                }
                catch (Exception exception)
                {
                    return Task.FromException<TResult>(exception);
                }
            }
            else
            {
                return _onExecuteAsync(Value);
            }
        }

        public override string ToString()
        {
            return Value.ToString();
        }
    }

    private class DeferredOperationSource
    {
        public DeferredOperationSource(KernelSchedulerDelegate<T, TResult> kernelSchedulerOnExecuteAsync, GetDeferredOperationsDelegate getDeferredOperations)
        {
            OnExecuteAsync = kernelSchedulerOnExecuteAsync;
            GetDeferredOperations = getDeferredOperations;
        }

        public GetDeferredOperationsDelegate GetDeferredOperations { get; }

        public KernelSchedulerDelegate<T, TResult> OnExecuteAsync { get; }
    }

    protected virtual bool IsChildOperation(T current, T incoming) => false;
}

static class DotNetStandardHelpers
{
#if !NETSTANDARD2_0
    internal static bool GetIsCompletedSuccessfully(this Task task)
    {
        return task.IsCompletedSuccessfully;
    }
#else
        // NetStandard 2.1
        // internal const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;                          // bin: 0000 0001 0000 0000 0000 0000 0000 0000
        // public bool IsCompletedSuccessfully => (m_stateFlags & TASK_STATE_COMPLETED_MASK) == TASK_STATE_RAN_TO_COMPLETION;
        // <see cref="IsCompleted"/> will return true when the Task is in one of the three
        // final states: <see cref="System.Threading.Tasks.TaskStatus.RanToCompletion">RanToCompletion</see>,
        // <see cref="System.Threading.Tasks.TaskStatus.Faulted">Faulted</see>, or
        // <see cref="System.Threading.Tasks.TaskStatus.Canceled">Canceled</see>.
        static public bool GetIsCompletedSuccessfully(this Task task)
        {
            return task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
        }
#endif
}