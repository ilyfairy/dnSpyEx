using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace dnSpy.Mcp {
	public sealed record McpToolFailureSignal(string ToolName, Type ReturnType, string BooleanPropertyName);

	public static class McpToolResultErrorPolicy {
		static readonly StringComparer comparer = StringComparer.OrdinalIgnoreCase;
		static readonly McpToolFailureSignal[] failureSignals = new[] {
			Signal<BreakpointOperationResult>("close_assembly", nameof(BreakpointOperationResult.Success)),
			Signal<BreakpointOperationResult>("clear_assemblies", nameof(BreakpointOperationResult.Success)),
			Signal<SaveAssemblyResult>("save_assembly_to_file", nameof(SaveAssemblyResult.Saved)),
			Signal<ModuleEditResult>("edit_module", nameof(ModuleEditResult.Success)),
			Signal<ModuleEditResult>("remove_module_custom_attribute", nameof(ModuleEditResult.Success)),
			Signal<ModuleCustomAttributeEditResult>("add_module_custom_attribute", nameof(ModuleCustomAttributeEditResult.Success)),
			Signal<AddTypeFromCSharpResult>("add_type_from_csharp", nameof(AddTypeFromCSharpResult.Success)),
			Signal<DeleteTypeResult>("delete_type", nameof(DeleteTypeResult.Success)),
			Signal<MethodIlPatchResult>("apply_method_il_patch", nameof(MethodIlPatchResult.Success)),
			Signal<AssemblyEditResult>("edit_assembly_basic_info", nameof(AssemblyEditResult.Success)),
			Signal<AssemblyEditResult>("remove_assembly_custom_attribute", nameof(AssemblyEditResult.Success)),
			Signal<AssemblyCustomAttributeEditResult>("add_assembly_custom_attribute", nameof(AssemblyCustomAttributeEditResult.Success)),
			Signal<AssemblySecurityEditResult>("replace_assembly_security_declarations", nameof(AssemblySecurityEditResult.Success)),
			Signal<AssemblyCSharpEditResult>("apply_assembly_attributes_csharp", nameof(AssemblyCSharpEditResult.Success)),
			Signal<AttachProcessResult>("attach_to_process", nameof(AttachProcessResult.Attached)),
			Signal<EvaluateExpressionResult>("evaluate_expression", nameof(EvaluateExpressionResult.Succeeded)),
			Signal<MethodResolutionPreviewResult>("resolve_method_for_breakpoint", nameof(MethodResolutionPreviewResult.Resolved)),
			Signal<ResourceExportResult>("export_resource", nameof(ResourceExportResult.Exported)),
			Signal<StartDebuggingResult>("start_debugging", nameof(StartDebuggingResult.Started)),
			Signal<DebugContextSelectionResult>("set_current_thread", nameof(DebugContextSelectionResult.Success)),
			Signal<DebugContextSelectionResult>("set_active_call_stack_frame", nameof(DebugContextSelectionResult.Success)),
			Signal<BreakpointSetResult>("set_method_breakpoint", nameof(BreakpointSetResult.Created)),
			Signal<BreakpointSetResult>("set_entry_point_breakpoint", nameof(BreakpointSetResult.Created)),
			Signal<BreakpointUpdateResult>("update_breakpoint", nameof(BreakpointUpdateResult.Updated)),
			Signal<BreakpointOperationResult>("remove_breakpoint", nameof(BreakpointOperationResult.Success)),
			Signal<BreakpointOperationResult>("clear_breakpoints", nameof(BreakpointOperationResult.Success)),
			Signal<ExceptionSettingsUpdateResult>("set_all_exception_breaks", nameof(ExceptionSettingsUpdateResult.Success)),
			Signal<ExceptionSettingsUpdateResult>("set_exception_break_state", nameof(ExceptionSettingsUpdateResult.Success)),
			Signal<ExceptionSettingsUpdateResult>("reset_exception_settings", nameof(ExceptionSettingsUpdateResult.Success)),
		};
		static readonly IReadOnlyList<McpToolFailureSignal> readOnlyFailureSignals = Array.AsReadOnly(failureSignals);
		static readonly IReadOnlyDictionary<string, McpToolFailureSignal> signalsByToolName = failureSignals
			.ToDictionary(signal => signal.ToolName, comparer);

		public static IReadOnlyList<McpToolFailureSignal> FailureSignals => readOnlyFailureSignals;

		public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateCallToolFilter() =>
			next => async (request, cancellationToken) => {
				var result = await next(request, cancellationToken).ConfigureAwait(false);
				MarkErrorIfFailureSignalIsFalse(request.Params?.Name, result);
				return result;
			};

		public static void ValidateToolMethod(string toolName, MethodInfo method) {
			if (!signalsByToolName.TryGetValue(toolName, out var signal))
				return;
			var actualReturnType = UnwrapReturnType(method.ReturnType);
			if (actualReturnType != signal.ReturnType)
				throw new InvalidOperationException($"MCP failure policy for '{toolName}' expects return type '{signal.ReturnType.FullName}', but method '{method.Name}' returns '{actualReturnType.FullName}'.");
		}

		public static bool MarkErrorIfFailureSignalIsFalse(string? toolName, CallToolResult result) {
			if (result is null)
				throw new ArgumentNullException(nameof(result));
			if (result.IsError is true || string.IsNullOrWhiteSpace(toolName) || !signalsByToolName.TryGetValue(toolName, out var signal))
				return false;

			foreach (var payload in GetJsonPayloads(result)) {
				if (!TryReadFailureSignal(payload, signal, out var succeeded))
					continue;
				if (succeeded)
					return false;
				result.IsError = true;
				return true;
			}
			return false;
		}

		static IEnumerable<JsonElement> GetJsonPayloads(CallToolResult result) {
			if (result.StructuredContent is JsonElement structuredContent)
				yield return structuredContent;

			foreach (var content in result.Content.OfType<TextContentBlock>())
				if (TryParseJson(content.Text, out var payload))
					yield return payload;
		}

		static bool TryReadFailureSignal(JsonElement payload, McpToolFailureSignal signal, out bool succeeded) {
			succeeded = false;
			return payload.ValueKind == JsonValueKind.Object &&
				TryGetBooleanProperty(payload, signal.BooleanPropertyName, out succeeded);
		}

		static bool TryParseJson(string text, out JsonElement payload) {
			try {
				using var document = JsonDocument.Parse(text);
				payload = document.RootElement.Clone();
				return true;
			}
			catch (JsonException) {
				payload = default;
				return false;
			}
		}

		static bool TryGetBooleanProperty(JsonElement payload, string propertyName, out bool value) {
			foreach (var property in payload.EnumerateObject()) {
				if (!comparer.Equals(property.Name, propertyName))
					continue;
				if (property.Value.ValueKind == JsonValueKind.True) {
					value = true;
					return true;
				}
				if (property.Value.ValueKind == JsonValueKind.False) {
					value = false;
					return true;
				}
				break;
			}
			value = false;
			return false;
		}

		static Type UnwrapReturnType(Type returnType) {
			if (returnType.IsGenericType) {
				var genericType = returnType.GetGenericTypeDefinition();
				if (genericType == typeof(Task<>) || genericType == typeof(ValueTask<>))
					return returnType.GetGenericArguments()[0];
			}
			return returnType;
		}

		static McpToolFailureSignal Signal<TResult>(string toolName, string booleanPropertyName) {
			var property = typeof(TResult).GetProperty(booleanPropertyName, BindingFlags.Instance | BindingFlags.Public);
			if (property?.PropertyType != typeof(bool))
				throw new InvalidOperationException($"MCP failure policy property '{typeof(TResult).FullName}.{booleanPropertyName}' is not a public Boolean property.");
			return new McpToolFailureSignal(toolName, typeof(TResult), booleanPropertyName);
		}
	}
}
