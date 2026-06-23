using System.Collections.Generic;
using dnSpy.Contracts.Extension;

namespace dnSpy.Mcp {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly McpServerController mcpServerController;

		[System.ComponentModel.Composition.ImportingConstructor]
		TheExtension(McpServerController mcpServerController) => this.mcpServerController = mcpServerController;

		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "Embedded MCP HTTP server integration for dnSpyEx",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
			if (@event == ExtensionEvent.AppExit)
				mcpServerController.Dispose();
		}
	}
}