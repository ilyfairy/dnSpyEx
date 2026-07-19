using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

const int ExpectedToolCount = 83;
const int ExpectedGeneralToolCount = 31;
const int ExpectedEditToolCount = 17;
const int ExpectedDebugToolCount = 35;

var aliases = new[] {
	"open_assembly",
	"get_module_settings",
	"update_module_settings",
	"update_assembly_settings",
	"evaluate_debug_expression",
	"get_assembly_summary",
	"find_method_usages",
	"get_method_uses",
	"get_debug_session_status",
	"list_debugger_processes",
	"get_recent_debugger_events",
	"get_debugger_output",
	"list_debug_threads",
	"get_debug_call_stack",
	"pause_debugged_processes",
	"step_debug_thread_into",
	"step_debug_thread_over",
	"step_debug_thread_out",
	"continue_debugged_processes",
	"stop_debug_session",
	"restore_default_exception_settings",
	"enable_all_exception_breaks",
	"disable_all_exception_breaks",
};

var tools = McpToolCatalog.AllTools;
AssertEqual(ExpectedToolCount, tools.Count, "canonical tool count");
AssertEqual(ExpectedToolCount, tools.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique policy count");
Assert(tools.All(a => !string.IsNullOrWhiteSpace(a.Description)), "Every canonical tool must have a reflected description.");
AssertEqual(ExpectedGeneralToolCount, tools.Count(a => a.Group == McpToolGroup.General), "General tool count");
AssertEqual(ExpectedEditToolCount, tools.Count(a => a.Group == McpToolGroup.Edit), "Edit tool count");
AssertEqual(ExpectedDebugToolCount, tools.Count(a => a.Group == McpToolGroup.Debug), "Debug tool count");

var validation = McpToolCatalog.ValidateDiscoveredTools();
Assert(validation.IsValid, validation.GetErrorMessage());

var exactNamesValidation = McpToolCatalog.ValidateDiscoveredToolNames(tools.Select(a => a.Name));
Assert(exactNamesValidation.IsValid, exactNamesValidation.GetErrorMessage());

var missingImplementationValidation = McpToolCatalog.ValidateDiscoveredToolNames(tools.Skip(1).Select(a => a.Name));
AssertEqual(1, missingImplementationValidation.MissingImplementations.Count, "missing implementation detection");
AssertEqual(tools[0].Name, missingImplementationValidation.MissingImplementations[0], "missing implementation name");

const string UnknownToolName = "__unknown_mcp_tool__";
var uncatalogedValidation = McpToolCatalog.ValidateDiscoveredToolNames(tools.Select(a => a.Name).Append(UnknownToolName));
AssertEqual(1, uncatalogedValidation.UncatalogedTools.Count, "uncataloged tool detection");
AssertEqual(UnknownToolName, uncatalogedValidation.UncatalogedTools[0], "uncataloged tool name");

var duplicateValidation = McpToolCatalog.ValidateDiscoveredToolNames(tools.Select(a => a.Name).Append(tools[0].Name.ToUpperInvariant()));
AssertEqual(1, duplicateValidation.DuplicateDiscoveredTools.Count, "case-insensitive duplicate detection");
AssertEqual(tools[0].Name, duplicateValidation.DuplicateDiscoveredTools[0], "duplicate tool name");

Assert(tools.Where(a => a.EnabledByDefault).All(a => a.Group == McpToolGroup.General && a.Risk == McpToolRisk.ReadOnly),
	"Only General read-only tools may be enabled by default.");
Assert(tools.Where(a => a.Group != McpToolGroup.General).All(a => !a.EnabledByDefault),
	"Edit and Debug tools must be disabled by default.");
Assert(tools.Where(a => a.Group == McpToolGroup.General).All(a => a.EnabledByDefault && a.Risk == McpToolRisk.ReadOnly),
	"Every General tool must be an enabled-by-default read-only operation.");

var writeDocumentTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
	"load_assembly",
	"close_assembly",
	"clear_assemblies",
	"reload_assembly",
	"save_assembly_to_file",
	"edit_module",
	"remove_module_custom_attribute",
	"add_module_custom_attribute",
	"add_type_from_csharp",
	"delete_type",
	"apply_method_il_patch",
	"edit_assembly_basic_info",
	"remove_assembly_custom_attribute",
	"add_assembly_custom_attribute",
	"replace_assembly_security_declarations",
	"apply_assembly_attributes_csharp",
	"export_resource",
};
var readDocumentTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
	"list_loaded_assemblies",
	"get_module_info",
	"list_module_custom_attributes",
	"get_method_il_body",
	"get_assembly_settings",
	"list_assembly_custom_attributes",
	"list_types",
	"list_methods",
	"get_entry_point_info",
	"resolve_method_for_breakpoint",
	"search_symbols",
	"get_metadata_summary",
	"get_assembly_info",
	"list_assembly_references",
	"list_resources",
	"get_pe_info",
	"decompile_assembly",
	"decompile_type",
	"decompile_method",
	"find_type_usages",
	"find_callers",
	"find_callees",
	"find_field_reads",
	"find_field_writes",
	"find_property_reads",
	"find_property_writes",
	"find_base_types",
	"find_derived_types",
	"find_interface_implementations",
	"find_interface_method_implementations",
	"get_mcp_context",
	"set_method_breakpoint",
	"set_entry_point_breakpoint",
};
var noDocumentAccessTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
	"list_decompilers",
	"list_attachable_processes",
	"attach_to_process",
	"evaluate_expression",
	"debug_session_status",
	"list_debug_processes",
	"list_debug_modules",
	"get_default_debug_environment",
	"get_recent_log_messages",
	"clear_debug_events",
	"get_recent_debug_events",
	"get_debug_output",
	"wait_for_debug_event",
	"wait_for_debug_output",
	"start_debugging",
	"list_threads",
	"set_current_thread",
	"get_call_stack",
	"set_active_call_stack_frame",
	"break_all",
	"step_into",
	"step_over",
	"step_out",
	"run_all",
	"stop_debugging",
	"list_breakpoints",
	"update_breakpoint",
	"remove_breakpoint",
	"clear_breakpoints",
	"list_exception_settings",
	"set_all_exception_breaks",
	"set_exception_break_state",
	"reset_exception_settings",
};
Assert(!writeDocumentTools.Overlaps(readDocumentTools), "Document Read and Write policies must not overlap.");
Assert(!writeDocumentTools.Overlaps(noDocumentAccessTools) && !readDocumentTools.Overlaps(noDocumentAccessTools),
	"Document None, Read, and Write policies must be disjoint.");
