using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Reflection;

using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
Pipe clientToServerPipe = new(), serverToClientPipe = new();

var tools = typeof(ErrorSemanticsTools)
	.GetMethods(BindingFlags.Public | BindingFlags.Static)
	.Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
	.Select(method => McpServerTool.Create(method))
	.ToArray();

await using McpServer server = McpServer.Create(
	new StreamServerTransport(clientToServerPipe.Reader.AsStream(), serverToClientPipe.Writer.AsStream()),
	new McpServerOptions { ToolCollection = [.. tools] });

var serverTask = server.RunAsync(timeoutSource.Token);
try {
	await using McpClient client = await McpClient.CreateAsync(
		new StreamClientTransport(clientToServerPipe.Writer.AsStream(), serverToClientPipe.Reader.AsStream()),
		cancellationToken: timeoutSource.Token);

	var registeredToolNames = (await client.ListToolsAsync(cancellationToken: timeoutSource.Token))
		.Select(tool => tool.Name)
		.ToHashSet(StringComparer.Ordinal);
	Assert(registeredToolNames.SetEquals(["throw_mcp_exception", "throw_ordinary_exception"]),
		"The typed error tools were not registered through the MCP server.");

	var mcpExceptionResult = await client.CallToolAsync(
		"throw_mcp_exception",
		cancellationToken: timeoutSource.Token);
	AssertError(mcpExceptionResult, "McpException");
	var mcpExceptionText = GetText(mcpExceptionResult);
	Assert(mcpExceptionText.Contains(ErrorSemanticsTools.McpErrorMessage, StringComparison.Ordinal),
		"An McpException message must be included in the tool error result.");

	var ordinaryExceptionResult = await client.CallToolAsync(
		"throw_ordinary_exception",
		cancellationToken: timeoutSource.Token);
	AssertError(ordinaryExceptionResult, "ordinary exception");
	var ordinaryExceptionText = GetText(ordinaryExceptionResult);
	Assert(!ordinaryExceptionText.Contains(ErrorSemanticsTools.SensitiveExceptionMessage, StringComparison.Ordinal),
		"An ordinary exception's internal message must not be exposed to the MCP client.");
	Assert(ordinaryExceptionText.Contains("An error occurred invoking 'throw_ordinary_exception'.", StringComparison.Ordinal),
		"An ordinary exception must produce the SDK's generic tool error message.");
}
finally {
	timeoutSource.Cancel();
	try {
		await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
	}
	catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested) {
	}
}

Console.WriteLine("MCP SDK error semantics validation passed over the in-memory stream transport.");

static void AssertError(CallToolResult result, string description) {
	Assert(result.IsError is true, $"The {description} call must return CallToolResult.IsError=true.");
	Assert(result.Content.Count > 0, $"The {description} call must return error content.");
}

static string GetText(CallToolResult result) =>
	string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text));

static void Assert(bool condition, string message) {
	if (!condition)
		throw new InvalidOperationException(message);
}

[McpServerToolType]
internal static class ErrorSemanticsTools {
	public const string McpErrorMessage = "The requested MCP operation failed.";
	public const string SensitiveExceptionMessage = "INTERNAL-SENSITIVE-DETAIL-7F0A";

	[McpServerTool(Name = "throw_mcp_exception"), Description("Throws an MCP-safe error for transport semantics testing.")]
	public static string ThrowMcpException() =>
		throw new McpException(McpErrorMessage);

	[McpServerTool(Name = "throw_ordinary_exception"), Description("Throws an internal error for transport semantics testing.")]
	public static string ThrowOrdinaryException() =>
		throw new InvalidOperationException(SensitiveExceptionMessage);
}
