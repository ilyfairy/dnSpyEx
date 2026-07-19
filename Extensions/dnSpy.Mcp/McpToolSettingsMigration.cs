using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Mcp {
	public static class McpToolSettingsMigration {
		public static string NormalizeEnabledToolNamesText(string? storedToolNamesText, bool isCurrentPolicyVersion) {
			var defaultToolNames = McpToolCatalog.GetDefaultEnabledToolNames();
			if (storedToolNamesText is null)
				return Serialize(defaultToolNames);

			var enabledToolNames = ParseCanonicalToolNames(storedToolNamesText);
			if (!isCurrentPolicyVersion)
				enabledToolNames.IntersectWith(defaultToolNames);
			return Serialize(enabledToolNames);
		}

		public static string CanonicalizeEnabledToolNames(IEnumerable<string> toolNames) {
			if (toolNames is null)
				throw new ArgumentNullException(nameof(toolNames));
			return Serialize(toolNames
				.Select(McpToolCatalog.TryGet)
				.Where(tool => tool is not null)
				.Select(tool => tool!.Name));
		}

		static HashSet<string> ParseCanonicalToolNames(string toolNamesText) => new HashSet<string>(
			toolNamesText
				.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(name => McpToolCatalog.TryGet(name.Trim()))
				.Where(tool => tool is not null)
				.Select(tool => tool!.Name),
			StringComparer.OrdinalIgnoreCase);

		static string Serialize(IEnumerable<string> toolNames) => string.Join(";", toolNames
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
	}
}