Assert(writeDocumentTools.Concat(readDocumentTools).Concat(noDocumentAccessTools).ToHashSet(StringComparer.OrdinalIgnoreCase)
	.SetEquals(tools.Select(tool => tool.Name)), "Every canonical tool must have one explicit document-access expectation.");
foreach (var tool in tools) {
	var expectedAccess = writeDocumentTools.Contains(tool.Name) ? McpDocumentAccess.Write :
		readDocumentTools.Contains(tool.Name) ? McpDocumentAccess.Read :
		noDocumentAccessTools.Contains(tool.Name) ? McpDocumentAccess.None : throw new InvalidOperationException($"Missing document-access expectation for {tool.Name}.");
	AssertEqual(expectedAccess, tool.DocumentAccess, $"document access for {tool.Name}");
}
AssertEqual(ExpectedEditToolCount, tools.Count(tool => tool.DocumentAccess == McpDocumentAccess.Write), "document Write tool count");
AssertEqual(33, tools.Count(tool => tool.DocumentAccess == McpDocumentAccess.Read), "document Read tool count");
AssertEqual(33, tools.Count(tool => tool.DocumentAccess == McpDocumentAccess.None), "document None tool count");

var backgroundHeavyTools = new[] {
	"search_symbols",
	"decompile_assembly",
	"decompile_type",
	"decompile_method",
	"find_type_usages",
	"find_callers",
	"find_callees",
	"find_field_reads",
	"find_field_writes",
	"find_property_reads",
	"find_property_writes",
	"find_derived_types",
	"find_interface_implementations",
	"find_interface_method_implementations",
};
Assert(backgroundHeavyTools.All(name => McpToolCatalog.TryGet(name)?.RequiresUIThread == false),
	"Pure decompilation and metadata scan tools must not occupy the UI thread.");
var backgroundDebuggerTools = new[] {
	"evaluate_expression",
	"get_call_stack",
	"set_current_thread",
	"set_active_call_stack_frame",
};
Assert(backgroundDebuggerTools.All(name => McpToolCatalog.TryGet(name)?.RequiresUIThread == false),
	"Debugger evaluation, stack walking, and coordinated selection waits must not occupy the UI thread.");
var asynchronousEditTools = new[] { "save_assembly_to_file", "add_type_from_csharp", "export_resource" };
Assert(asynchronousEditTools.All(name => McpToolCatalog.TryGet(name) is { RequiresUIThread: false, DocumentAccess: McpDocumentAccess.Write }),
	"Long-running edit/file tools must use the asynchronous document gate without whole-call UI affinity.");
var discoveredToolMethods = typeof(DnSpyMcpTools)
	.GetMethods(BindingFlags.Instance | BindingFlags.Public)
	.Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
	.Where(item => item.Attribute is not null)
	.ToDictionary(
		item => string.IsNullOrWhiteSpace(item.Attribute!.Name) ? item.Method.Name : item.Attribute.Name,
		item => item.Method,
		StringComparer.OrdinalIgnoreCase);
Assert(asynchronousEditTools.All(name => typeof(Task).IsAssignableFrom(discoveredToolMethods[name].ReturnType)),
	"Long-running edit/file tools must expose asynchronous tool methods, not only background policy metadata.");
Assert(McpToolCatalog.TryGet("attach_to_process") is { RequiresUIThread: false, DocumentAccess: McpDocumentAccess.None },
	"Process attachment must perform provider work off the UI thread without taking the document gate.");

var settings = new McpSettings();
Assert(settings.GetEnabledToolNames().SetEquals(tools.Where(a => a.EnabledByDefault).Select(a => a.Name)),
	"Fresh settings must enable exactly the catalog defaults.");
Assert(McpToolCatalog.TryGet(UnknownToolName) is null, "An unknown tool must not have a policy.");
Assert(!settings.IsToolEnabled(UnknownToolName), "An unknown tool must be denied.");
settings.EnabledToolNamesText = UnknownToolName;
Assert(!settings.IsToolEnabled(UnknownToolName), "An unknown tool must stay denied even when persisted as enabled.");
AssertEqual(0, settings.GetEnabledToolNames().Count, "unknown persisted tool filtering");

foreach (var alias in aliases) {
	Assert(McpToolCatalog.TryGet(alias) is null, $"Alias '{alias}' must not have a policy.");
	settings.EnabledToolNamesText = alias;
	Assert(!settings.IsToolEnabled(alias), $"Alias '{alias}' must stay denied even when persisted as enabled.");
	AssertEqual(0, settings.GetEnabledToolNames().Count, $"persisted alias filtering for {alias}");
}

settings.SetEnabledToolNames(aliases.Append(UnknownToolName));
AssertEqual(string.Empty, settings.EnabledToolNamesText, "unknown and alias setting canonicalization");

RunToolSettingsMigrationTests();
RunToolResultErrorPolicyTests();
McpDnlibSafetyTests.Run();
await RunTypedFailureFilterTransportTestAsync();

