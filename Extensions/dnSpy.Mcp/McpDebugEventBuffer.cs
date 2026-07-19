using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.Mcp {
	readonly struct McpDebugEventWaitResult {
		public McpDebugEventEntry? Event { get; }
		public long LatestSequence { get; }

		public McpDebugEventWaitResult(McpDebugEventEntry? @event, long latestSequence) {
			Event = @event;
			LatestSequence = latestSequence;
		}
	}

	sealed class McpDebugEventBuffer {
		readonly object lockObj;
		readonly int maxEvents;
		readonly List<McpDebugEventEntry> events;
		long nextSequence;
		TaskCompletionSource<bool> changedSource;

		public McpDebugEventBuffer(int maxEvents = 1000) {
			if (maxEvents <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxEvents));
			lockObj = new object();
			this.maxEvents = maxEvents;
			events = new List<McpDebugEventEntry>();
			changedSource = CreateChangedSource();
		}

		public McpDebugEventEntry Add(Func<long, McpDebugEventEntry> entryFactory) {
			if (entryFactory is null)
				throw new ArgumentNullException(nameof(entryFactory));

			McpDebugEventEntry entry;
			TaskCompletionSource<bool> sourceToSignal;
			lock (lockObj) {
				entry = entryFactory(++nextSequence) ?? throw new InvalidOperationException("The debug event factory returned null.");
				if (entry.Sequence != nextSequence)
					throw new InvalidOperationException("The debug event sequence must match the sequence supplied to the factory.");
				events.Add(entry);
				if (events.Count > maxEvents)
					events.RemoveRange(0, events.Count - maxEvents);
				sourceToSignal = changedSource;
				changedSource = CreateChangedSource();
			}
			sourceToSignal.TrySetResult(true);
			return entry;
		}

		public McpDebugEventEntry[] GetRecentEvents(string[]? eventKinds = null, int maxResults = 200, long? afterSequence = null, int? processId = null) {
			lock (lockObj) {
				IEnumerable<McpDebugEventEntry> query = events;
				if (afterSequence is not null)
					query = query.Where(a => a.Sequence > afterSequence.Value);
				if (processId is not null)
					query = query.Where(a => a.ProcessId == processId.Value);
				query = FilterByKind(query, eventKinds);
				return query.TakeLast(Math.Max(1, maxResults)).ToArray();
			}
		}

		public McpDebugEventEntry[] GetRecentOutput(int maxResults = 200, int? processId = null, long? afterSequence = null) {
			lock (lockObj) {
				IEnumerable<McpDebugEventEntry> query = events.Where(a => a.IsOutputLine);
				if (afterSequence is not null)
					query = query.Where(a => a.Sequence > afterSequence.Value);
				if (processId is not null)
					query = query.Where(a => a.ProcessId == processId.Value);
				return query.TakeLast(Math.Max(1, maxResults)).ToArray();
			}
		}

		public int Clear() {
			lock (lockObj) {
				var count = events.Count;
				events.Clear();
				return count;
			}
		}

		public Task<McpDebugEventWaitResult> WaitForEventAsync(string[]? eventKinds, long? afterSequence, int timeoutMilliseconds, int? processId = null, bool outputOnly = false, CancellationToken cancellationToken = default, CancellationToken serverCancellationToken = default) {
			if (timeoutMilliseconds < Timeout.Infinite)
				throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "Timeout must be -1 (infinite) or a non-negative number of milliseconds.");
			var lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverCancellationToken);
			return WaitForEventWithLifetimeAsync(eventKinds, afterSequence, timeoutMilliseconds, processId, outputOnly, lifetimeSource);
		}

		async Task<McpDebugEventWaitResult> WaitForEventWithLifetimeAsync(string[]? eventKinds, long? afterSequence, int timeoutMilliseconds, int? processId, bool outputOnly, CancellationTokenSource lifetimeSource) {
			using (lifetimeSource)
				return await WaitForEventCoreAsync(eventKinds, afterSequence, timeoutMilliseconds, processId, outputOnly, lifetimeSource.Token).ConfigureAwait(false);
		}

		async Task<McpDebugEventWaitResult> WaitForEventCoreAsync(string[]? eventKinds, long? afterSequence, int timeoutMilliseconds, int? processId, bool outputOnly, CancellationToken cancellationToken) {
			Task changedTask;
			long minSequence;
			lock (lockObj) {
				cancellationToken.ThrowIfCancellationRequested();
				minSequence = afterSequence ?? nextSequence;
				var match = TryFindEvent(eventKinds, minSequence, processId, outputOnly);
				if (match is not null)
					return new McpDebugEventWaitResult(match, match.Sequence);
				if (timeoutMilliseconds == 0)
					return CreateTimeoutResult(minSequence);
				changedTask = changedSource.Task;
			}

			using var timeoutSource = timeoutMilliseconds < 0 ? null : new CancellationTokenSource(timeoutMilliseconds);
			using var waitSource = timeoutSource is null ?
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) :
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

			while (true) {
				try {
					await changedTask.WaitAsync(waitSource.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested) {
					lock (lockObj) {
						var match = TryFindEvent(eventKinds, minSequence, processId, outputOnly);
						return match is null ? CreateTimeoutResult(minSequence) : new McpDebugEventWaitResult(match, match.Sequence);
					}
				}

				lock (lockObj) {
					var match = TryFindEvent(eventKinds, minSequence, processId, outputOnly);
					if (match is not null)
						return new McpDebugEventWaitResult(match, match.Sequence);
					changedTask = changedSource.Task;
				}
			}
		}

		McpDebugEventWaitResult CreateTimeoutResult(long minSequence) =>
			new McpDebugEventWaitResult(null, Math.Max(minSequence, nextSequence));

		McpDebugEventEntry? TryFindEvent(string[]? eventKinds, long minSequence, int? processId, bool outputOnly) {
			IEnumerable<McpDebugEventEntry> query = events.Where(a => a.Sequence > minSequence);
			if (processId is not null)
				query = query.Where(a => a.ProcessId == processId.Value);
			if (outputOnly)
				query = query.Where(a => a.IsOutputLine);
			query = FilterByKind(query, eventKinds);
			return query.OrderBy(a => a.Sequence).FirstOrDefault();
		}

		static IEnumerable<McpDebugEventEntry> FilterByKind(IEnumerable<McpDebugEventEntry> query, string[]? eventKinds) {
			if (eventKinds is null || eventKinds.Length == 0)
				return query;
			var kinds = new HashSet<string>(eventKinds.Where(a => !string.IsNullOrWhiteSpace(a)), StringComparer.OrdinalIgnoreCase);
			return query.Where(a => kinds.Contains(a.Kind));
		}

		static TaskCompletionSource<bool> CreateChangedSource() =>
			new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
	}

}
