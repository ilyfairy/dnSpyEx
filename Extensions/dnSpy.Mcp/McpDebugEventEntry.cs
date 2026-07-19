using System;

namespace dnSpy.Mcp {
	public sealed record McpDebugEventEntry(long Sequence, DateTimeOffset TimestampUtc, string Kind, string Severity, string Message, bool IsOutputLine, int? ProcessId, string? ProcessName, string? ProcessFilename, string? RuntimeName, ulong? ThreadId, string? ModuleName, string? ModuleFilename, string? AppDomainName);
}