RunTargetResolutionTests();
RunHttpSecurityTests();
await RunDebugEventWaitTestsAsync();
await RunDebuggerMutationCoordinatorTestsAsync();

Console.WriteLine($"MCP tool policy validation passed: {tools.Count} canonical tools, " +
	$"{ExpectedGeneralToolCount} General, {ExpectedEditToolCount} Edit, {ExpectedDebugToolCount} Debug.");
Console.WriteLine("MCP permission migration, document access, and typed failure-result validation passed.");
Console.WriteLine("MCP target resolution, HTTP security, asynchronous debug waits, and debugger mutation coordination validation passed.");
Console.WriteLine("MCP dnlib IL operand and custom attribute safety validation passed.");

static void RunToolSettingsMigrationTests() {
	var defaults = McpToolCatalog.GetDefaultEnabledToolNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
	var expectedDefaultsText = string.Join(";", defaults);
	AssertEqual(expectedDefaultsText, McpToolSettingsMigration.NormalizeEnabledToolNamesText(null, isCurrentPolicyVersion: false),
		"missing legacy permission property migration");
	AssertEqual(expectedDefaultsText, McpToolSettingsMigration.NormalizeEnabledToolNamesText(null, isCurrentPolicyVersion: true),
		"missing current permission property migration");
	AssertEqual(string.Empty, McpToolSettingsMigration.NormalizeEnabledToolNamesText(string.Empty, isCurrentPolicyVersion: false),
		"empty legacy permission preservation");
	AssertEqual(string.Empty, McpToolSettingsMigration.NormalizeEnabledToolNamesText(string.Empty, isCurrentPolicyVersion: true),
		"empty current permission preservation");

	var legacySelection = "LOAD_ASSEMBLY;debug_session_status;LIST_LOADED_ASSEMBLIES;open_assembly;__unknown_mcp_tool__";
	AssertEqual("list_loaded_assemblies", McpToolSettingsMigration.NormalizeEnabledToolNamesText(legacySelection, isCurrentPolicyVersion: false),
		"fail-closed legacy permission intersection");
	AssertEqual(string.Empty, McpToolSettingsMigration.NormalizeEnabledToolNamesText("load_assembly;debug_session_status", isCurrentPolicyVersion: false),
		"legacy non-default permission removal");
	AssertEqual("debug_session_status;list_loaded_assemblies;load_assembly", McpToolSettingsMigration.NormalizeEnabledToolNamesText(legacySelection, isCurrentPolicyVersion: true),
		"current permission canonicalization");
}

static void RunToolResultErrorPolicyTests() {
	var sdkJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
	var expectedFailureSignalTools = new[] {
		"close_assembly",
		"clear_assemblies",
		"save_assembly_to_file",
		"edit_module",
		"remove_module_custom_attribute",
		"add_module_custom_attribute",
		"add_type_from_csharp",
		"delete_type",
		"apply_method_il_patch",
		"edit_assembly_basic_info",
		"remove_assembly_custom_attribute",
		"add_assembly_custom_attribute",
		"replace_assembly_security_declarations",
		"apply_assembly_attributes_csharp",
		"attach_to_process",
		"evaluate_expression",
		"resolve_method_for_breakpoint",
		"export_resource",
		"start_debugging",
		"set_current_thread",
		"set_active_call_stack_frame",
		"set_method_breakpoint",
		"set_entry_point_breakpoint",
		"update_breakpoint",
		"remove_breakpoint",
		"clear_breakpoints",
		"set_all_exception_breaks",
		"set_exception_break_state",
		"reset_exception_settings",
	};
	Assert(McpToolResultErrorPolicy.FailureSignals.Select(signal => signal.ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase)
		.SetEquals(expectedFailureSignalTools), "The typed failure-signal policy must contain exactly the reviewed tool set.");

	var toolMethods = typeof(DnSpyMcpTools)
		.GetMethods(BindingFlags.Instance | BindingFlags.Public)
		.Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
		.Where(item => item.Attribute is not null)
		.ToDictionary(
			item => string.IsNullOrWhiteSpace(item.Attribute!.Name) ? item.Method.Name : item.Attribute.Name,
			item => item.Method,
			StringComparer.OrdinalIgnoreCase);
	foreach (var signal in McpToolResultErrorPolicy.FailureSignals) {
		Assert(toolMethods.TryGetValue(signal.ToolName, out var method), $"Failure-signal tool '{signal.ToolName}' must exist.");
		McpToolResultErrorPolicy.ValidateToolMethod(signal.ToolName, method!);
		Assert(signal.ReturnType.GetProperty(signal.BooleanPropertyName)?.PropertyType == typeof(bool),
			$"Failure signal '{signal.ReturnType.Name}.{signal.BooleanPropertyName}' must be Boolean.");
	}

	var failedSavePayload = new SaveAssemblyResult(false, "Save failed.", null);
	var failedSaveText = JsonSerializer.Serialize(failedSavePayload, sdkJsonOptions);
	var failedSaveResult = CreateToolResult(failedSaveText);
	Assert(McpToolResultErrorPolicy.MarkErrorIfFailureSignalIsFalse("save_assembly_to_file", failedSaveResult),
		"Saved=false must be promoted to an MCP tool error.");
	Assert(failedSaveResult.IsError is true, "Saved=false must set CallToolResult.IsError=true.");
	AssertEqual(failedSaveText, ((TextContentBlock)failedSaveResult.Content.Single()).Text, "failure content preservation");

	var successfulSaveResult = CreateToolResult(JsonSerializer.Serialize(new SaveAssemblyResult(true, "Saved.", "out.dll"), sdkJsonOptions));
	Assert(!McpToolResultErrorPolicy.MarkErrorIfFailureSignalIsFalse("save_assembly_to_file", successfulSaveResult),
		"Saved=true must remain a successful MCP tool result.");
	Assert(successfulSaveResult.IsError is not true, "Saved=true must not set IsError.");

	var unknownToolResult = CreateToolResult(failedSaveText);
	Assert(!McpToolResultErrorPolicy.MarkErrorIfFailureSignalIsFalse("unknown_tool", unknownToolResult),
		"An unknown tool must not be classified from an incidental Boolean property.");
	Assert(unknownToolResult.IsError is not true, "An unknown tool payload must remain unchanged.");

	var noEntryPointResult = CreateToolResult(JsonSerializer.Serialize(new EntryPointInfoResult(false, "test.dll", null, null, "No entry point."), sdkJsonOptions));
	Assert(!McpToolResultErrorPolicy.MarkErrorIfFailureSignalIsFalse("get_entry_point_info", noEntryPointResult),
		"HasEntryPoint=false is a normal metadata state, not a tool error.");
	var timedOutResult = CreateToolResult(JsonSerializer.Serialize(new DebugEventWaitResult(true, null, 42), sdkJsonOptions));
	Assert(!McpToolResultErrorPolicy.MarkErrorIfFailureSignalIsFalse("wait_for_debug_event", timedOutResult),
		"TimedOut=true is a normal wait result, not a tool error.");
}

