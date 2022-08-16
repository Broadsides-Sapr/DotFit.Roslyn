﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.TestHooks;

namespace Roslyn.Utilities
{
    /// <summary>
    /// A queue where items can be added to to be processed in batches after some delay has passed. When processing
    /// happens, all the items added since the last processing point will be passed along to be worked on.  Rounds of
    /// processing happen serially, only starting up after a previous round has completed.
    /// <para>
    /// Failure to complete a particular batch (either due to cancellation or some faulting error) will not prevent
    /// further batches from executing. The only thing that will permenantly stop this queue from processing items is if
    /// the <see cref="CancellationToken"/> passed to the constructor switches to <see
    /// cref="CancellationToken.IsCancellationRequested"/>.
    /// </para>
    /// </summary>
    internal class AsyncBatchingWorkQueue<TItem, TResult>
    {
        /// <summary>
        /// Delay we wait after finishing the processing of one batch and starting up on then.
        /// </summary>
        private readonly TimeSpan _delay;

        /// <summary>
        /// Equality comparer uses to dedupe items if present.
        /// </summary>
        private readonly IEqualityComparer<TItem>? _equalityComparer;

        /// <summary>
        /// Callback to actually perform the processing of the next batch of work.
        /// </summary>
        private readonly Func<ImmutableSegmentedList<TItem>, CancellationToken, ValueTask<TResult>> _processBatchAsync;
        private readonly IAsynchronousOperationListener _asyncListener;
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationSeries _cancellationSeries;

        #region protected by lock

        /// <summary>
        /// Lock we will use to ensure the remainder of these fields can be accessed in a threadsafe
        /// manner.  When work is added we'll place the data into <see cref="_nextBatch"/>.
        /// We'll then kick of a task to process this in the future if we don't already have an
        /// existing task in flight for that.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Data added that we want to process in our next update task.
        /// </summary>
        private readonly ImmutableSegmentedList<(TItem item, CancellationToken cancellationToken)>.Builder _nextBatch =
            ImmutableSegmentedList.CreateBuilder<(TItem item, CancellationToken cancellationToken)>();

        /// <summary>
        /// Used if <see cref="_equalityComparer"/> is present to ensure only unique items are added to <see
        /// cref="_nextBatch"/>.
        /// </summary>
        private readonly SegmentedHashSet<TItem> _uniqueItems;

        /// <summary>
        /// Task kicked off to do the next batch of processing of <see cref="_nextBatch"/>. These
        /// tasks form a chain so that the next task only processes when the previous one completes.
        /// </summary>
        private Task<TResult?> _updateTask = Task.FromResult(default(TResult));

        /// <summary>
        /// Whether or not there is an existing task in flight that will process the current batch
        /// of <see cref="_nextBatch"/>.  If there is an existing in flight task, we don't need to
        /// kick off a new one if we receive more work before it runs.
        /// </summary>
        private bool _taskInFlight = false;

        /// <summary>
        /// Token controlling any items in the queue that haven't run yet.
        /// </summary>
        private CancellationToken _lastBatchCancellationToken;

        #endregion

        public AsyncBatchingWorkQueue(
            TimeSpan delay,
            Func<ImmutableSegmentedList<TItem>, CancellationToken, ValueTask<TResult>> processBatchAsync,
            IEqualityComparer<TItem>? equalityComparer,
            IAsynchronousOperationListener asyncListener,
            CancellationToken cancellationToken)
        {
            _delay = delay;
            _processBatchAsync = processBatchAsync;
            _equalityComparer = equalityComparer;
            _asyncListener = asyncListener;
            _cancellationToken = cancellationToken;

            _uniqueItems = new SegmentedHashSet<TItem>(equalityComparer);

            // Combine with our cancellation token so that any batch is controlled by that token as well.
            _cancellationSeries = new CancellationSeries(cancellationToken);
            CancelExistingWork();
        }

        /// <summary>
        /// Cancels any outstanding work in this queue.  Work that has not yet started will never run. Work that is in
        /// progress will request cancellation in a standard best effort fashion.
        /// </summary>
        public void CancelExistingWork()
        {
            lock (_gate)
                _lastBatchCancellationToken = _cancellationSeries.CreateNext();
        }

        public void AddWork(TItem item, bool cancelExistingWork = false)
        {
            using var _ = ArrayBuilder<TItem>.GetInstance(out var items);
            items.Add(item);

            AddWork(items, cancelExistingWork);
        }

