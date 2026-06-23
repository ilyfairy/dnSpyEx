using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;

namespace dnSpy.Mcp {
	[Export(typeof(McpOutputLogger))]
	public sealed class McpOutputLogger {
		static readonly Guid OUTPUT_GUID = new Guid("55A73DD4-3007-4B0E-B581-95D55E7E0C87");

		readonly IOutputService outputService;
		readonly Dispatcher dispatcher;
		readonly object lockObj;
		readonly List<McpLogEntry> entries;
		IOutputTextPane? textPane;

		[ImportingConstructor]
		McpOutputLogger(IOutputService outputService, IAppWindow appWindow) {
			this.outputService = outputService;
			dispatcher = appWindow.MainWindow.Dispatcher;
			lockObj = new object();
			entries = new List<McpLogEntry>();
		}

		void UI(Action callback) {
			if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
				return;
			if (dispatcher.CheckAccess())
				callback();
			else
				dispatcher.BeginInvoke(DispatcherPriority.Send, callback);
		}

		void EnsureInitialized_UI() {
			if (textPane is null)
				textPane = outputService.Create(OUTPUT_GUID, "MCP");
		}

		public void Select() => UI(() => {
			EnsureInitialized_UI();
			outputService.Select(OUTPUT_GUID);
		});

		public void WriteLine(string message) {
			AddEntry("info", message);
			UI(() => {
			EnsureInitialized_UI();
			textPane!.WriteLine(TextColor.Text, $"[{DateTime.Now:HH:mm:ss}] {message}");
			});
		}

		public void WriteError(string message) {
			AddEntry("error", message);
			UI(() => {
			EnsureInitialized_UI();
			outputService.Select(OUTPUT_GUID);
			textPane!.WriteLine(TextColor.Error, $"[{DateTime.Now:HH:mm:ss}] {message}");
			});
		}

		public void WriteException(Exception ex) => WriteError(ex.ToString());

		void AddEntry(string level, string message) {
			lock (lockObj) {
				entries.Add(new McpLogEntry(DateTimeOffset.UtcNow, level, message));
				const int maxEntries = 500;
				if (entries.Count > maxEntries)
					entries.RemoveRange(0, entries.Count - maxEntries);
			}
		}

		public McpLogEntry[] GetEntries(string? level = null, int maxResults = 200) {
			lock (lockObj) {
				IEnumerable<McpLogEntry> query = entries;
				if (!string.IsNullOrWhiteSpace(level))
					query = query.Where(a => string.Equals(a.Level, level, StringComparison.OrdinalIgnoreCase));
				return query.TakeLast(Math.Max(1, maxResults)).ToArray();
			}
		}
	}

	public sealed record McpLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);
}