static CallToolResult CreateToolResult(string json) => new CallToolResult {
	Content = new[] { new TextContentBlock { Text = json } },
};

static async Task RunTypedFailureFilterTransportTestAsync() {
	using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
	Pipe clientToServerPipe = new Pipe();
	Pipe serverToClientPipe = new Pipe();
	var method = typeof(TypedFailureFilterTransportTools).GetMethod(nameof(TypedFailureFilterTransportTools.FailSave))!;
	McpToolResultErrorPolicy.ValidateToolMethod("save_assembly_to_file", method);
	var options = new McpServerOptions {
		ToolCollection = [McpServerTool.Create(method)],
	};
	options.Filters.Request.CallToolFilters.Add(McpToolResultErrorPolicy.CreateCallToolFilter());

	await using var server = McpServer.Create(
		new StreamServerTransport(clientToServerPipe.Reader.AsStream(), serverToClientPipe.Writer.AsStream()),
		options);
	var serverTask = server.RunAsync(timeoutSource.Token);
	try {
		await using var client = await McpClient.CreateAsync(
			new StreamClientTransport(clientToServerPipe.Writer.AsStream(), serverToClientPipe.Reader.AsStream()),
			cancellationToken: timeoutSource.Token);
		var result = await client.CallToolAsync("save_assembly_to_file", cancellationToken: timeoutSource.Token);
		Assert(result.IsError is true, "The registered CallTool filter must promote Saved=false to IsError=true over MCP transport.");
		var text = ((TextContentBlock)result.Content.Single()).Text;
		Assert(text.Contains("\"saved\":false", StringComparison.OrdinalIgnoreCase),
			"The registered CallTool filter must preserve the typed failure payload.");
	}
	finally {
		timeoutSource.Cancel();
		try {
			await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested) {
		}
	}
}

static void RunTargetResolutionTests() {
	var documents = new[] {
		new McpDocumentIdentity(0, @"C:\first\Shared.dll", "Shared", "Shared", "Shared, Version=1.0.0.0", "Shared.dll", "Shared.dll"),
		new McpDocumentIdentity(1, @"D:\second\Shared.dll", "Shared", "Shared", "Shared, Version=2.0.0.0", "Shared.dll", "Shared.dll"),
		new McpDocumentIdentity(2, @"C:\unique\Unique.dll", "Unique", "Unique", "Unique, Version=1.0.0.0", "Unique.dll", "Unique.dll"),
	};

	AssertEqual(1, McpTargetResolver.FindDocumentByExactPath(documents, @"D:\second\Shared.dll") ?? -1, "exact document path resolution");
	var currentDirectoryDocument = new[] {
		new McpDocumentIdentity(0, System.IO.Path.GetFullPath("Shared.dll"), "Shared", "Shared", "Shared, Version=1.0.0.0", "Shared.dll", "Shared.dll"),
	};
	Assert(McpTargetResolver.FindDocumentByExactPath(currentDirectoryDocument, "Shared.dll") is null,
		"A bare filename must not bypass document ambiguity checks by being normalized against the process working directory.");
	AssertEqual(2, McpTargetResolver.FindDocumentByIdentity(documents, "Unique, Version=1.0.0.0") ?? -1, "full assembly identity resolution");
	AssertThrows<InvalidOperationException>(() => McpTargetResolver.FindDocumentByIdentity(documents, "Shared.dll"), "ambiguous document filename");
	AssertThrows<InvalidOperationException>(() => McpTargetResolver.FindDocumentByIdentity(documents, "Shared"), "ambiguous document short name");
	Assert(McpTargetResolver.FindDocumentByIdentity(documents, @"E:\missing\Shared.dll") is null,
		"An explicit path must not fall back to a loaded document with the same filename.");

	var types = new[] {
		new McpTypeIdentity(0, "First.dll", "First.Namespace.Widget", "First.Namespace.Widget", "Widget"),
		new McpTypeIdentity(1, "Second.dll", "Second.Namespace.Widget", "Second.Namespace.Widget", "Widget"),
		new McpTypeIdentity(2, "First.dll", "First.Namespace.UniqueType", "First.Namespace.UniqueType", "UniqueType"),
	};

	AssertEqual(1, McpTargetResolver.FindType(types, "Second.Namespace.Widget") ?? -1, "exact full type name resolution");
	AssertEqual(2, McpTargetResolver.FindType(types, "UniqueType") ?? -1, "unique short type name resolution");
	AssertThrows<InvalidOperationException>(() => McpTargetResolver.FindType(types, "Widget"), "ambiguous short type name");
	Assert(McpTargetResolver.FindType(types, "MissingType") is null, "A missing type must not resolve to another target.");

	McpTargetResolver.ValidateCurrentDebugThreadProcess(null, null);
	McpTargetResolver.ValidateCurrentDebugThreadProcess(123, 123);
	AssertThrows<InvalidOperationException>(() => McpTargetResolver.ValidateCurrentDebugThreadProcess(123, null), "process-only thread selection without a current thread");
	AssertThrows<InvalidOperationException>(() => McpTargetResolver.ValidateCurrentDebugThreadProcess(123, 456), "process-only thread selection with a different current process");
}

