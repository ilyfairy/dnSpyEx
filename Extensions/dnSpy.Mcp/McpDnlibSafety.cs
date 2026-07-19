using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Mcp {
	static class McpDnlibSafety {
		public static IMethod ResolveMethodToken(ModuleDef module, uint rawToken) {
			var provider = ResolveToken(module, rawToken, "method");
			if (!IsMethodOperand(provider))
				throw new InvalidOperationException($"Metadata token '0x{rawToken:X8}' does not identify a method in module '{module.Name}'.");
			return (IMethod)provider;
		}

		public static IField ResolveFieldToken(ModuleDef module, uint rawToken) {
			var provider = ResolveToken(module, rawToken, "field");
			if (!IsFieldOperand(provider))
				throw new InvalidOperationException($"Metadata token '0x{rawToken:X8}' does not identify a field in module '{module.Name}'.");
			return (IField)provider;
		}

		public static ITypeDefOrRef ResolveTypeToken(ModuleDef module, uint rawToken) {
			var provider = ResolveToken(module, rawToken, "type");
			return provider as ITypeDefOrRef ?? throw new InvalidOperationException($"Metadata token '0x{rawToken:X8}' does not identify a type in module '{module.Name}'.");
		}

		public static bool IsMethodOperand(object? operand) => operand switch {
			MemberRef memberRef => memberRef.IsMethodRef,
			IMethod when operand is not IField => true,
			_ => false,
		};

		public static bool IsFieldOperand(object? operand) => operand switch {
			MemberRef memberRef => memberRef.IsFieldRef,
			IField when operand is not IMethod => true,
			_ => false,
		};

		public static void ValidateOpCode(OpCode opCode) {
			if (opCode.OpCodeType == OpCodeType.Nternal || opCode.OperandType == OperandType.InlinePhi)
				throw new InvalidOperationException($"IL opcode '{opCode.Name}' is reserved or unsupported and cannot be emitted.");
		}

		public static void ValidateInstructionOperand(OpCode opCode, object? operand) {
			ValidateOpCode(opCode);
			var valid = opCode.OperandType switch {
				OperandType.InlineNone => operand is null,
				OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget => operand is Instruction,
				OperandType.InlineSwitch => operand is IList<Instruction>,
				OperandType.ShortInlineVar or OperandType.InlineVar => IsValidVariableOperand(opCode.Code, operand),
				OperandType.InlineString => operand is string,
				OperandType.InlineI => operand is int,
				OperandType.InlineI8 => operand is long,
				OperandType.InlineR => operand is double,
				OperandType.ShortInlineR => operand is float,
				OperandType.ShortInlineI => opCode.Code is Code.Unaligned or Code.No ? operand is byte : operand is sbyte,
				OperandType.InlineType => operand is ITypeDefOrRef,
				OperandType.InlineMethod => IsMethodOperand(operand),
				OperandType.InlineField => IsFieldOperand(operand),
				OperandType.InlineTok => operand is ITypeDefOrRef || IsMethodOperand(operand) || IsFieldOperand(operand),
				OperandType.InlineSig => operand is MethodSig,
				_ => false,
			};
			if (!valid)
				throw new InvalidOperationException($"IL opcode '{opCode.Name}' has an invalid {opCode.OperandType} operand of type '{operand?.GetType().FullName ?? "<null>"}'.");
		}

		public static object CreateShortInlineIOperand(OpCode opCode, int value) {
			ValidateOpCode(opCode);
			if (opCode.OperandType != OperandType.ShortInlineI)
				throw new InvalidOperationException($"IL opcode '{opCode.Name}' does not use a ShortInlineI operand.");
			if (opCode.Code is Code.Unaligned or Code.No) {
				if (value is < byte.MinValue or > byte.MaxValue)
					throw new InvalidOperationException($"ShortInlineI operand for '{opCode.Name}' must be between {byte.MinValue} and {byte.MaxValue}.");
				return (byte)value;
			}
			if (value is < sbyte.MinValue or > sbyte.MaxValue)
				throw new InvalidOperationException($"ShortInlineI operand for '{opCode.Name}' must be between {sbyte.MinValue} and {sbyte.MaxValue}.");
			return (sbyte)value;
		}

		static bool IsValidVariableOperand(Code code, object? operand) => code switch {
			Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S or Code.Stloc or Code.Stloc_S => operand is Local,
			Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S or Code.Starg or Code.Starg_S => operand is Parameter,
			_ => false,
		};

		static IMDTokenProvider ResolveToken(ModuleDef module, uint rawToken, string expectedKind) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			try {
				return module.ResolveToken(rawToken) ?? throw new InvalidOperationException($"Could not resolve {expectedKind} metadata token '0x{rawToken:X8}' in module '{module.Name}'.");
			}
			catch (InvalidOperationException) {
				throw;
			}
			catch (Exception ex) {
				throw new InvalidOperationException($"Could not resolve {expectedKind} metadata token '0x{rawToken:X8}' in module '{module.Name}'.", ex);
			}
		}
	}

	sealed class McpAttributeConstructorResolver {
		static readonly AssemblyNameComparer assemblyComparer = AssemblyNameComparer.CompareAll;
		static readonly IEqualityComparer<AssemblyDef> assemblyReferenceComparer = DnlibReferenceComparer<AssemblyDef>.Instance;
		static readonly IEqualityComparer<ModuleDef> moduleReferenceComparer = DnlibReferenceComparer<ModuleDef>.Instance;
		static readonly IEqualityComparer<TypeDef> typeReferenceComparer = DnlibReferenceComparer<TypeDef>.Instance;
		readonly ModuleDef targetModule;
		readonly AssemblyDef[] reachableAssemblies;
		readonly ModuleDef[] reachableModules;

		public McpAttributeConstructorResolver(ModuleDef targetModule, IEnumerable<AssemblyDef> loadedAssemblies) {
			this.targetModule = targetModule ?? throw new ArgumentNullException(nameof(targetModule));
			if (loadedAssemblies is null)
				throw new ArgumentNullException(nameof(loadedAssemblies));
			(reachableAssemblies, reachableModules) = CreateReachableScope(targetModule, loadedAssemblies);
		}

		public IMethodDefOrRef ResolveByToken(uint rawToken) {
			var provider = targetModule.ResolveToken(rawToken);
			if (provider is not IMethodDefOrRef constructor || provider is MemberRef memberRef && !memberRef.IsMethodRef)
				throw new InvalidOperationException($"Metadata token '0x{rawToken:X8}' does not identify a custom attribute constructor in module '{targetModule.Name}'.");
			ValidateConstructor(constructor);
			return constructor;
		}

		public IMethodDefOrRef? ResolveByName(string attributeName, int parameterCount) {
			if (string.IsNullOrWhiteSpace(attributeName))
				throw new ArgumentException("Attribute name must not be empty.", nameof(attributeName));
			if (parameterCount < 0)
				throw new ArgumentOutOfRangeException(nameof(parameterCount));

			var normalized = attributeName.Trim();
			var withSuffix = normalized.EndsWith("Attribute", StringComparison.Ordinal) ? normalized : normalized + "Attribute";
			var candidateNames = new[] { normalized, withSuffix }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var allTypes = reachableModules.SelectMany(module => module.GetTypes()).Distinct(typeReferenceComparer).ToArray();
			var exactMatches = allTypes.Where(type => candidateNames.Any(name =>
				string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(type.ReflectionFullName, name, StringComparison.OrdinalIgnoreCase))).ToArray();
			var type = GetUniqueType(exactMatches, attributeName);
			if (type is null) {
				var shortMatches = allTypes.Where(candidate => candidateNames.Any(name => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
				type = GetUniqueType(shortMatches, attributeName);
			}
			if (type is null)
				return null;
			EnsureAttributeType(type);

			var constructors = type.Methods.Where(method =>
				IsValidConstructorShape(method) && method.MethodSig!.Params.Count == parameterCount).ToArray();
			if (constructors.Length == 0)
				return null;
			if (constructors.Length > 1)
				throw new InvalidOperationException($"Attribute constructor '{attributeName}' with {parameterCount} parameter(s) is ambiguous in type '{type.FullName}'. Matches: {string.Join(" | ", constructors.Take(10).Select(a => a.FullName))}.");

			var constructor = constructors[0];
			if (constructor.Module == targetModule)
				return constructor;
			return targetModule.Import(constructor) as IMethodDefOrRef ?? throw new InvalidOperationException($"Could not import attribute constructor '{constructor.FullName}' into module '{targetModule.Name}'.");
		}

		public void ValidateConstructor(IMethodDefOrRef constructor) {
			if (constructor is null)
				throw new ArgumentNullException(nameof(constructor));
			if (constructor is MemberRef memberRef && !memberRef.IsMethodRef)
				throw new InvalidOperationException($"Metadata member '{constructor.FullName}' is a field, not a custom attribute constructor.");
			if (!IsValidConstructorShape(constructor))
				throw new InvalidOperationException($"Method '{constructor.FullName}' is not a valid custom attribute constructor. A constructor must be named .ctor, be an instance method, and return void.");
			var declaringType = ResolveTypeDefinition(constructor.DeclaringType) ?? throw new InvalidOperationException($"Could not resolve the declaring type of custom attribute constructor '{constructor.FullName}' within the target assembly's reachable references.");
			EnsureAttributeType(declaringType);
		}

		static bool IsValidConstructorShape(IMethodDefOrRef constructor) {
			var signature = constructor.MethodSig;
			return string.Equals(constructor.Name, ".ctor", StringComparison.Ordinal) &&
				signature is not null &&
				signature.IsDefault &&
				signature.HasThis &&
				!signature.ExplicitThis &&
				signature.GenParamCount == 0 &&
				signature.RetType.ElementType == ElementType.Void;
		}

		void EnsureAttributeType(TypeDef type) {
			var visited = new HashSet<TypeDef>(typeReferenceComparer);
			var current = type;
			while (visited.Add(current)) {
				var baseType = current.BaseType;
				if (baseType is null)
					break;
				if (IsSystemAttributeBase(baseType, current.Module))
					return;
				current = ResolveTypeDefinition(baseType) ?? throw new InvalidOperationException($"Could not resolve base type '{baseType.FullName}' while validating attribute type '{type.FullName}'.");
			}
			throw new InvalidOperationException($"Type '{type.FullName}' does not inherit System.Attribute and cannot be used as a custom attribute.");
		}

		static bool IsSystemAttributeBase(ITypeDefOrRef baseType, ModuleDef? derivedModule) {
			if (derivedModule is null || !string.Equals(baseType.FullName, "System.Attribute", StringComparison.Ordinal))
				return false;
			var definitionAssembly = baseType.DefinitionAssembly;
			return definitionAssembly is not null && assemblyComparer.Equals(definitionAssembly, derivedModule.CorLibTypes.AssemblyRef);
		}

		TypeDef? ResolveTypeDefinition(ITypeDefOrRef? type) {
			if (type is null)
				return null;
			if (type is TypeDef typeDef)
				return reachableModules.Contains(typeDef.Module) ? typeDef : null;

			var definitionAssembly = type.DefinitionAssembly;
			IEnumerable<ModuleDef> modules;
			if (definitionAssembly is not null) {
				var assemblies = reachableAssemblies.Where(assembly => assemblyComparer.Equals(definitionAssembly, assembly)).ToArray();
				if (assemblies.Length > 1)
					throw new InvalidOperationException($"Type reference '{type.FullName}' has an ambiguous reachable assembly identity '{definitionAssembly.FullName}'.");
				if (assemblies.Length == 0)
					return null;
				modules = assemblies[0].Modules;
			}
			else
				modules = new[] { targetModule };

			var matches = modules.SelectMany(module => module.GetTypes())
				.Where(candidate => string.Equals(candidate.FullName, type.FullName, StringComparison.Ordinal))
				.Distinct(typeReferenceComparer)
				.ToArray();
			if (matches.Length > 1)
				throw new InvalidOperationException($"Type reference '{type.FullName}' is ambiguous within its reachable assembly.");
			return matches.Length == 1 ? matches[0] : null;
		}

		static TypeDef? GetUniqueType(TypeDef[] matches, string attributeName) {
			var uniqueMatches = matches.Distinct(typeReferenceComparer).ToArray();
			if (uniqueMatches.Length == 0)
				return null;
			if (uniqueMatches.Length == 1)
				return uniqueMatches[0];
			throw new InvalidOperationException($"Attribute type '{attributeName}' is ambiguous across reachable assemblies. Matches: {string.Join(" | ", uniqueMatches.Take(10).Select(type => $"{type.FullName} ({type.Module?.Assembly?.FullName ?? type.Module?.Name})"))}.");
		}

		static (AssemblyDef[] Assemblies, ModuleDef[] Modules) CreateReachableScope(ModuleDef targetModule, IEnumerable<AssemblyDef> loadedAssemblies) {
			var loaded = loadedAssemblies.Where(assembly => assembly is not null).Distinct(assemblyReferenceComparer).ToArray();
			var targetAssembly = targetModule.Assembly;
			var reachableAssemblies = new HashSet<AssemblyDef>(assemblyReferenceComparer);
			var reachableModules = new HashSet<ModuleDef>(moduleReferenceComparer) { targetModule };
			var pendingModules = new Queue<ModuleDef>();
			if (targetAssembly is not null) {
				reachableAssemblies.Add(targetAssembly);
				foreach (var module in targetAssembly.Modules) {
					reachableModules.Add(module);
					pendingModules.Enqueue(module);
				}
			}
			else
				pendingModules.Enqueue(targetModule);

			while (pendingModules.Count != 0) {
				var module = pendingModules.Dequeue();
				foreach (var assemblyReference in module.GetAssemblyRefs().Distinct(assemblyComparer)) {
					AssemblyDef[] matches;
					if (targetAssembly is not null && assemblyComparer.Equals(assemblyReference, targetAssembly))
						matches = new[] { targetAssembly };
					else
						matches = loaded.Where(assembly => assemblyComparer.Equals(assemblyReference, assembly)).ToArray();
					if (matches.Length == 0)
						continue;
					if (matches.Length > 1)
						throw new InvalidOperationException($"Assembly reference '{assemblyReference.FullName}' is ambiguous across loaded assemblies. Matches: {string.Join(" | ", matches.Select(a => a.FullName))}.");
					var resolvedAssembly = matches[0];
					if (!reachableAssemblies.Add(resolvedAssembly))
						continue;
					foreach (var resolvedModule in resolvedAssembly.Modules) {
						if (reachableModules.Add(resolvedModule))
							pendingModules.Enqueue(resolvedModule);
					}
				}
			}

			return (reachableAssemblies.ToArray(), reachableModules.ToArray());
		}
	}

	sealed class DnlibReferenceComparer<T> : IEqualityComparer<T> where T : class {
		public static readonly DnlibReferenceComparer<T> Instance = new DnlibReferenceComparer<T>();

		DnlibReferenceComparer() {
		}

		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
		public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
