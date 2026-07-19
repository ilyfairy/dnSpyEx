using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace dnSpy.Mcp {
	static class McpHttpSecurity {
		public static bool IsListenAddressAllowed(string? listenAddress) => IsLoopbackListenAddress(listenAddress);

		public static bool HasValidBearerToken(string? authorization, string bearerToken) {
			const string bearerPrefix = "Bearer ";
			if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
				return false;

			var suppliedToken = authorization.Substring(bearerPrefix.Length);
			var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
			var expectedBytes = Encoding.UTF8.GetBytes(bearerToken);
			return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
		}

		public static bool IsLoopbackListenAddress(string? listenAddress) {
			var host = NormalizeHost(listenAddress);
			if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
				return true;
			if (!IPAddress.TryParse(host, out var address))
				return false;
			if (address.IsIPv4MappedToIPv6)
				address = address.MapToIPv4();
			return IPAddress.IsLoopback(address);
		}

		public static bool IsHostAllowed(string? requestHost, int? requestPort, string requestScheme, string listenAddress, int listenPort) {
			if (!IsLoopbackListenAddress(listenAddress))
				return false;
			if (!IsLoopbackListenAddress(requestHost))
				return false;
			return GetEffectivePort(requestPort, requestScheme) == listenPort;
		}

		public static bool IsOriginAllowed(string? origin, string? requestHost, int? requestPort, string requestScheme, string listenAddress, int listenPort) {
			if (!IsLoopbackListenAddress(listenAddress))
				return false;
			if (origin is null)
				return true;
			if (string.IsNullOrWhiteSpace(origin) ||
				origin.IndexOf(',', StringComparison.Ordinal) >= 0 ||
				!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
				!(string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
				  string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
				!string.IsNullOrEmpty(originUri.UserInfo) ||
				!string.IsNullOrEmpty(originUri.Query) ||
				!string.IsNullOrEmpty(originUri.Fragment) ||
				originUri.AbsolutePath != "/" ||
				!string.Equals(originUri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase)) {
				return false;
			}

			var originPort = originUri.IsDefaultPort ? GetDefaultPort(originUri.Scheme) : originUri.Port;
			if (originPort != GetEffectivePort(requestPort, requestScheme) || originPort != listenPort)
				return false;

			return IsLoopbackListenAddress(originUri.Host) && IsLoopbackListenAddress(requestHost);
		}

		static string NormalizeHost(string? host) {
			var normalized = host?.Trim() ?? string.Empty;
			if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
				normalized = normalized.Substring(1, normalized.Length - 2);
			return normalized.TrimEnd('.');
		}

		static int GetEffectivePort(int? port, string scheme) => port ?? GetDefaultPort(scheme);

		static int GetDefaultPort(string scheme) =>
			string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
	}
}