static void RunHttpSecurityTests() {
	Assert(McpHttpSecurity.IsLoopbackListenAddress("localhost"), "localhost must be recognized as loopback.");
	Assert(McpHttpSecurity.IsLoopbackListenAddress("LOCALHOST."), "localhost with a trailing dot must be recognized as loopback.");
	Assert(McpHttpSecurity.IsLoopbackListenAddress("127.42.0.1"), "The IPv4 127/8 range must be recognized as loopback.");
	Assert(McpHttpSecurity.IsLoopbackListenAddress("[::1]"), "IPv6 loopback must be recognized.");
	Assert(McpHttpSecurity.IsLoopbackListenAddress("::ffff:127.0.0.1"), "IPv4-mapped IPv6 loopback must be recognized.");
	Assert(!McpHttpSecurity.IsLoopbackListenAddress("0.0.0.0"), "The IPv4 wildcard must not be treated as loopback.");
	Assert(!McpHttpSecurity.IsLoopbackListenAddress("::"), "The IPv6 wildcard must not be treated as loopback.");
	Assert(!McpHttpSecurity.IsLoopbackListenAddress("dnspy-host"), "An arbitrary hostname must not be assumed to be loopback.");

	Assert(McpHttpSecurity.IsListenAddressAllowed("127.0.0.1"), "A loopback listener must be allowed.");
	Assert(!McpHttpSecurity.IsListenAddressAllowed("0.0.0.0"), "A wildcard remote listener must be rejected even when authentication is configured.");
	Assert(!McpHttpSecurity.IsListenAddressAllowed("192.0.2.10"), "A non-loopback listener must be rejected because the embedded transport is HTTP-only.");
	Assert(McpHttpSecurity.HasValidBearerToken("Bearer secret", "secret"), "An exact bearer token must be accepted.");
	Assert(McpHttpSecurity.HasValidBearerToken("bearer secret", "secret"), "The bearer authentication scheme is case-insensitive.");
	Assert(!McpHttpSecurity.HasValidBearerToken("Bearer wrong", "secret"), "An incorrect bearer token must be rejected.");
	Assert(!McpHttpSecurity.HasValidBearerToken("Basic secret", "secret"), "A non-bearer authorization scheme must be rejected.");

	Assert(McpHttpSecurity.IsHostAllowed("localhost", 38888, "http", "127.0.0.1", 38888), "A loopback Host header with the configured port must be accepted.");
	Assert(McpHttpSecurity.IsHostAllowed("::1", 38888, "http", "localhost", 38888), "Equivalent IPv6 loopback Host headers must be accepted.");
	Assert(!McpHttpSecurity.IsHostAllowed("attacker.example", 38888, "http", "127.0.0.1", 38888), "A DNS-rebinding Host header must be rejected for a loopback listener.");
	Assert(!McpHttpSecurity.IsHostAllowed("localhost", 38889, "http", "127.0.0.1", 38888), "A Host header with the wrong port must be rejected.");
	Assert(!McpHttpSecurity.IsHostAllowed("localhost", null, "http", "127.0.0.1", 38888), "A missing non-default Host port must be rejected.");

	Assert(McpHttpSecurity.IsOriginAllowed(null, "localhost", 38888, "http", "127.0.0.1", 38888), "Non-browser clients without Origin must remain supported.");
	Assert(!McpHttpSecurity.IsOriginAllowed(string.Empty, "localhost", 38888, "http", "127.0.0.1", 38888), "An empty Origin header must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("  ", "localhost", 38888, "http", "127.0.0.1", 38888), "A whitespace Origin header must be rejected.");
	Assert(McpHttpSecurity.IsOriginAllowed("http://127.0.0.1:38888", "localhost", 38888, "http", "127.0.0.1", 38888), "A configured loopback Origin must be accepted.");
	Assert(McpHttpSecurity.IsOriginAllowed("http://[::1]:38888", "127.0.0.1", 38888, "http", "localhost", 38888), "Equivalent IPv6 loopback Origins must be accepted.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://attacker.example:38888", "localhost", 38888, "http", "127.0.0.1", 38888), "A DNS-rebinding Origin must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://localhost:38889", "localhost", 38888, "http", "127.0.0.1", 38888), "An Origin with the wrong port must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("https://localhost:38888", "localhost", 38888, "http", "127.0.0.1", 38888), "An Origin with the wrong scheme must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://localhost:38888/path", "localhost", 38888, "http", "127.0.0.1", 38888), "An Origin containing a path must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://localhost:38888/?query=1", "localhost", 38888, "http", "127.0.0.1", 38888), "An Origin containing a query must be rejected.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://localhost:38888,http://localhost:38888", "localhost", 38888, "http", "127.0.0.1", 38888), "Multiple Origin values must be rejected.");
	Assert(!McpHttpSecurity.IsHostAllowed("dnspy-host", 38888, "http", "0.0.0.0", 38888), "Host validation must fail closed for a disallowed remote listener.");
	Assert(!McpHttpSecurity.IsOriginAllowed("http://dnspy-host:38888", "dnspy-host", 38888, "http", "0.0.0.0", 38888), "Origin validation must fail closed for a disallowed remote listener.");
}

