using System;
using System.ComponentModel.Composition;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.ToolBars;

namespace dnSpy.Mcp {
	static class McpToolBarConstants {
		public const string GROUP_APP_TB_MAIN_MCP = "5500,FE9B01A0-6D1A-4A34-9E1B-3B41CF29C96C";
	}

	[ExportToolBarButton(OwnerGuid = ToolBarConstants.APP_TB_GUID, Icon = DsImagesAttribute.Run, ToolTip = "Toggle embedded MCP HTTP server", Group = McpToolBarConstants.GROUP_APP_TB_MAIN_MCP, Order = 0)]
	sealed class ToggleMcpServerToolBarCommand : ToolBarButtonBase {
		readonly McpServerController mcpServerController;
		readonly IAppWindow appWindow;

		[ImportingConstructor]
		ToggleMcpServerToolBarCommand(McpServerController mcpServerController, IAppWindow appWindow) {
			this.mcpServerController = mcpServerController;
			this.appWindow = appWindow;
		}

		public override void Execute(IToolBarItemContext context) {
			try {
				mcpServerController.ToggleAsync();
			}
			catch (Exception ex) {
				MsgBox.Instance.Show(ex);
			}
			finally {
				appWindow.RefreshToolBar();
			}
		}

		public override bool IsEnabled(IToolBarItemContext context) => mcpServerController.CanToggle;

		public override string? GetHeader(IToolBarItemContext context) => "MCP";

		public override ImageReference? GetIcon(IToolBarItemContext context) =>
			mcpServerController.State == McpServerState.Running || mcpServerController.State == McpServerState.Stopping ? DsImages.Stop : DsImages.Run;

		public override string? GetToolTip(IToolBarItemContext context) =>
			mcpServerController.GetToolTipText();
	}
}