        public void AddWork(IEnumerable<TItem> items, bool cancelExistingWork = false)
        {
            // Don't do any more work if we've been asked to shutdown.
            if (_cancellationToken.IsCancellationRequested)
                return;

            lock (_gate)
            {
                // if we were asked to cancel the prior set of items, do so now.
                if (cancelExistingWork)
                    CancelExistingWork();

                // add our work to the set we'll process in the next batch.
                AddItemsToBatch(items);

                if (!_taskInFlight)
                {
                    // No in-flight task.  Kick one off to process these messages a second from now.
                    // We always attach the task to the previous one so that notifications to the ui
                    // follow the same order as the notification the OOP server sent to us.
                    _updateTask = ContinueAfterDelay(_updateTask);
                    _taskInFlight = true;
                }
            }

            return;

            void AddItemsToBatch(IEnumerable<TItem> items)
            {
                // no equality comparer.  We want to process all items.
                if (_equalityComparer == null)
                {
                    foreach (var item in items)
                        _nextBatch.Add((item, _lastBatchCancellationToken));
                }
                else
                {
                    // We're deduping items.  Only add the item if it's the first time we've seen it.
                    foreach (var item in items)
                    {
                        if (_uniqueItems.Add(item))
                            _nextBatch.Add((item, _lastBatchCancellationToken));
                    }
                }
            }

            async Task<TResult?> ContinueAfterDelay(Task lastTask)
            {
                using var _ = _asyncListener.BeginAsyncOperation(nameof(AddWork));

                // Await the previous item in the task chain in a non-throwing fashion.  Regardless of whether that last
                // task completed successfully or not, we want to move onto the next batch.
                await lastTask.NoThrowAwaitableInternal(captureContext: false);

                // If we were asked to shutdown, immediately transition to the canceled state without doing any more work.
                _cancellationToken.ThrowIfCancellationRequested();

                // Ensure that we always yield the current thread this is necessary for correctness as we are called
                // inside a lock that _taskInFlight to true.  We must ensure that the work to process the next batch
                // must be on another thread that runs afterwards, can only grab the thread once we release it and will
                // then reset that bool back to false
                await Task.Yield().ConfigureAwait(false);
                await _asyncListener.Delay(_delay, _cancellationToken).ConfigureAwait(false);
                return await ProcessNextBatchAsync(_cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until the current batch of work completes and returns the last value successfully computed from <see
        /// cref="_processBatchAsync"/>.  If the last <see cref="_processBatchAsync"/> canceled or failed, then a
        /// corresponding canceled or faulted task will be returned that propagates that outwards.
        /// </summary>
        public Task<TResult?> WaitUntilCurrentBatchCompletesAsync()
        {
            lock (_gate)
                return _updateTask;
        }

        private async ValueTask<TResult?> ProcessNextBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (nextBatch, batchCancellationToken) = GetNextBatchAndResetQueue();

                // We may have no items if the entire batch was canceled (and no new work was added).
                if (nextBatch.IsEmpty)
                    return default;

                return await _processBatchAsync(nextBatch, batchCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
            {
                // Don't bubble up cancellation to the queue for the nested batch cancellation.  Just because we decided
                // to cancel this batch isn't something that should stop processing further batches.
                return default;
            }
            catch (Exception ex) when (FatalError.ReportAndPropagateUnlessCanceled(ex, ErrorSeverity.Critical))
            {
                // Report an exception if the batch fails for a non-cancellation reason.
                //
                // Note: even though we propagate this exception outwards, we will still continue processing further
                // batches due to the `await NoThrowAwaitableInternal()` above.  The sentiment being that generally
                // failures are recoverable here, and we will have reported the error so we can see in telemetry if we
                // have a problem that needs addressing.
                //
                // Not this code is unreachable because ReportAndPropagateUnlessCanceled returns false along all codepaths.
                throw ExceptionUtilities.Unreachable;
            }
        }

        private (ImmutableSegmentedList<TItem> items, CancellationToken batchCancellationToken) GetNextBatchAndResetQueue()
        {
            lock (_gate)
            {
                var builder = ImmutableSegmentedList.CreateBuilder<TItem>();
                CancellationToken? itemCancellationToken = null;
                foreach (var (item, cancellationToken) in _nextBatch)
                {
                    // ignore canceled items.
                    if (cancellationToken.IsCancellationRequested)
                        continue;

                    builder.Add(item);

                    // we have an uncanceled item.  The cancellation-token controlling it better be the same as any
                    // cancellation-tokens controlling the other items in this batch.
                    if (itemCancellationToken == null)
                    {
                        itemCancellationToken = cancellationToken;
                    }
                    else
                    {
                        Contract.ThrowIfFalse(cancellationToken.Equals(itemCancellationToken.Value));
                    }
                }

                // mark there being no existing update task so that the next OOP notification will
                // kick one off.
                _nextBatch.Clear();
                _uniqueItems.Clear();
                _taskInFlight = false;

                if (builder.Count > 0)
                {
                    // If we had an item to process, we must have a cancellation token for it.
                    Contract.ThrowIfNull(itemCancellationToken);
                }

                var items = builder.ToImmutable();

                return (items, itemCancellationToken.GetValueOrDefault());
            }
        }
    }
}