static async Task RunDebugEventWaitTestsAsync() {
	var timeoutBuffer = new McpDebugEventBuffer();
	AssertThrows<ArgumentOutOfRangeException>(() => timeoutBuffer.WaitForEventAsync(null, null, -2), "invalid negative debug wait timeout");
	var preexistingEvent = AddDebugEvent(timeoutBuffer, "preexisting-event");
	var stopwatch = Stopwatch.StartNew();
	var timeoutResult = await timeoutBuffer.WaitForEventAsync(null, null, 50).WaitAsync(TimeSpan.FromSeconds(2));
	stopwatch.Stop();
	Assert(timeoutResult.Event is null, "A debug event wait must not return an event on timeout.");
	AssertEqual(preexistingEvent.Sequence, timeoutResult.LatestSequence, "afterSequence=null timeout cursor");
	Assert(stopwatch.ElapsedMilliseconds >= 20, "A debug event wait must not report timeout immediately.");
	var futureCursorResult = await timeoutBuffer.WaitForEventAsync(null, preexistingEvent.Sequence + 100, 0);
	Assert(futureCursorResult.Event is null, "A zero-timeout wait with a future cursor must not return an event.");
	AssertEqual(preexistingEvent.Sequence + 100, futureCursorResult.LatestSequence, "non-regressing future debug event cursor");

	var requestCancellationBuffer = new McpDebugEventBuffer();
	using (var requestCancellationSource = new CancellationTokenSource()) {
		var waitTask = requestCancellationBuffer.WaitForEventAsync(null, null, Timeout.Infinite, cancellationToken: requestCancellationSource.Token);
		requestCancellationSource.Cancel();
		await AssertCanceledAsync(waitTask.WaitAsync(TimeSpan.FromSeconds(2)), "request cancellation");
	}

	var serverStopBuffer = new McpDebugEventBuffer();
	using (var serverStopSource = new CancellationTokenSource()) {
		var waitTask = serverStopBuffer.WaitForEventAsync(null, null, Timeout.Infinite, serverCancellationToken: serverStopSource.Token);
		serverStopSource.Cancel();
		await AssertCanceledAsync(waitTask.WaitAsync(TimeSpan.FromSeconds(2)), "server-stop cancellation");
	}

	var nextEventBuffer = new McpDebugEventBuffer();
	var oldEvent = AddDebugEvent(nextEventBuffer, "old-event");
	var nextEventTask = nextEventBuffer.WaitForEventAsync(null, null, 2000);
	await Task.Delay(40);
	Assert(!nextEventTask.IsCompleted, "afterSequence=null must ignore events that existed before the wait began.");
	var newEvent = AddDebugEvent(nextEventBuffer, "new-event");
	var nextEventResult = await nextEventTask.WaitAsync(TimeSpan.FromSeconds(2));
	AssertEqual(newEvent.Sequence, nextEventResult.Event?.Sequence ?? 0, "sequence returned by an afterSequence=null wait");
	AssertEqual(newEvent.Sequence, nextEventResult.LatestSequence, "cursor returned by an afterSequence=null wait");

	var bufferedResult = await nextEventBuffer.WaitForEventAsync(null, oldEvent.Sequence - 1, 0);
	AssertEqual(oldEvent.Sequence, bufferedResult.Event?.Sequence ?? 0, "buffered event returned for an explicit afterSequence");
	AssertEqual(oldEvent.Sequence, bufferedResult.LatestSequence, "cursor for a buffered event");

	var filteredTask = nextEventBuffer.WaitForEventAsync(new[] { "breakpoint-hit" }, newEvent.Sequence, 2000);
	AddDebugEvent(nextEventBuffer, "module-loaded");
	await Task.Delay(40);
	Assert(!filteredTask.IsCompleted, "An unmatched event must not complete a filtered wait.");
	var matchingEvent = AddDebugEvent(nextEventBuffer, "breakpoint-hit");
	var filteredResult = await filteredTask.WaitAsync(TimeSpan.FromSeconds(2));
	AssertEqual(matchingEvent.Sequence, filteredResult.Event?.Sequence ?? 0, "filtered debug event wait result");
	AssertEqual(matchingEvent.Sequence, filteredResult.LatestSequence, "filtered debug event wait cursor");

	var outputTask = nextEventBuffer.WaitForEventAsync(null, matchingEvent.Sequence, 2000, outputOnly: true);
	AddDebugEvent(nextEventBuffer, "exception-thrown", isOutputLine: false);
	await Task.Delay(40);
	Assert(!outputTask.IsCompleted, "A non-output event must not complete an output-only wait.");
	var outputEvent = AddDebugEvent(nextEventBuffer, "program-message", isOutputLine: true);
	var outputResult = await outputTask.WaitAsync(TimeSpan.FromSeconds(2));
	AssertEqual(outputEvent.Sequence, outputResult.Event?.Sequence ?? 0, "output-only debug event wait result");
	AssertEqual(outputEvent.Sequence, outputResult.LatestSequence, "output-only debug event wait cursor");

	var filteredTimeoutBuffer = new McpDebugEventBuffer();
	var filteredBaseline = AddDebugEvent(filteredTimeoutBuffer, "baseline");
	var filteredTimeoutTask = filteredTimeoutBuffer.WaitForEventAsync(new[] { "wanted" }, null, 80);
	var ignoredEvent = AddDebugEvent(filteredTimeoutBuffer, "ignored");
	var filteredTimeoutResult = await filteredTimeoutTask.WaitAsync(TimeSpan.FromSeconds(2));
	Assert(filteredTimeoutResult.Event is null, "A filtered wait must time out when only unmatched events arrive.");
	AssertEqual(ignoredEvent.Sequence, filteredTimeoutResult.LatestSequence, "filtered timeout cursor");
	Assert(filteredTimeoutResult.LatestSequence > filteredBaseline.Sequence, "A timeout cursor must advance past unmatched buffered events.");
}

