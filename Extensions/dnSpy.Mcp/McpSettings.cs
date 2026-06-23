using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Mcp {
	public class McpSettings : ViewModelBase {
		public string ListenAddress {
			get => listenAddress;
			set {
				value ??= string.Empty;
				if (listenAddress != value) {
					listenAddress = value;
					OnPropertyChanged(nameof(ListenAddress));
				}
			}
		}
		string listenAddress = "127.0.0.1";

		public int Port {
			get => port;
			set {
				if (port != value) {
					port = value;
					OnPropertyChanged(nameof(Port));
				}
			}
		}
		int port = 38888;

		public string RoutePath {
			get => routePath;
			set {
				value ??= string.Empty;
				if (routePath != value) {
					routePath = value;
					OnPropertyChanged(nameof(RoutePath));
				}
			}
		}
		string routePath = "/mcp";

		public string EnabledToolNamesText {
			get => enabledToolNamesText;
			set {
				value ??= string.Empty;
				if (enabledToolNamesText != value) {
					enabledToolNamesText = value;
					OnPropertyChanged(nameof(EnabledToolNamesText));
				}
			}
		}
		string enabledToolNamesText = string.Join(";", McpToolCatalog.GetDefaultEnabledToolNames());

		public HashSet<string> GetEnabledToolNames() => new HashSet<string>(
			EnabledToolNamesText
				.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(a => a.Trim())
				.Where(a => !string.IsNullOrWhiteSpace(a)),
				StringComparer.OrdinalIgnoreCase);

		public void SetEnabledToolNames(IEnumerable<string> toolNames) =>
			EnabledToolNamesText = string.Join(";", toolNames.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

		public bool IsToolEnabled(string toolName) {
			var tool = McpToolCatalog.TryGet(toolName);
			if (tool is null)
				return true;
			return GetEnabledToolNames().Contains(tool.Name);
		}

		public McpSettings Clone() => CopyTo(new McpSettings());

		public McpSettings CopyTo(McpSettings other) {
			other.ListenAddress = ListenAddress;
			other.Port = Port;
			other.RoutePath = RoutePath;
			other.EnabledToolNamesText = EnabledToolNamesText;
			return other;
		}
	}

	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("814B96D6-8E1B-4F16-B37D-877BE6CC6D1F");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		McpSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var section = settingsService.GetOrCreateSection(SETTINGS_GUID);
			ListenAddress = section.Attribute<string>(nameof(ListenAddress)) ?? ListenAddress;
			Port = section.Attribute<int?>(nameof(Port)) ?? Port;
			RoutePath = section.Attribute<string>(nameof(RoutePath)) ?? RoutePath;
			EnabledToolNamesText = section.Attribute<string>(nameof(EnabledToolNamesText)) ?? EnabledToolNamesText;
			PropertyChanged += McpSettingsImpl_PropertyChanged;
		}

		void McpSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			var section = settingsService.RecreateSection(SETTINGS_GUID);
			section.Attribute(nameof(ListenAddress), ListenAddress);
			section.Attribute(nameof(Port), Port);
			section.Attribute(nameof(RoutePath), RoutePath);
			section.Attribute(nameof(EnabledToolNamesText), EnabledToolNamesText);
		}
	}
}
