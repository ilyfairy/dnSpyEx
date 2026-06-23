using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;

namespace dnSpy.Mcp {
	public enum McpToolGroup {
		General,
		Edit,
		Debug,
	}

	public sealed record McpToolInfo(string Name, string Description, McpToolGroup Group, bool EnabledByDefault);

	static class McpToolCatalog {
		static readonly StringComparer comparer = StringComparer.OrdinalIgnoreCase;
		static readonly Lazy<McpToolInfo[]> allTools = new Lazy<McpToolInfo[]>(CreateTools);
		static readonly HashSet<string> debugTools = new HashSet<string>(new[] {
			"list_attachable_processes",
			"attach_to_process",
			"set_current_thread",
			"evaluate_expression",
			"evaluate_debug_expression",
			"get_entry_point_info",
			"resolve_method_for_breakpoint",
			"debug_session_status",
			"get_debug_session_status",
			"list_debug_processes",
			"list_debugger_processes",
			"list_debug_modules",
			"get_default_debug_environment",
			"clear_debug_events",
			"get_recent_debug_events",
			"get_recent_debugger_events",
			"get_debug_output",
			"get_debugger_output",
			"wait_for_debug_event",
			"wait_for_debug_output",
			"start_debugging",
			"list_threads",
			"list_debug_threads",
			"get_call_stack",
			"get_debug_call_stack",
			"set_active_call_stack_frame",
			"break_all",
			"pause_debugged_processes",
			"step_into",
			"step_debug_thread_into",
			"step_over",
			"step_debug_thread_over",
			"step_out",
			"step_debug_thread_out",
			"run_all",
			"continue_debugged_processes",
			"stop_debugging",
			"stop_debug_session",
			"list_breakpoints",
			"set_method_breakpoint",
			"set_entry_point_breakpoint",
			"update_breakpoint",
			"remove_breakpoint",
			"clear_breakpoints",
		}, comparer);
		static readonly HashSet<string> editTools = new HashSet<string>(new[] {
			"save_assembly_to_file",
			"edit_module",
			"update_module_settings",
			"remove_module_custom_attribute",
			"add_module_custom_attribute",
			"add_type_from_csharp",
			"delete_type",
			"get_method_il_body",
			"apply_method_il_patch",
			"edit_assembly_basic_info",
			"update_assembly_settings",
			"remove_assembly_custom_attribute",
			"add_assembly_custom_attribute",
			"replace_assembly_security_declarations",
			"apply_assembly_attributes_csharp",
		}, comparer);

		public static IReadOnlyList<McpToolInfo> AllTools => allTools.Value;

		public static IReadOnlyCollection<string> GetDefaultEnabledToolNames() =>
			AllTools.Where(a => a.EnabledByDefault).Select(a => a.Name).ToArray();

		public static McpToolInfo? TryGet(string toolName) =>
			AllTools.FirstOrDefault(a => comparer.Equals(a.Name, toolName));

		static McpToolInfo[] CreateTools() => typeof(DnSpyMcpTools)
			.GetMethods(BindingFlags.Instance | BindingFlags.Public)
			.Select(method => {
				var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
				if (toolAttr is null)
					return null;
				var name = string.IsNullOrWhiteSpace(toolAttr.Name) ? method.Name : toolAttr.Name;
				var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
				var group = GetGroup(name);
				return new McpToolInfo(name, description, group, group == McpToolGroup.General);
			})
			.Where(a => a is not null)
			.Cast<McpToolInfo>()
			.OrderBy(a => a.Group)
			.ThenBy(a => a.Name, comparer)
			.ToArray();

		static McpToolGroup GetGroup(string toolName) {
			if (debugTools.Contains(toolName))
				return McpToolGroup.Debug;
			if (editTools.Contains(toolName))
				return McpToolGroup.Edit;
			return McpToolGroup.General;
		}
	}
}