static async Task RunDebuggerMutationCoordinatorTestsAsync() {
	var compatibleDispatcher = new CompatibleDbgDispatcher();
	var compatibleCallbackCount = 0;
	Assert(compatibleDispatcher.TryBeginInvoke(() => compatibleCallbackCount++),
		"The default DbgDispatcher.TryBeginInvoke implementation must preserve existing derived dispatchers.");
	AssertEqual(1, compatibleCallbackCount, "default DbgDispatcher.TryBeginInvoke callback count");

	var rejectedDispatcher = new ManualDispatcher { AcceptCallbacks = false };
	var rejectedActionCount = 0;
	var rejectionError = await AssertThrowsAsync<InvalidOperationException>(Task.Run(() => McpDebuggerMutationCoordinator.Run(
		rejectedDispatcher.TryBeginInvoke,
		() => Interlocked.Increment(ref rejectedActionCount),
		TimeSpan.FromSeconds(30),
		CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2)), "rejected debugger mutation first hop");
	Assert(rejectionError.Message.Contains("shutting down", StringComparison.OrdinalIgnoreCase),
		"A rejected first dispatcher hop must report a clear shutdown error.");
	AssertEqual(0, rejectedActionCount, "rejected debugger mutation action count");
	AssertEqual(0, rejectedDispatcher.PendingCount, "rejected debugger mutation pending callback count");

	var canceledDispatcher = new ManualDispatcher();
	var canceledActionCount = 0;
	using (var cancellationSource = new CancellationTokenSource()) {
		var mutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
			canceledDispatcher.TryBeginInvoke,
			() => Interlocked.Increment(ref canceledActionCount),
			TimeSpan.FromSeconds(2),
			cancellationSource.Token));
		await WaitUntilAsync(() => canceledDispatcher.PendingCount == 1, "queued debugger mutation before cancellation");
		cancellationSource.Cancel();
		await AssertCanceledAsync(mutationTask.WaitAsync(TimeSpan.FromSeconds(2)), "queued debugger mutation cancellation");
		canceledDispatcher.RunNext();
		AssertEqual(0, canceledActionCount, "abandoned canceled debugger mutation action count");
	}

	var timedOutDispatcher = new ManualDispatcher();
	var timedOutActionCount = 0;
	var timedOutMutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
		timedOutDispatcher.TryBeginInvoke,
		() => Interlocked.Increment(ref timedOutActionCount),
		TimeSpan.FromMilliseconds(60),
		CancellationToken.None));
	await WaitUntilAsync(() => timedOutDispatcher.PendingCount == 1, "queued debugger mutation before timeout");
	await AssertThrowsAsync<TimeoutException>(timedOutMutationTask.WaitAsync(TimeSpan.FromSeconds(2)), "queued debugger mutation timeout");
	timedOutDispatcher.RunNext();
	AssertEqual(0, timedOutActionCount, "abandoned timed-out debugger mutation action count");

	var barrierRejectedDispatcher = new ManualDispatcher();
	var barrierRejectedActionCount = 0;
	var barrierRejectedMutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
		barrierRejectedDispatcher.TryBeginInvoke,
		() => {
			Interlocked.Increment(ref barrierRejectedActionCount);
			barrierRejectedDispatcher.AcceptCallbacks = false;
		},
		TimeSpan.FromSeconds(30),
		CancellationToken.None));
	await WaitUntilAsync(() => barrierRejectedDispatcher.PendingCount == 1, "queued debugger mutation before barrier rejection");
	barrierRejectedDispatcher.RunNext();
	var barrierRejectionError = await AssertThrowsAsync<InvalidOperationException>(barrierRejectedMutationTask.WaitAsync(TimeSpan.FromSeconds(2)),
		"rejected debugger mutation completion barrier");
	Assert(barrierRejectionError.Message.Contains("completion barrier", StringComparison.OrdinalIgnoreCase),
		"A rejected completion barrier must report a clear shutdown error.");
	AssertEqual(1, barrierRejectedActionCount, "barrier-rejected debugger mutation action count");
	AssertEqual(0, barrierRejectedDispatcher.PendingCount, "barrier-rejected pending callback count");

	var runningCancellationDispatcher = new ManualDispatcher();
	using (var cancellationSource = new CancellationTokenSource())
	using (var actionStarted = new ManualResetEventSlim())
	using (var releaseAction = new ManualResetEventSlim()) {
		var runningCancellationActionCount = 0;
		var mutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
			runningCancellationDispatcher.TryBeginInvoke,
			() => {
				Interlocked.Increment(ref runningCancellationActionCount);
				actionStarted.Set();
				releaseAction.Wait();
			},
			TimeSpan.FromSeconds(2),
			cancellationSource.Token));
		await WaitUntilAsync(() => runningCancellationDispatcher.PendingCount == 1, "queued debugger mutation before running cancellation");
		var executeTask = Task.Run(runningCancellationDispatcher.RunNext);
		await WaitUntilAsync(() => actionStarted.IsSet, "started debugger mutation before cancellation");
		cancellationSource.Cancel();
		await Task.Delay(40);
		Assert(!mutationTask.IsCompleted, "Cancellation after a debugger mutation starts must wait for the real completion barrier.");
		releaseAction.Set();
		await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
		AssertEqual(1, runningCancellationDispatcher.PendingCount, "queued completion barrier after running cancellation");
		runningCancellationDispatcher.RunNext();
		await mutationTask.WaitAsync(TimeSpan.FromSeconds(2));
		AssertEqual(1, runningCancellationActionCount, "completed debugger mutation action count after cancellation");
	}

	var runningTimeoutDispatcher = new ManualDispatcher();
	using (var actionStarted = new ManualResetEventSlim())
	using (var releaseAction = new ManualResetEventSlim()) {
		var runningTimeoutActionCount = 0;
		var mutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
			runningTimeoutDispatcher.TryBeginInvoke,
			() => {
				Interlocked.Increment(ref runningTimeoutActionCount);
				actionStarted.Set();
				releaseAction.Wait();
			},
			TimeSpan.FromMilliseconds(60),
			CancellationToken.None));
		await WaitUntilAsync(() => runningTimeoutDispatcher.PendingCount == 1, "queued debugger mutation before running timeout");
		var executeTask = Task.Run(runningTimeoutDispatcher.RunNext);
		await WaitUntilAsync(() => actionStarted.IsSet, "started debugger mutation before timeout");
		await Task.Delay(100);
		Assert(!mutationTask.IsCompleted, "Timeout after a debugger mutation starts must wait for the real completion barrier.");
		releaseAction.Set();
		await executeTask.WaitAsync(TimeSpan.FromSeconds(2));
		AssertEqual(1, runningTimeoutDispatcher.PendingCount, "queued completion barrier after running timeout");
		runningTimeoutDispatcher.RunNext();
		await mutationTask.WaitAsync(TimeSpan.FromSeconds(2));
		AssertEqual(1, runningTimeoutActionCount, "completed debugger mutation action count after timeout");
	}

	var barrierDispatcher = new ManualDispatcher();
	var barrierActionCount = 0;
	var barrierMutationTask = Task.Run(() => McpDebuggerMutationCoordinator.Run(
		barrierDispatcher.TryBeginInvoke,
		() => Interlocked.Increment(ref barrierActionCount),
		TimeSpan.FromSeconds(2),
		CancellationToken.None));
	await WaitUntilAsync(() => barrierDispatcher.PendingCount == 1, "queued debugger mutation action");
	barrierDispatcher.RunNext();
	AssertEqual(1, barrierActionCount, "executed debugger mutation action count");
	Assert(!barrierMutationTask.IsCompleted, "A debugger mutation must not complete before its FIFO barrier runs.");
	AssertEqual(1, barrierDispatcher.PendingCount, "queued debugger mutation FIFO barrier count");
	barrierDispatcher.RunNext();
	await barrierMutationTask.WaitAsync(TimeSpan.FromSeconds(2));
}

