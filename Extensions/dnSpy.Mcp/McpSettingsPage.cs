using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Mcp {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class McpAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly McpSettings mcpSettings;

		[ImportingConstructor]
		McpAppSettingsPageProvider(McpSettings mcpSettings) => this.mcpSettings = mcpSettings;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new McpAppSettingsPage(mcpSettings);
		}
	}

	sealed class McpAppSettingsPage : AppSettingsPage {
		static readonly Guid THE_GUID = new Guid("A207BD64-0352-4E2C-A0B4-1A68B6C32B31");

		readonly McpSettings globalSettings;
		readonly McpSettings newSettings;
		readonly Dictionary<string, CheckBox> toolCheckBoxes;
		TextBox? listenAddressTextBox;
		TextBox? portTextBox;
		TextBox? routeTextBox;
		FrameworkElement? uiObject;

		public override Guid ParentGuid => Guid.Empty;
		public override Guid Guid => THE_GUID;
		public override double Order => AppSettingsConstants.ORDER_DEBUGGER + 0.2;
		public override string Title => "MCP Server";

		public override object? UIObject {
			get {
				uiObject ??= CreateUI();
				return uiObject;
			}
		}

		public McpAppSettingsPage(McpSettings mcpSettings) {
			globalSettings = mcpSettings;
			newSettings = mcpSettings.Clone();
			toolCheckBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
		}

		public override void OnApply() {
			if (listenAddressTextBox is not null)
				newSettings.ListenAddress = McpServerController.NormalizeListenAddress(listenAddressTextBox.Text);
			else
				newSettings.ListenAddress = McpServerController.NormalizeListenAddress(newSettings.ListenAddress);

			if (portTextBox is not null && int.TryParse(portTextBox.Text, out var port))
				newSettings.Port = McpServerController.NormalizePort(port);
			else
				newSettings.Port = McpServerController.NormalizePort(newSettings.Port);

			if (routeTextBox is not null)
				newSettings.RoutePath = McpServerController.NormalizeRoutePath(routeTextBox.Text);
			else
				newSettings.RoutePath = McpServerController.NormalizeRoutePath(newSettings.RoutePath);

			if (toolCheckBoxes.Count != 0) {
				newSettings.SetEnabledToolNames(toolCheckBoxes.Where(a => a.Value.IsChecked == true).Select(a => a.Key));
			}

			newSettings.CopyTo(globalSettings);
		}

		FrameworkElement CreateUI() {
			listenAddressTextBox = new TextBox {
				Text = McpServerController.NormalizeListenAddress(newSettings.ListenAddress),
				MinWidth = 220,
			};
			portTextBox = new TextBox {
				Text = newSettings.Port.ToString(),
				MinWidth = 120,
			};
			routeTextBox = new TextBox {
				Text = McpServerController.NormalizeRoutePath(newSettings.RoutePath),
				MinWidth = 220,
			};

			var panel = new StackPanel {
				Margin = new Thickness(12),
			};

			panel.Children.Add(new TextBlock {
				Text = "Embedded MCP HTTP server settings.",
				Margin = new Thickness(0, 0, 0, 8),
				FontWeight = FontWeights.SemiBold,
			});
			panel.Children.Add(new TextBlock {
				Text = "The server is disabled by default. Debug and edit MCP tools are also disabled by default and must be enabled manually.",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 12),
			});

			panel.Children.Add(CreateRow("Listen address", listenAddressTextBox));
			panel.Children.Add(CreateRow("Port", portTextBox));
			panel.Children.Add(CreateRow("Route", routeTextBox));
			panel.Children.Add(CreateToolEnablementSection());
			panel.Children.Add(new TextBlock {
				Text = "Endpoint and tool enablement changes take effect after restarting the MCP server.",
				Margin = new Thickness(0, 12, 0, 0),
				TextWrapping = TextWrapping.Wrap,
			});

#if NETFRAMEWORK
			panel.Children.Add(new TextBlock {
				Text = "This feature requires the .NET dnSpyEx build. The .NET Framework build will show the button as unavailable.",
				Margin = new Thickness(0, 8, 0, 0),
				TextWrapping = TextWrapping.Wrap,
			});
#endif

			return panel;
		}

		FrameworkElement CreateToolEnablementSection() {
			toolCheckBoxes.Clear();
			var enabledTools = newSettings.GetEnabledToolNames();
			var outerPanel = new StackPanel {
				Margin = new Thickness(0, 12, 0, 0),
			};
			outerPanel.Children.Add(new TextBlock {
				Text = "MCP tool enablement",
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 6),
			});
			outerPanel.Children.Add(new TextBlock {
				Text = "Choose which tools are allowed to run. General read-only tools start enabled. Debug and edit tools start disabled.",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 8),
			});

			var buttons = new WrapPanel {
				Margin = new Thickness(0, 0, 0, 8),
			};
			buttons.Children.Add(CreateActionButton("Enable Debug MCP Tools", () => SetGroupChecked(McpToolGroup.Debug, true)));
			buttons.Children.Add(CreateActionButton("Enable Edit MCP Tools", () => SetGroupChecked(McpToolGroup.Edit, true)));
			buttons.Children.Add(CreateActionButton("Reset Defaults", ResetDefaults));
			outerPanel.Children.Add(buttons);

			var scrollViewer = new ScrollViewer {
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				MaxHeight = 420,
			};
			var toolsPanel = new StackPanel();
			foreach (var group in new[] { McpToolGroup.General, McpToolGroup.Edit, McpToolGroup.Debug }) {
				var groupTools = McpToolCatalog.AllTools.Where(a => a.Group == group).ToArray();
				if (groupTools.Length == 0)
					continue;
				toolsPanel.Children.Add(new TextBlock {
					Text = group switch {
						McpToolGroup.General => "General",
						McpToolGroup.Edit => "Edit",
						McpToolGroup.Debug => "Debug",
						_ => group.ToString(),
					},
					FontWeight = FontWeights.SemiBold,
					Margin = new Thickness(0, 6, 0, 4),
				});
				foreach (var tool in groupTools) {
					var checkBox = new CheckBox {
						Content = tool.Name,
						IsChecked = enabledTools.Contains(tool.Name),
						ToolTip = string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : tool.Description,
						Margin = new Thickness(0, 2, 0, 2),
					};
					toolCheckBoxes[tool.Name] = checkBox;
					toolsPanel.Children.Add(checkBox);
				}
			}
			scrollViewer.Content = toolsPanel;
			outerPanel.Children.Add(scrollViewer);
			return outerPanel;
		}

		Button CreateActionButton(string text, Action action) {
			var button = new Button {
				Content = text,
				Margin = new Thickness(0, 0, 8, 8),
				Padding = new Thickness(10, 4, 10, 4),
			};
			button.Click += (s, e) => action();
			return button;
		}

		void SetGroupChecked(McpToolGroup group, bool isChecked) {
			foreach (var tool in McpToolCatalog.AllTools.Where(a => a.Group == group)) {
				if (toolCheckBoxes.TryGetValue(tool.Name, out var checkBox))
					checkBox.IsChecked = isChecked;
			}
		}

		void ResetDefaults() {
			var defaults = new HashSet<string>(McpToolCatalog.GetDefaultEnabledToolNames(), StringComparer.OrdinalIgnoreCase);
			foreach (var entry in toolCheckBoxes)
				entry.Value.IsChecked = defaults.Contains(entry.Key);
		}

		static FrameworkElement CreateRow(string label, Control control) {
			var grid = new Grid {
				Margin = new Thickness(0, 4, 0, 4),
			};
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			var textBlock = new TextBlock {
				Text = label,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 8, 0),
			};
			Grid.SetColumn(textBlock, 0);
			Grid.SetColumn(control, 1);
			grid.Children.Add(textBlock);
			grid.Children.Add(control);
			return grid;
		}
	}
}
