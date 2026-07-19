using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.Mcp {
	static class McpDebuggerMutationCoordinator {
		public static void Run(Func<Action, bool> tryBeginInvoke, Action action, TimeSpan timeout, CancellationToken cancellationToken) {
			if (tryBeginInvoke is null)
				throw new ArgumentNullException(nameof(tryBeginInvoke));
			if (action is null)
				throw new ArgumentNullException(nameof(action));

			cancellationToken.ThrowIfCancellationRequested();
			var operation = new MutationOperation(tryBeginInvoke, action);
			if (!tryBeginInvoke(operation.Execute))
				throw CreateDispatcherShutdownException("state update");

			var completed = false;
			try {
				completed = operation.Completion.Wait(timeout, cancellationToken);
			}
			catch (OperationCanceledException) {
				if (operation.TryAbandon())
					throw;
			}

			// Cancellation and timeout can abandon only a callback that has not started. Once the
			// dispatcher owns the operation, wait for its FIFO barrier so a mutation is never reported
			// as canceled or timed out and then applied later.
			if (!completed && operation.TryAbandon())
				throw new TimeoutException("Timed out waiting for the debugger state to update.");

			var error = operation.Completion.GetAwaiter().GetResult();
			if (error is not null)
				ExceptionDispatchInfo.Capture(error).Throw();
		}

		static InvalidOperationException CreateDispatcherShutdownException(string operation) =>
			new InvalidOperationException($"The debugger dispatcher is shutting down and rejected the debugger {operation}.");

		sealed class MutationOperation {
			const int StatePending = 0;
			const int StateRunning = 1;
			const int StateAbandoned = 2;
			const int StateCompleted = 3;

			readonly Func<Action, bool> tryBeginInvoke;
			readonly Action action;
			readonly TaskCompletionSource<Exception?> completionSource;
			int state;

			public Task<Exception?> Completion => completionSource.Task;

			public MutationOperation(Func<Action, bool> tryBeginInvoke, Action action) {
				this.tryBeginInvoke = tryBeginInvoke;
				this.action = action;
				completionSource = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			public bool TryAbandon() =>
				Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending;

			public void Execute() {
				if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending)
					return;

				Exception? error = null;
				try {
					action();
				}
				catch (Exception ex) {
					error = ex;
				}

				try {
					// Debugger setters enqueue their actual mutations. This second dispatcher hop is
					// the FIFO barrier after those mutations and their corresponding events.
					if (!tryBeginInvoke(() => Complete(error)))
						Complete(error ?? CreateDispatcherShutdownException("completion barrier"));
				}
				catch (Exception ex) {
					Complete(error ?? ex);
				}
			}

			void Complete(Exception? error) {
				if (Interlocked.CompareExchange(ref state, StateCompleted, StateRunning) == StateRunning)
					completionSource.TrySetResult(error);
			}
		}
	}
}