static McpDebugEventEntry AddDebugEvent(McpDebugEventBuffer buffer, string kind, bool isOutputLine = false) =>
	buffer.Add(sequence => new McpDebugEventEntry(sequence, DateTimeOffset.UtcNow, kind, "info", kind, isOutputLine, null, null, null, null, null, null, null, null));

static async Task AssertCanceledAsync(Task task, string description) {
	try {
		await task.ConfigureAwait(false);
	}
	catch (OperationCanceledException) {
		return;
	}
	throw new InvalidOperationException($"Expected {description} to cancel the debug event wait.");
}

static async Task<TException> AssertThrowsAsync<TException>(Task task, string description) where TException : Exception {
	try {
		await task.ConfigureAwait(false);
	}
	catch (TException ex) {
		return ex;
	}
	throw new InvalidOperationException($"Expected {description} to throw {typeof(TException).Name}.");
}

static async Task WaitUntilAsync(Func<bool> condition, string description) {
	var timeoutAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.0);
	while (!condition()) {
		if (Stopwatch.GetTimestamp() >= timeoutAt)
			throw new TimeoutException($"Timed out waiting for {description}.");
		await Task.Delay(5).ConfigureAwait(false);
	}
}

static void Assert(bool condition, string message) {
	if (!condition)
		throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string description) where TException : Exception {
	try {
		action();
	}
	catch (TException) {
		return;
	}
	throw new InvalidOperationException($"Expected {description} to throw {typeof(TException).Name}.");
}

static void AssertEqual<T>(T expected, T actual, string description) where T : notnull {
	if (!EqualityComparer<T>.Default.Equals(expected, actual))
		throw new InvalidOperationException($"Expected {description} to be '{expected}', but it was '{actual}'.");
}

sealed class CompatibleDbgDispatcher : dnSpy.Contracts.Debugger.DbgDispatcher {
	public override bool CheckAccess() => true;

	public override void BeginInvoke(Action callback) => callback();
}

sealed class ManualDispatcher {
	readonly object lockObj = new object();
	readonly Queue<Action> callbacks = new Queue<Action>();
	bool acceptCallbacks = true;

	public bool AcceptCallbacks {
		get {
			lock (lockObj)
				return acceptCallbacks;
		}
		set {
			lock (lockObj)
				acceptCallbacks = value;
		}
	}

	public int PendingCount {
		get {
			lock (lockObj)
				return callbacks.Count;
		}
	}

	public bool TryBeginInvoke(Action callback) {
		if (callback is null)
			throw new ArgumentNullException(nameof(callback));
		lock (lockObj) {
			if (!acceptCallbacks)
				return false;
			callbacks.Enqueue(callback);
			return true;
		}
	}

	public void RunNext() {
		Action callback;
		lock (lockObj)
			callback = callbacks.Dequeue();
		callback();
	}
}

[McpServerToolType]
static class TypedFailureFilterTransportTools {
	[McpServerTool(Name = "save_assembly_to_file")]
	public static SaveAssemblyResult FailSave() => new SaveAssemblyResult(false, "Save failed.", null);
}
