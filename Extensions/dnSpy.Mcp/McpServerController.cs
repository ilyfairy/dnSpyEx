using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.App;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Scripting;
using dnSpy.Contracts.ToolWindows.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace dnSpy.Mcp {
	public enum McpServerState {
		Stopped,
		Starting,
		Running,
		Stopping,
	}

	[Export(typeof(McpServerController))]
	public sealed class McpServerController : IDisposable {
		const string ServerName = "dnSpyEx MCP";
		const string ServerVersion = "0.1.0";
		static readonly Guid OutputToolWindowGuid = new Guid("90A45E97-727E-4F31-8692-06E19218D99A");

		readonly object lockObj;
		readonly object debugEventLockObj;
		readonly IAppWindow appWindow;
		readonly IDsToolWindowService toolWindowService;
		readonly McpSettings mcpSettings;
		readonly McpOutputLogger logger;
		readonly DbgManager dbgManager;
		readonly DbgCodeBreakpointsService dbgCodeBreakpointsService;
		readonly DbgCodeBreakpointHitCountService dbgCodeBreakpointHitCountService;
		readonly DbgCallStackService dbgCallStackService;
		readonly AttachableProcessesService attachableProcessesService;
		readonly DbgLanguageService dbgLanguageService;
		readonly DbgDotNetBreakpointFactory dbgDotNetBreakpointFactory;
		readonly DbgExceptionSettingsService dbgExceptionSettingsService;
		readonly IDsDocumentService documentService;
		readonly IDocumentTreeView documentTreeView;
		readonly IDocumentTabService documentTabService;
		readonly IDecompilerService decompilerService;
		readonly ILanguageCompilerProvider[] languageCompilerProviders;
		readonly ISettingsService settingsService;
		readonly IServiceLocator serviceLocator;

		WebApplication? webApplication;
		CancellationTokenSource? serverCancellationTokenSource;
		McpServerState state;
		bool statusBarOpen;
		int statusBarVersion;
		bool isDisposed;
		bool isShuttingDown;
		long nextDebugEventSequence;
		readonly List<McpDebugEventEntry> debugEvents;

		[ImportingConstructor]
		McpServerController(IAppWindow appWindow, IDsToolWindowService toolWindowService, McpSettings mcpSettings, McpOutputLogger logger, DbgManager dbgManager, DbgCodeBreakpointsService dbgCodeBreakpointsService, DbgCodeBreakpointHitCountService dbgCodeBreakpointHitCountService, DbgCallStackService dbgCallStackService, AttachableProcessesService attachableProcessesService, DbgLanguageService dbgLanguageService, DbgDotNetBreakpointFactory dbgDotNetBreakpointFactory, DbgExceptionSettingsService dbgExceptionSettingsService, IDsDocumentService documentService, IDocumentTreeView documentTreeView, IDocumentTabService documentTabService, IDecompilerService decompilerService, [ImportMany] IEnumerable<ILanguageCompilerProvider> languageCompilerProviders, ISettingsService settingsService, IServiceLocator serviceLocator) {
			lockObj = new object();
			debugEventLockObj = new object();
			this.appWindow = appWindow;
			this.toolWindowService = toolWindowService;
			this.mcpSettings = mcpSettings;
			this.logger = logger;
			this.dbgManager = dbgManager;
			this.dbgCodeBreakpointsService = dbgCodeBreakpointsService;
			this.dbgCodeBreakpointHitCountService = dbgCodeBreakpointHitCountService;
			this.dbgCallStackService = dbgCallStackService;
			this.attachableProcessesService = attachableProcessesService;
			this.dbgLanguageService = dbgLanguageService;
			this.dbgDotNetBreakpointFactory = dbgDotNetBreakpointFactory;
			this.dbgExceptionSettingsService = dbgExceptionSettingsService;
			this.documentService = documentService;
			this.documentTreeView = documentTreeView;
			this.documentTabService = documentTabService;
			this.decompilerService = decompilerService;
			this.languageCompilerProviders = languageCompilerProviders.ToArray();
			this.settingsService = settingsService;
			this.serviceLocator = serviceLocator;
			debugEvents = new List<McpDebugEventEntry>();
			state = McpServerState.Stopped;
			appWindow.MainWindowClosed += AppWindow_MainWindowClosed;
			dbgManager.MessageUserMessage += DbgManager_MessageUserMessage;
			dbgManager.DbgManagerMessage += DbgManager_DbgManagerMessage;
			dbgManager.MessageProcessCreated += DbgManager_MessageProcessCreated;
			dbgManager.MessageProcessExited += DbgManager_MessageProcessExited;
			dbgManager.MessageModuleLoaded += DbgManager_MessageModuleLoaded;
			dbgManager.MessageExceptionThrown += DbgManager_MessageExceptionThrown;
			dbgManager.MessageEntryPointBreak += DbgManager_MessageEntryPointBreak;
			dbgManager.MessageStepComplete += DbgManager_MessageStepComplete;
			dbgManager.MessageProgramMessage += DbgManager_MessageProgramMessage;
			dbgManager.MessageAsyncProgramMessage += DbgManager_MessageAsyncProgramMessage;
			dbgManager.MessageBoundBreakpoint += DbgManager_MessageBoundBreakpoint;
		}

		public bool IsSupported => true;

		public McpServerState State {
			get {
				lock (lockObj)
					return state;
			}
		}

		public bool IsRunning => State == McpServerState.Running;
		public bool IsBusy => State == McpServerState.Starting || State == McpServerState.Stopping;
		public bool CanToggle => !IsBusy;
		public string EndpointUrl => GetEndpointUrl();

		public void ToggleAsync() {
			ShowOutput();
			switch (State) {
			case McpServerState.Stopped:
				_ = StartAsync();
				break;
			case McpServerState.Running:
				_ = StopAsync();
				break;
			default:
				logger.WriteLine($"MCP server is currently {State.ToString().ToLowerInvariant()}, ignoring toggle request.");
				break;
			}
		}

		public string GetToolTipText() => State switch {
			McpServerState.Starting => $"Starting MCP HTTP server ({EndpointUrl})",
			McpServerState.Stopping => $"Stopping MCP HTTP server ({EndpointUrl})",
			McpServerState.Running => $"Stop MCP HTTP server ({EndpointUrl})",
			_ => $"Start MCP HTTP server ({EndpointUrl})",
		};

		public static string NormalizeRoutePath(string? routePath) {
			var path = string.IsNullOrWhiteSpace(routePath) ? "/mcp" : routePath!.Trim();
			if (!path.StartsWith("/", StringComparison.Ordinal))
				path = "/" + path;
			while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
				path = path.Substring(0, path.Length - 1);
			return path;
		}

		public static string NormalizeListenAddress(string? listenAddress) =>
			string.IsNullOrWhiteSpace(listenAddress) ? "127.0.0.1" : listenAddress!.Trim();

		public static int NormalizePort(int port) => port is >= 1 and <= 65535 ? port : 38888;

		public void Dispose() {
			Shutdown(disposing: true);
		}

		void Shutdown(bool disposing) {
			WebApplication? app = null;
			CancellationTokenSource? cancellationTokenSource = null;
			lock (lockObj) {
				if (isDisposed)
					return;
				isDisposed = disposing;
				isShuttingDown = true;
				app = webApplication;
				cancellationTokenSource = serverCancellationTokenSource;
				webApplication = null;
				serverCancellationTokenSource = null;
				state = McpServerState.Stopped;
			}

			try {
				appWindow.MainWindowClosed -= AppWindow_MainWindowClosed;
				dbgManager.MessageUserMessage -= DbgManager_MessageUserMessage;
				dbgManager.DbgManagerMessage -= DbgManager_DbgManagerMessage;
				dbgManager.MessageProcessCreated -= DbgManager_MessageProcessCreated;
				dbgManager.MessageProcessExited -= DbgManager_MessageProcessExited;
				dbgManager.MessageModuleLoaded -= DbgManager_MessageModuleLoaded;
				dbgManager.MessageExceptionThrown -= DbgManager_MessageExceptionThrown;
				dbgManager.MessageEntryPointBreak -= DbgManager_MessageEntryPointBreak;
				dbgManager.MessageStepComplete -= DbgManager_MessageStepComplete;
				dbgManager.MessageProgramMessage -= DbgManager_MessageProgramMessage;
				dbgManager.MessageAsyncProgramMessage -= DbgManager_MessageAsyncProgramMessage;
				dbgManager.MessageBoundBreakpoint -= DbgManager_MessageBoundBreakpoint;
			}
			catch {
			}

			cancellationTokenSource?.Cancel();
			if (app is null) {
				cancellationTokenSource?.Dispose();
				return;
			}

			_ = ShutdownWebApplicationAsync(app, cancellationTokenSource);
		}

		async Task ShutdownWebApplicationAsync(WebApplication app, CancellationTokenSource? cancellationTokenSource) {
			try {
				try {
					using var stopCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					await app.StopAsync(stopCancellationTokenSource.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
				}
				catch {
				}
				await app.DisposeAsync().ConfigureAwait(false);
			}
			catch {
			}
			finally {
				cancellationTokenSource?.Dispose();
			}
		}

		void AppWindow_MainWindowClosed(object? sender, EventArgs e) => Shutdown(disposing: true);

		public McpDebugEventEntry[] GetRecentDebugEvents(string[]? eventKinds = null, int maxResults = 200, long? afterSequence = null, int? processId = null) {
			lock (debugEventLockObj) {
				IEnumerable<McpDebugEventEntry> query = debugEvents;
				if (afterSequence is not null)
					query = query.Where(a => a.Sequence > afterSequence.Value);
				if (processId is not null)
					query = query.Where(a => a.ProcessId == processId.Value);
				if (eventKinds is not null && eventKinds.Length > 0) {
					var kinds = new HashSet<string>(eventKinds.Where(a => !string.IsNullOrWhiteSpace(a)), StringComparer.OrdinalIgnoreCase);
					query = query.Where(a => kinds.Contains(a.Kind));
				}
				return query.TakeLast(Math.Max(1, maxResults)).ToArray();
			}
		}

		public McpDebugEventEntry[] GetRecentDebugOutput(int maxResults = 200, int? processId = null, long? afterSequence = null) {
			lock (debugEventLockObj) {
				IEnumerable<McpDebugEventEntry> query = debugEvents.Where(a => a.IsOutputLine);
				if (afterSequence is not null)
					query = query.Where(a => a.Sequence > afterSequence.Value);
				if (processId is not null)
					query = query.Where(a => a.ProcessId == processId.Value);
				return query.TakeLast(Math.Max(1, maxResults)).ToArray();
			}
		}

		public int ClearDebugEvents() {
			lock (debugEventLockObj) {
				var count = debugEvents.Count;
				debugEvents.Clear();
				return count;
			}
		}

		public McpDebugEventEntry? WaitForDebugEvent(string[]? eventKinds, long? afterSequence, int timeoutMilliseconds, int? processId = null, bool outputOnly = false) {
			var timeout = timeoutMilliseconds < 0 ? Timeout.Infinite : timeoutMilliseconds;
			lock (debugEventLockObj) {
				var match = TryFindDebugEvent(eventKinds, afterSequence, processId, outputOnly);
				if (match is not null)
					return match;

				var startTickCount = Environment.TickCount;
				while (true) {
					var remaining = timeout == Timeout.Infinite ? Timeout.Infinite : Math.Max(0, timeout - unchecked(Environment.TickCount - startTickCount));
					if (remaining == 0)
						return null;
					Monitor.Wait(debugEventLockObj, remaining);
					match = TryFindDebugEvent(eventKinds, afterSequence, processId, outputOnly);
					if (match is not null)
						return match;
				}
			}
		}

		McpDebugEventEntry? TryFindDebugEvent(string[]? eventKinds, long? afterSequence, int? processId, bool outputOnly) {
			var minSequence = afterSequence ?? 0;
			IEnumerable<McpDebugEventEntry> query = debugEvents.Where(a => a.Sequence > minSequence);
			if (processId is not null)
				query = query.Where(a => a.ProcessId == processId.Value);
			if (outputOnly)
				query = query.Where(a => a.IsOutputLine);
			if (eventKinds is not null && eventKinds.Length > 0) {
				var kinds = new HashSet<string>(eventKinds.Where(a => !string.IsNullOrWhiteSpace(a)), StringComparer.OrdinalIgnoreCase);
				query = query.Where(a => kinds.Contains(a.Kind));
			}
			return query.OrderBy(a => a.Sequence).FirstOrDefault();
		}

		void DbgManager_MessageUserMessage(object? sender, DbgMessageUserMessageEventArgs e) =>
			RecordDebugEvent("user-message", $"Debugger user message [{e.MessageKind}]: {e.Message}", isOutputLine: false, severity: "error");

		void DbgManager_DbgManagerMessage(object? sender, DbgManagerMessageEventArgs e) {
			if (string.Equals(e.MessageKind, PredefinedDbgManagerMessageKinds.ErrorUser, StringComparison.OrdinalIgnoreCase))
				RecordDebugEvent("manager-message", $"Debugger manager message [{e.MessageKind}]: {e.Message}", isOutputLine: false, severity: "error");
			else
				RecordDebugEvent("manager-message", $"Debugger manager message [{e.MessageKind}]: {e.Message}", isOutputLine: false, severity: "info");
		}

		void DbgManager_MessageProcessCreated(object? sender, DbgMessageProcessCreatedEventArgs e) =>
			RecordDebugEvent("process-created", $"The program '[0x{e.Process.Id:X}] {e.Process.Name}' has started.", process: e.Process, isOutputLine: true);

		void DbgManager_MessageProcessExited(object? sender, DbgMessageProcessExitedEventArgs e) =>
			RecordDebugEvent("process-exited", $"The program '[0x{e.Process.Id:X}] {e.Process.Name}' has exited with code {e.ExitCode} (0x{e.ExitCode:X}).", process: e.Process, isOutputLine: true);

		void DbgManager_MessageModuleLoaded(object? sender, DbgMessageModuleLoadedEventArgs e) =>
			RecordDebugEvent("module-loaded", $"{e.Module.Process.Name} ({e.Module.Runtime.Name}: {e.Module.AppDomain?.Name ?? "DefaultDomain"}): Loaded '{e.Module.Filename}'.", process: e.Module.Process, runtime: e.Module.Runtime, module: e.Module, isOutputLine: true);

		void DbgManager_MessageExceptionThrown(object? sender, DbgMessageExceptionThrownEventArgs e) =>
			RecordDebugEvent("exception-thrown", $"Exception thrown: {e.Exception.Id} {e.Exception.Message}".Trim(), process: e.Exception.Process, runtime: e.Exception.Runtime, thread: e.Exception.Thread, module: e.Exception.Module, isOutputLine: false, severity: e.Exception.IsUnhandled ? "error" : "warning");

		void DbgManager_MessageEntryPointBreak(object? sender, DbgMessageEntryPointBreakEventArgs e) =>
			RecordDebugEvent("entry-point-break", "Entry point reached.", process: e.Runtime.Process, runtime: e.Runtime, thread: e.Thread, isOutputLine: false);

		void DbgManager_MessageStepComplete(object? sender, DbgMessageStepCompleteEventArgs e) =>
			RecordDebugEvent("step-complete", e.HasError ? $"Step completed with error: {e.Error}" : "Step completed.", process: e.Thread.Process, runtime: e.Thread.Runtime, thread: e.Thread, isOutputLine: false, severity: e.HasError ? "error" : "info");

		void DbgManager_MessageProgramMessage(object? sender, DbgMessageProgramMessageEventArgs e) =>
			RecordDebugEvent("program-message", e.Message, process: e.Runtime.Process, runtime: e.Runtime, thread: e.Thread, isOutputLine: true);

		void DbgManager_MessageAsyncProgramMessage(object? sender, DbgMessageAsyncProgramMessageEventArgs e) =>
			RecordDebugEvent($"async-program-message/{e.Source}", e.Message, process: e.Runtime.Process, runtime: e.Runtime, isOutputLine: true);

		void DbgManager_MessageBoundBreakpoint(object? sender, DbgMessageBoundBreakpointEventArgs e) =>
			RecordDebugEvent("breakpoint-hit", $"Breakpoint hit: {e.BoundBreakpoint.Breakpoint.Id}", process: e.BoundBreakpoint.Process, runtime: e.BoundBreakpoint.Runtime, thread: e.Thread, module: e.BoundBreakpoint.Module, isOutputLine: false);

		void RecordDebugEvent(string kind, string message, DbgProcess? process = null, DbgRuntime? runtime = null, DbgThread? thread = null, DbgModule? module = null, bool isOutputLine = false, string severity = "info") {
			var entry = new McpDebugEventEntry(
				Interlocked.Increment(ref nextDebugEventSequence),
				DateTimeOffset.UtcNow,
				kind,
				severity,
				message,
				isOutputLine,
				process?.Id,
				process?.Name,
				process?.Filename,
				runtime?.Name,
				thread?.Id,
				module?.Name,
				module?.Filename,
				module?.AppDomain?.Name);

			lock (debugEventLockObj) {
				debugEvents.Add(entry);
				const int maxEvents = 1000;
				if (debugEvents.Count > maxEvents)
					debugEvents.RemoveRange(0, debugEvents.Count - maxEvents);
				Monitor.PulseAll(debugEventLockObj);
			}

			if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
				logger.WriteError($"Debugger event [{kind}]: {message}");
			else
				logger.WriteLine($"Debugger event [{kind}]: {message}");
		}

		async Task StartAsync() {
			CancellationTokenSource? cancellationTokenSource = null;
			lock (lockObj) {
				if (isDisposed || state != McpServerState.Stopped)
					return;
				state = McpServerState.Starting;
				cancellationTokenSource = new CancellationTokenSource();
				serverCancellationTokenSource = cancellationTokenSource;
			}

			OnStateUpdated($"Starting MCP server on {EndpointUrl}...");
			logger.WriteLine($"Starting MCP server at {EndpointUrl}");

			WebApplication? app = null;
			try {
				app = CreateWebApplication();
				lock (lockObj) {
					if (isDisposed || !ReferenceEquals(serverCancellationTokenSource, cancellationTokenSource))
						throw new OperationCanceledException();
					webApplication = app;
				}

				await Task.Run(async () => {
					await app.StartAsync(cancellationTokenSource!.Token).ConfigureAwait(false);
				}).ConfigureAwait(false);

				bool shouldStopImmediately;
				lock (lockObj) {
					shouldStopImmediately = isDisposed || !ReferenceEquals(webApplication, app) || !ReferenceEquals(serverCancellationTokenSource, cancellationTokenSource) || cancellationTokenSource!.IsCancellationRequested;
					if (!shouldStopImmediately)
						state = McpServerState.Running;
				}

				if (shouldStopImmediately) {
					try {
						await app.StopAsync().ConfigureAwait(false);
					}
					catch {
					}
					await app.DisposeAsync().ConfigureAwait(false);
					cancellationTokenSource!.Dispose();
					return;
				}

				logger.WriteLine($"MCP server started at {EndpointUrl}");
				OnStateUpdated($"MCP server started at {EndpointUrl}", closeStatusBarAfterDelay: true);
			}
			catch (OperationCanceledException) when ((cancellationTokenSource?.IsCancellationRequested ?? false) || isDisposed) {
				lock (lockObj) {
					state = McpServerState.Stopped;
					if (ReferenceEquals(webApplication, app))
						webApplication = null;
					if (ReferenceEquals(serverCancellationTokenSource, cancellationTokenSource))
						serverCancellationTokenSource = null;
				}

				if (app is not null) {
					try {
						await app.DisposeAsync().ConfigureAwait(false);
					}
					catch {
					}
				}
				cancellationTokenSource?.Dispose();
			}
			catch (Exception ex) {
				lock (lockObj) {
					state = McpServerState.Stopped;
					if (ReferenceEquals(webApplication, app))
						webApplication = null;
					if (ReferenceEquals(serverCancellationTokenSource, cancellationTokenSource))
						serverCancellationTokenSource = null;
				}

				if (app is not null) {
					try {
						try {
							await app.StopAsync().ConfigureAwait(false);
						}
						catch {
						}
						await app.DisposeAsync().ConfigureAwait(false);
					}
					catch {
					}
				}
				cancellationTokenSource?.Dispose();

				logger.WriteException(ex);
				OnStateUpdated("MCP server failed to start", closeStatusBarAfterDelay: true);
			}
		}

		async Task StopAsync() {
			WebApplication? app;
			CancellationTokenSource? cancellationTokenSource;
			lock (lockObj) {
				if ((state != McpServerState.Running && state != McpServerState.Starting) || webApplication is null)
					return;
				state = McpServerState.Stopping;
				app = webApplication;
				cancellationTokenSource = serverCancellationTokenSource;
				webApplication = null;
				serverCancellationTokenSource = null;
			}

			OnStateUpdated("Stopping MCP server...");
			logger.WriteLine("Stopping MCP server");

			try {
				cancellationTokenSource?.Cancel();
				await Task.Run(async () => {
					try {
						using var stopCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
						await app.StopAsync(stopCancellationTokenSource.Token).ConfigureAwait(false);
					}
					catch {
					}
					await app.DisposeAsync().ConfigureAwait(false);
				}).ConfigureAwait(false);

				lock (lockObj)
					state = McpServerState.Stopped;

				logger.WriteLine("MCP server stopped");
				OnStateUpdated("MCP server stopped", closeStatusBarAfterDelay: true);
			}
			catch (Exception ex) {
				lock (lockObj)
					state = McpServerState.Stopped;

				logger.WriteException(ex);
				OnStateUpdated("MCP server stop failed", closeStatusBarAfterDelay: true);
			}
			finally {
				cancellationTokenSource?.Dispose();
			}
		}

		string GetEndpointUrl() => $"http://{FormatListenAddressForUrl(NormalizeListenAddress(mcpSettings.ListenAddress))}:{NormalizePort(mcpSettings.Port)}{NormalizeRoutePath(mcpSettings.RoutePath)}";

		static string FormatListenAddressForUrl(string listenAddress) =>
			listenAddress.Contains(":", StringComparison.Ordinal) && !listenAddress.StartsWith("[", StringComparison.Ordinal) ? $"[{listenAddress}]" : listenAddress;

		void ShowOutput() => RunOnUI(() => {
			toolWindowService.Show(OutputToolWindowGuid);
			logger.Select();
		});

		void OnStateUpdated(string statusMessage, bool closeStatusBarAfterDelay = false) {
			int version;
			RunOnUI(() => {
				appWindow.RefreshToolBar();
				if (!statusBarOpen) {
					appWindow.StatusBar.Open();
					statusBarOpen = true;
				}
				appWindow.StatusBar.Show(statusMessage);
			});

			if (!closeStatusBarAfterDelay)
				return;

			lock (lockObj)
				version = ++statusBarVersion;

			_ = Task.Run(async () => {
				await Task.Delay(1500).ConfigureAwait(false);
				RunOnUI(() => {
					lock (lockObj) {
						if (version != statusBarVersion || !statusBarOpen)
							return;
						appWindow.StatusBar.Close();
						statusBarOpen = false;
					}
				});
			});
		}

		void RunOnUI(Action callback) {
			if (isShuttingDown || appWindow.MainWindow.Dispatcher.HasShutdownStarted || appWindow.MainWindow.Dispatcher.HasShutdownFinished)
				return;
			if (appWindow.MainWindow.Dispatcher.CheckAccess())
				callback();
			else
				appWindow.MainWindow.Dispatcher.BeginInvoke(callback);
		}

		public T RunOnUISync<T>(Func<T> callback) {
			if (isShuttingDown || appWindow.MainWindow.Dispatcher.HasShutdownStarted || appWindow.MainWindow.Dispatcher.HasShutdownFinished)
				throw new InvalidOperationException("dnSpy UI is shutting down.");
			if (appWindow.MainWindow.Dispatcher.CheckAccess())
				return callback();
			return appWindow.MainWindow.Dispatcher.Invoke(callback);
		}

		public void RunOnUISync(Action callback) {
			if (isShuttingDown || appWindow.MainWindow.Dispatcher.HasShutdownStarted || appWindow.MainWindow.Dispatcher.HasShutdownFinished)
				throw new InvalidOperationException("dnSpy UI is shutting down.");
			if (appWindow.MainWindow.Dispatcher.CheckAccess())
				callback();
			else
				appWindow.MainWindow.Dispatcher.Invoke(callback);
		}

		WebApplication CreateWebApplication() {
			var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
				Args = Array.Empty<string>(),
			});

			builder.WebHost.UseUrls($"http://{FormatListenAddressForUrl(NormalizeListenAddress(mcpSettings.ListenAddress))}:{NormalizePort(mcpSettings.Port)}");
			builder.Logging.ClearProviders();

			builder.Services.AddSingleton(mcpSettings);
			builder.Services.AddSingleton(dbgManager);
			builder.Services.AddSingleton(dbgCodeBreakpointsService);
			builder.Services.AddSingleton(dbgCodeBreakpointHitCountService);
			builder.Services.AddSingleton(dbgCallStackService);
			builder.Services.AddSingleton(attachableProcessesService);
			builder.Services.AddSingleton(dbgLanguageService);
			builder.Services.AddSingleton(dbgDotNetBreakpointFactory);
			builder.Services.AddSingleton(dbgExceptionSettingsService);
			builder.Services.AddSingleton(documentService);
			builder.Services.AddSingleton(documentTreeView);
			builder.Services.AddSingleton(documentTabService);
			builder.Services.AddSingleton(decompilerService);
			builder.Services.AddSingleton<IEnumerable<ILanguageCompilerProvider>>(languageCompilerProviders);
			builder.Services.AddSingleton(settingsService);
			builder.Services.AddSingleton(serviceLocator);
			builder.Services.AddSingleton(logger);
			builder.Services.AddSingleton(this);
			var mcpServerBuilder = builder.Services.AddMcpServer(options => {
				options.ServerInfo = new() {
					Name = ServerName,
					Version = ServerVersion,
				};
			})
			.WithHttpTransport();
			mcpServerBuilder.WithTools(CreateEnabledTools());

			var app = builder.Build();
			app.MapMcp(NormalizeRoutePath(mcpSettings.RoutePath));
			return app;
		}

		IEnumerable<McpServerTool> CreateEnabledTools() =>
			typeof(DnSpyMcpTools)
				.GetMethods(BindingFlags.Instance | BindingFlags.Public)
				.Where(method => {
					var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
					if (toolAttr is null)
						return false;
					var name = string.IsNullOrWhiteSpace(toolAttr.Name) ? method.Name : toolAttr.Name;
					return mcpSettings.IsToolEnabled(name);
				})
				.Select(method => McpServerTool.Create(method, request => new DnSpyMcpTools(
					this,
					logger,
					dbgManager,
					dbgCodeBreakpointsService,
					dbgCodeBreakpointHitCountService,
					dbgCallStackService,
					attachableProcessesService,
					dbgLanguageService,
					dbgDotNetBreakpointFactory,
					dbgExceptionSettingsService,
					documentService,
					documentTreeView,
					documentTabService,
					decompilerService,
					languageCompilerProviders,
					settingsService,
					serviceLocator)));
	}

	public sealed record McpDebugEventEntry(long Sequence, DateTimeOffset TimestampUtc, string Kind, string Severity, string Message, bool IsOutputLine, int? ProcessId, string? ProcessName, string? ProcessFilename, string? RuntimeName, ulong? ThreadId, string? ModuleName, string? ModuleFilename, string? AppDomainName);
}
