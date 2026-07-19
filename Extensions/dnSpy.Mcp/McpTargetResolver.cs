using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace dnSpy.Mcp {
	readonly record struct McpDocumentIdentity(
		int Index,
		string Filename,
		string ShortName,
		string AssemblyName,
		string AssemblyFullName,
		string ModuleName,
		string ModuleFullName);

	readonly record struct McpTypeIdentity(
		int Index,
		string ModuleName,
		string FullName,
		string ReflectionFullName,
		string Name);

	static class McpTargetResolver {
		static readonly StringComparer comparer = StringComparer.OrdinalIgnoreCase;

		public static void ValidateCurrentDebugThreadProcess(int? requestedProcessId, int? currentProcessId) {
			if (requestedProcessId is null)
				return;
			if (currentProcessId is null)
				throw new InvalidOperationException($"Process {requestedProcessId.Value} was specified without a thread id, but there is no current debug thread. Call list_threads and pass threadId or managedThreadId.");
			if (currentProcessId.Value != requestedProcessId.Value)
				throw new InvalidOperationException($"The current debug thread belongs to process {currentProcessId.Value}, not requested process {requestedProcessId.Value}. Call list_threads and pass threadId or managedThreadId for the requested process.");
		}

		public static int? FindDocumentByExactPath(IReadOnlyList<McpDocumentIdentity> documents, string identifier) {
			if (!LooksLikePathIdentifier(identifier.Trim()))
				return null;
			var normalizedIdentifier = NormalizePath(identifier);
			if (normalizedIdentifier.Length == 0)
				return null;
			return FindUnique(
				documents,
				identifier,
				"document path",
				a => !string.IsNullOrWhiteSpace(a.Filename) && comparer.Equals(NormalizePath(a.Filename), normalizedIdentifier),
				a => a.Filename,
				a => a.Index);
		}

		public static int? FindDocumentByIdentity(IReadOnlyList<McpDocumentIdentity> documents, string identifier) {
			var normalizedIdentifier = identifier.Trim();
			var tiers = new List<Func<McpDocumentIdentity, bool>> {
				a => comparer.Equals(a.AssemblyFullName, normalizedIdentifier),
				a => comparer.Equals(a.ModuleFullName, normalizedIdentifier),
			};
			if (!LooksLikePathIdentifier(normalizedIdentifier)) {
				var fileName = GetFileName(normalizedIdentifier);
				if (fileName.Length != 0)
					tiers.Add(a => comparer.Equals(GetFileName(a.Filename), fileName));
			}
			tiers.Add(a => comparer.Equals(a.ShortName, normalizedIdentifier));
			tiers.Add(a => comparer.Equals(a.AssemblyName, normalizedIdentifier));
			tiers.Add(a => comparer.Equals(a.ModuleName, normalizedIdentifier));

			foreach (var tier in tiers) {
				var match = FindUnique(documents, identifier, "document identifier", tier, a => a.Filename, a => a.Index);
				if (match is not null)
					return match;
			}
			return null;
		}

		public static int? FindType(IReadOnlyList<McpTypeIdentity> types, string identifier) {
			var normalizedIdentifier = identifier.Trim();
			var exactMatch = FindUnique(
				types,
				identifier,
				"type name",
				a => comparer.Equals(a.FullName, normalizedIdentifier) || comparer.Equals(a.ReflectionFullName, normalizedIdentifier),
				DescribeType,
				a => a.Index);
			if (exactMatch is not null)
				return exactMatch;

			return FindUnique(
				types,
				identifier,
				"short type name",
				a => comparer.Equals(a.Name, normalizedIdentifier),
				DescribeType,
				a => a.Index);
		}

		static int? FindUnique<T>(
			IReadOnlyList<T> candidates,
			string identifier,
			string targetKind,
			Func<T, bool> predicate,
			Func<T, string> describe,
			Func<T, int> getIndex) {
			var matches = candidates.Where(predicate).ToArray();
			if (matches.Length == 0)
				return null;
			if (matches.Length == 1)
				return getIndex(matches[0]);

			var descriptions = matches
				.Select(describe)
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.Distinct(comparer)
				.Take(10)
				.ToArray();
			var suffix = descriptions.Length == 0 ? string.Empty : $" Matches: {string.Join(" | ", descriptions)}.";
			throw new InvalidOperationException($"Ambiguous {targetKind} '{identifier}' matched {matches.Length} loaded targets.{suffix}");
		}

		static string DescribeType(McpTypeIdentity type) =>
			string.IsNullOrWhiteSpace(type.ModuleName) ? type.FullName : $"{type.FullName} (module: {type.ModuleName})";

		static bool LooksLikePathIdentifier(string identifier) =>
			Path.IsPathRooted(identifier) ||
			identifier.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
			identifier.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

		static string GetFileName(string path) {
			try {
				return Path.GetFileName(path) ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}

		static string NormalizePath(string path) {
			if (string.IsNullOrWhiteSpace(path))
				return string.Empty;
			try {
				return Path.GetFullPath(path.Trim());
			}
			catch {
				return path.Trim();
			}
		}
	}
}
