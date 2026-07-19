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

	public enum McpDocumentAccess {
		None,
		Read,
		Write,
	}

	public enum McpToolRisk {
		ReadOnly,
		WorkspaceMutation,
		AssemblyEdit,
		FileWrite,
		DebuggerRead,
		DebuggerControl,
		DebuggeeExecution,
	}

	public sealed record McpToolInfo(
		string Name,
		string Description,
		McpToolGroup Group,
		McpDocumentAccess DocumentAccess,
		bool EnabledByDefault,
		bool RequiresUIThread,
		McpToolRisk Risk);

	public sealed record McpToolCatalogValidationResult(
		IReadOnlyList<string> UncatalogedTools,
		IReadOnlyList<string> MissingImplementations,
		IReadOnlyList<string> DuplicateDiscoveredTools,
		IReadOnlyList<string> DuplicatePolicies) {
		public bool IsValid =>
			UncatalogedTools.Count == 0 &&
			MissingImplementations.Count == 0 &&
			DuplicateDiscoveredTools.Count == 0 &&
			DuplicatePolicies.Count == 0;

		public string GetErrorMessage() {
			if (IsValid)
				return "The MCP tool catalog matches the discovered tools.";

			var errors = new List<string>();
			AddError(errors, "Tools without an explicit policy", UncatalogedTools);
			AddError(errors, "Policies without an implementation", MissingImplementations);
			AddError(errors, "Duplicate discovered tool names", DuplicateDiscoveredTools);
			AddError(errors, "Duplicate policy names", DuplicatePolicies);
			return string.Join("; ", errors);
		}

		static void AddError(List<string> errors, string label, IReadOnlyList<string> names) {
			if (names.Count != 0)
				errors.Add($"{label}: {string.Join(", ", names)}");
		}
	}

	public static class McpToolCatalog {
		static readonly StringComparer comparer = StringComparer.OrdinalIgnoreCase;
		static readonly McpToolPolicy[] policies = new[] {
			// General tools are read-only and are the only tools enabled by default. Pure
			// document/dnlib reads run on request threads; UI-backed context stays on UI.
			Policy("list_loaded_assemblies", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_decompilers", McpToolGroup.General, McpDocumentAccess.None, true, true, McpToolRisk.ReadOnly),
			Policy("get_module_info", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_module_custom_attributes", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_method_il_body", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_assembly_settings", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_assembly_custom_attributes", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_types", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_methods", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_entry_point_info", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("resolve_method_for_breakpoint", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("search_symbols", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_metadata_summary", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_assembly_info", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_assembly_references", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("list_resources", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("get_pe_info", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("decompile_assembly", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("decompile_type", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("decompile_method", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_type_usages", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_callers", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_callees", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_field_reads", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_field_writes", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_property_reads", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_property_writes", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_base_types", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_derived_types", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_interface_implementations", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),
			Policy("find_interface_method_implementations", McpToolGroup.General, McpDocumentAccess.Read, true, false, McpToolRisk.ReadOnly),

			// Workspace, file, and assembly mutations require explicit opt-in.
			Policy("load_assembly", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.WorkspaceMutation),
			Policy("close_assembly", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.WorkspaceMutation),
			Policy("clear_assemblies", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.WorkspaceMutation),
			Policy("reload_assembly", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.WorkspaceMutation),
			Policy("save_assembly_to_file", McpToolGroup.Edit, McpDocumentAccess.Write, false, false, McpToolRisk.FileWrite),
			Policy("edit_module", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("remove_module_custom_attribute", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("add_module_custom_attribute", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("add_type_from_csharp", McpToolGroup.Edit, McpDocumentAccess.Write, false, false, McpToolRisk.AssemblyEdit),
			Policy("delete_type", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("apply_method_il_patch", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("edit_assembly_basic_info", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("remove_assembly_custom_attribute", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("add_assembly_custom_attribute", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("replace_assembly_security_declarations", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("apply_assembly_attributes_csharp", McpToolGroup.Edit, McpDocumentAccess.Write, false, true, McpToolRisk.AssemblyEdit),
			Policy("export_resource", McpToolGroup.Edit, McpDocumentAccess.Write, false, false, McpToolRisk.FileWrite),

			// Debugger observation and control are disabled until explicitly enabled.
			Policy("get_mcp_context", McpToolGroup.Debug, McpDocumentAccess.Read, false, true, McpToolRisk.DebuggerRead),
			Policy("list_attachable_processes", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("attach_to_process", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("evaluate_expression", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggeeExecution),
			Policy("debug_session_status", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("list_debug_processes", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("list_debug_modules", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("get_default_debug_environment", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("get_recent_log_messages", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("clear_debug_events", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("get_recent_debug_events", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("get_debug_output", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("wait_for_debug_event", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("wait_for_debug_output", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("start_debugging", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("list_threads", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("set_current_thread", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("get_call_stack", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("set_active_call_stack_frame", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("break_all", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("step_into", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("step_over", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("step_out", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("run_all", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("stop_debugging", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("list_breakpoints", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerRead),
			Policy("set_method_breakpoint", McpToolGroup.Debug, McpDocumentAccess.Read, false, true, McpToolRisk.DebuggerControl),
			Policy("set_entry_point_breakpoint", McpToolGroup.Debug, McpDocumentAccess.Read, false, true, McpToolRisk.DebuggerControl),
			Policy("update_breakpoint", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("remove_breakpoint", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("clear_breakpoints", McpToolGroup.Debug, McpDocumentAccess.None, false, true, McpToolRisk.DebuggerControl),
			Policy("list_exception_settings", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerRead),
			Policy("set_all_exception_breaks", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("set_exception_break_state", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
			Policy("reset_exception_settings", McpToolGroup.Debug, McpDocumentAccess.None, false, false, McpToolRisk.DebuggerControl),
		};
		static readonly Lazy<McpToolCatalogValidationResult> validation = new Lazy<McpToolCatalogValidationResult>(ValidateDiscoveredToolsCore);
		static readonly Lazy<McpToolInfo[]> allTools = new Lazy<McpToolInfo[]>(CreateTools);
		static readonly Lazy<IReadOnlyDictionary<string, McpToolInfo>> toolsByName = new Lazy<IReadOnlyDictionary<string, McpToolInfo>>(
			() => AllTools.ToDictionary(a => a.Name, comparer));

		public static IReadOnlyList<McpToolInfo> AllTools => allTools.Value;

		public static IReadOnlyCollection<string> GetDefaultEnabledToolNames() =>
			AllTools.Where(a => a.EnabledByDefault).Select(a => a.Name).ToArray();

		public static McpToolInfo? TryGet(string toolName) {
			if (string.IsNullOrWhiteSpace(toolName))
				return null;
			return toolsByName.Value.TryGetValue(toolName, out var tool) ? tool : null;
		}

		public static McpToolCatalogValidationResult ValidateDiscoveredTools() => validation.Value;

		public static McpToolCatalogValidationResult ValidateDiscoveredToolNames(IEnumerable<string> discoveredToolNames) {
			if (discoveredToolNames is null)
				throw new ArgumentNullException(nameof(discoveredToolNames));

			var discoveredNames = discoveredToolNames
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.Select(a => a.Trim())
				.ToArray();
			var policyNames = policies.Select(a => a.Name).ToArray();
			var duplicateDiscoveredTools = FindDuplicates(discoveredNames);
			var duplicatePolicies = FindDuplicates(policyNames);
			var discoveredSet = new HashSet<string>(discoveredNames, comparer);
			var policySet = new HashSet<string>(policyNames, comparer);

			return new McpToolCatalogValidationResult(
				discoveredSet.Except(policySet, comparer).OrderBy(a => a, comparer).ToArray(),
				policySet.Except(discoveredSet, comparer).OrderBy(a => a, comparer).ToArray(),
				duplicateDiscoveredTools,
				duplicatePolicies);
		}

		public static void EnsureCatalogMatchesDiscoveredTools() {
			var result = ValidateDiscoveredTools();
			if (!result.IsValid)
				throw new InvalidOperationException(result.GetErrorMessage());
		}

		static McpToolPolicy Policy(string name, McpToolGroup group, McpDocumentAccess documentAccess, bool enabledByDefault, bool requiresUIThread, McpToolRisk risk) =>
			new McpToolPolicy(name, group, documentAccess, enabledByDefault, requiresUIThread, risk);

		static McpToolInfo[] CreateTools() {
			EnsureCatalogMatchesDiscoveredTools();
			var descriptions = DiscoverTools()
				.GroupBy(a => a.Name, comparer)
				.ToDictionary(a => a.Key, a => a.First().Description, comparer);
			return policies
				.Select(policy => new McpToolInfo(
					policy.Name,
					descriptions.TryGetValue(policy.Name, out var description) ? description : string.Empty,
					policy.Group,
					policy.DocumentAccess,
					policy.EnabledByDefault,
					policy.RequiresUIThread,
					policy.Risk))
				.OrderBy(a => a.Group)
				.ThenBy(a => a.Name, comparer)
				.ToArray();
		}

		static McpToolCatalogValidationResult ValidateDiscoveredToolsCore() =>
			ValidateDiscoveredToolNames(DiscoverTools().Select(a => a.Name));

		static IReadOnlyList<string> FindDuplicates(IEnumerable<string> names) => names
			.GroupBy(a => a, comparer)
			.Where(a => a.Count() > 1)
			.Select(a => a.Key)
			.OrderBy(a => a, comparer)
			.ToArray();

		static IEnumerable<DiscoveredTool> DiscoverTools() => typeof(DnSpyMcpTools)
			.GetMethods(BindingFlags.Instance | BindingFlags.Public)
			.Select(method => {
				var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>();
				if (toolAttribute is null)
					return null;
				var name = string.IsNullOrWhiteSpace(toolAttribute.Name) ? method.Name : toolAttribute.Name;
				var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
				return new DiscoveredTool(name, description);
			})
			.Where(a => a is not null)
			.Cast<DiscoveredTool>();

		sealed record McpToolPolicy(string Name, McpToolGroup Group, McpDocumentAccess DocumentAccess, bool EnabledByDefault, bool RequiresUIThread, McpToolRisk Risk);
		sealed record DiscoveredTool(string Name, string Description);
	}
}
