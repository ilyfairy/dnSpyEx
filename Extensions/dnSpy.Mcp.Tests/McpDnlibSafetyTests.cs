using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using dnSpy.Mcp;

namespace dnSpy.Mcp {
	static class McpDnlibSafetyTests {
		public static void Run() {
			RunTokenKindTests();
			RunInstructionValidationTests();
			RunCustomAttributeTokenTests();
			RunAttributeReachabilityTests();
		}

		static void RunTokenKindTests() {
			var (_, module) = CreateAssembly("TokenTests");
			var owner = AddType(module, "Tests", "Owner", module.CorLibTypes.Object.TypeDefOrRef);
			var method = AddMethod(owner, "Method", MethodSig.CreateStatic(module.CorLibTypes.Void));
			var field = AddField(owner, "Field", new FieldSig(module.CorLibTypes.Int32));
			var methodRef = module.UpdateRowId(new MemberRefUser(module, "ReferencedMethod", MethodSig.CreateStatic(module.CorLibTypes.Void), owner));
			var fieldRef = module.UpdateRowId(new MemberRefUser(module, "ReferencedField", new FieldSig(module.CorLibTypes.Int32), owner));
			var methodSpec = module.UpdateRowId(new MethodSpecUser(method, new GenericInstMethodSig(module.CorLibTypes.Int32)));
			var typeSpec = module.UpdateRowId(new TypeSpecUser(new SZArraySig(module.CorLibTypes.String)));
			AddOperandReferences(owner, methodRef, fieldRef, methodSpec, typeSpec);
			using var loaded = Reload(module);
			AssertType<MethodDef>(McpDnlibSafety.ResolveMethodToken(loaded, method.MDToken.Raw), "MethodDef method token");
			AssertMethodMemberRef(McpDnlibSafety.ResolveMethodToken(loaded, methodRef.MDToken.Raw), "method MemberRef token");
			AssertType<MethodSpec>(McpDnlibSafety.ResolveMethodToken(loaded, methodSpec.MDToken.Raw), "MethodSpec token");
			AssertThrows(() => McpDnlibSafety.ResolveMethodToken(loaded, field.MDToken.Raw), "FieldDef must not resolve as a method");
			AssertThrows(() => McpDnlibSafety.ResolveMethodToken(loaded, fieldRef.MDToken.Raw), "field MemberRef must not resolve as a method");

			AssertType<FieldDef>(McpDnlibSafety.ResolveFieldToken(loaded, field.MDToken.Raw), "FieldDef field token");
			AssertFieldMemberRef(McpDnlibSafety.ResolveFieldToken(loaded, fieldRef.MDToken.Raw), "field MemberRef token");
			AssertThrows(() => McpDnlibSafety.ResolveFieldToken(loaded, method.MDToken.Raw), "MethodDef must not resolve as a field");
			AssertThrows(() => McpDnlibSafety.ResolveFieldToken(loaded, methodRef.MDToken.Raw), "method MemberRef must not resolve as a field");

			AssertType<TypeDef>(McpDnlibSafety.ResolveTypeToken(loaded, owner.MDToken.Raw), "TypeDef type token");
			AssertType<TypeSpec>(McpDnlibSafety.ResolveTypeToken(loaded, typeSpec.MDToken.Raw), "TypeSpec type token");
			AssertThrows(() => McpDnlibSafety.ResolveTypeToken(loaded, method.MDToken.Raw), "method token must not resolve as a type");
		}

		static void RunInstructionValidationTests() {
			var (_, module) = CreateAssembly("InstructionTests");
			var owner = AddType(module, "Tests", "Owner", module.CorLibTypes.Object.TypeDefOrRef);
			var method = AddMethod(owner, "Method", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32));
			var methodRef = module.UpdateRowId(new MemberRefUser(module, "ReferencedMethod", MethodSig.CreateStatic(module.CorLibTypes.Void), owner));
			var fieldRef = module.UpdateRowId(new MemberRefUser(module, "ReferencedField", new FieldSig(module.CorLibTypes.Int32), owner));
			var methodSpec = module.UpdateRowId(new MethodSpecUser(methodRef, new GenericInstMethodSig(module.CorLibTypes.Int32)));
			var typeSpec = module.UpdateRowId(new TypeSpecUser(new SZArraySig(module.CorLibTypes.String)));
			var local = new Local(module.CorLibTypes.Int32);
			var parameter = method.Parameters[0];

			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Call, methodRef);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Call, methodSpec);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldfld, fieldRef);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldtoken, typeSpec);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldloc, local);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldarg, parameter);
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Switch, new System.Collections.Generic.List<Instruction> { Instruction.Create(OpCodes.Ret) });
			McpDnlibSafety.ValidateInstructionOperand(OpCodes.Calli, MethodSig.CreateStatic(module.CorLibTypes.Void));
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Call, fieldRef), "call must reject a field MemberRef");
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldfld, methodRef), "ldfld must reject a method MemberRef");
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldloc, parameter), "ldloc must reject a parameter");
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldarg, local), "ldarg must reject a local");
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Ldc_I4, 1L), "ldc.i4 must reject an Int64 operand");
			AssertThrows(() => McpDnlibSafety.ValidateInstructionOperand(OpCodes.Calli, new FieldSig(module.CorLibTypes.Int32)), "calli must reject a field signature");
			AssertThrows(() => McpDnlibSafety.ValidateOpCode(OpCodes.Prefix1), "reserved prefix opcode");

			AssertType<sbyte>(McpDnlibSafety.CreateShortInlineIOperand(OpCodes.Ldc_I4_S, -128), "ldc.i4.s operand type");
			AssertType<byte>(McpDnlibSafety.CreateShortInlineIOperand(OpCodes.Unaligned, 255), "unaligned operand type");
			AssertType<byte>(McpDnlibSafety.CreateShortInlineIOperand(OpCodes.No, 1), "no. operand type");
			AssertThrows(() => McpDnlibSafety.CreateShortInlineIOperand(OpCodes.Ldc_I4_S, 128), "ldc.i4.s overflow");
			AssertThrows(() => McpDnlibSafety.CreateShortInlineIOperand(OpCodes.Unaligned, 256), "unaligned overflow");
		}

		static void RunCustomAttributeTokenTests() {
			var (assembly, module) = CreateAssembly("AttributeTokenTests");
			var validType = AddAttributeType(module, "Tests", "ValidAttribute");
			var validCtor = AddMethod(validType, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void));
			var resolver = new McpAttributeConstructorResolver(module, new[] { assembly });
			var fieldCtor = module.UpdateRowId(new MemberRefUser(module, ".ctor", new FieldSig(module.CorLibTypes.Int32), validType));
			var staticCtor = AddMethod(validType, ".ctor", MethodSig.CreateStatic(module.CorLibTypes.Void));
			var returningCtor = AddMethod(validType, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Int32));
			var ordinaryType = AddType(module, "Tests", "NotAnAttribute", module.CorLibTypes.Object.TypeDefOrRef);
			var ordinaryCtor = AddMethod(ordinaryType, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void));
			var fakeCoreLib = new AssemblyDefUser("Fake.CoreLib", new Version(1, 0, 0, 0));
			var fakeCoreLibRef = module.UpdateRowId(fakeCoreLib.ToAssemblyRef());
			var spoofedType = AddType(module, "Tests", "SpoofedAttribute", new TypeRefUser(module, "System", "Attribute", fakeCoreLibRef));
			var spoofedCtor = AddMethod(spoofedType, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void));
			AddOperandReferences(validType, validCtor, fieldCtor, null, null);
			using var loaded = Reload(module);
			var loadedResolver = new McpAttributeConstructorResolver(loaded, new[] { loaded.Assembly! });
			AssertType<MethodDef>(loadedResolver.ResolveByToken(validCtor.MDToken.Raw), "valid custom attribute constructor token");
			AssertThrows(() => loadedResolver.ResolveByToken(fieldCtor.MDToken.Raw), "field MemberRef custom attribute token");
			AssertThrows(() => loadedResolver.ResolveByToken(staticCtor.MDToken.Raw), "static custom attribute constructor");
			AssertThrows(() => loadedResolver.ResolveByToken(returningCtor.MDToken.Raw), "non-void custom attribute constructor");
			AssertThrows(() => loadedResolver.ResolveByToken(ordinaryCtor.MDToken.Raw), "constructor on a non-Attribute type");
			AssertThrows(() => loadedResolver.ResolveByToken(spoofedCtor.MDToken.Raw), "System.Attribute from a non-corelib assembly");
			_ = resolver;
		}

		static void RunAttributeReachabilityTests() {
			var (targetAssembly, targetModule) = CreateAssembly("TargetAssembly");
			var (referencedAssembly, referencedModule) = CreateAssembly("ReferencedAttributes");
			AddAssemblyReference(targetModule, referencedAssembly);
			var referencedType = AddAttributeType(referencedModule, "Tests", "ReachableAttribute");
			AddMethod(referencedType, ".ctor", MethodSig.CreateInstance(referencedModule.CorLibTypes.Void));
			var (unrelatedAssembly, unrelatedModule) = CreateAssembly("UnrelatedAttributes");
			var unrelatedType = AddAttributeType(unrelatedModule, "Tests", "ReachableAttribute");
			AddMethod(unrelatedType, ".ctor", MethodSig.CreateInstance(unrelatedModule.CorLibTypes.Void));
			var unreferencedOnly = AddAttributeType(unrelatedModule, "Tests", "UnreferencedOnlyAttribute");
			AddMethod(unreferencedOnly, ".ctor", MethodSig.CreateInstance(unrelatedModule.CorLibTypes.Void));
			var baseAttribute = AddAttributeType(referencedModule, "Tests", "BaseAttribute");
			var indirectAttribute = AddType(referencedModule, "Tests", "IndirectAttribute", baseAttribute);
			AddMethod(indirectAttribute, ".ctor", MethodSig.CreateInstance(referencedModule.CorLibTypes.Void));

			using var loadedTarget = Reload(targetModule);
			using var loadedReferenced = Reload(referencedModule);
			using var loadedUnrelated = Reload(unrelatedModule);
			Assert(loadedTarget.GetAssemblyRefs().Any(), "Target module must preserve its AssemblyRef after serialization.");
			var resolver = new McpAttributeConstructorResolver(loadedTarget, new[] { loadedTarget.Assembly!, loadedReferenced.Assembly!, loadedUnrelated.Assembly! });
			var resolved = resolver.ResolveByName("Tests.Reachable", 0);
			Assert(resolved is not null, "A referenced attribute type must resolve by name.");
			AssertSame(loadedTarget, resolved!.Module, "referenced constructor import target module");
			Assert(string.Equals(resolved.DeclaringType?.FullName, referencedType.FullName, StringComparison.Ordinal), "The unreferenced same-name attribute must not affect resolution.");
			Assert(resolver.ResolveByName("Tests.UnreferencedOnly", 0) is null, "An unreferenced loaded assembly must not supply an attribute constructor.");
			Assert(resolver.ResolveByName("Tests.Indirect", 0) is not null, "Indirect System.Attribute inheritance must be accepted.");

			var (ambiguousTargetAssembly, ambiguousTargetModule) = CreateAssembly("AmbiguousTarget");
			AddAssemblyReference(ambiguousTargetModule, referencedAssembly);
			var targetDuplicate = AddAttributeType(ambiguousTargetModule, "Tests", "ReachableAttribute");
			AddMethod(targetDuplicate, ".ctor", MethodSig.CreateInstance(ambiguousTargetModule.CorLibTypes.Void));
			using var loadedAmbiguousTarget = Reload(ambiguousTargetModule);
			var ambiguousTypeResolver = new McpAttributeConstructorResolver(loadedAmbiguousTarget, new[] { loadedAmbiguousTarget.Assembly!, loadedReferenced.Assembly!, loadedUnrelated.Assembly! });
			AssertThrows(() => ambiguousTypeResolver.ResolveByName("Tests.Reachable", 0), "attribute type ambiguity must be detected before constructor selection");

			var (duplicateReferencedAssembly, _) = CreateAssembly("ReferencedAttributes");
			AssertThrows(() => new McpAttributeConstructorResolver(loadedTarget, new[] { loadedTarget.Assembly!, loadedReferenced.Assembly!, duplicateReferencedAssembly }), "duplicate loaded assembly identity");
			_ = targetAssembly;
			_ = ambiguousTargetAssembly;
		}

		static (AssemblyDefUser Assembly, ModuleDefUser Module) CreateAssembly(string name) {
			var assembly = new AssemblyDefUser(name, new Version(1, 0, 0, 0));
			var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll };
			assembly.Modules.Add(module);
			return (assembly, module);
		}

		static ModuleDefMD Reload(ModuleDef module) {
			using var stream = new MemoryStream();
			var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
			options.MetadataOptions.Flags |= MetadataFlags.PreserveRids;
			module.Write(stream, options);
			return ModuleDefMD.Load(stream.ToArray());
		}

		static void AddAssemblyReference(ModuleDef module, AssemblyDef assembly) {
			var assemblyReference = module.UpdateRowId(assembly.ToAssemblyRef());
			var markerType = module.UpdateRowId(new TypeRefUser(module, "Mcp", "AssemblyReferenceMarker", assemblyReference));
			var markerMethod = module.UpdateRowId(new MemberRefUser(module, "Touch", MethodSig.CreateStatic(module.CorLibTypes.Void), markerType));
			var owner = AddType(module, "Mcp", "AssemblyReferenceHolder" + module.Types.Count, module.CorLibTypes.Object.TypeDefOrRef);
			var holder = AddMethod(owner, "Reference", MethodSig.CreateStatic(module.CorLibTypes.Void));
			holder.Body = new CilBody();
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Call, markerMethod));
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		}
		static TypeDefUser AddAttributeType(ModuleDef module, string @namespace, string name) => AddType(module, @namespace, name, new TypeRefUser(module, "System", "Attribute", module.CorLibTypes.AssemblyRef));
		static TypeDefUser AddType(ModuleDef module, string @namespace, string name, ITypeDefOrRef? baseType) {
			var type = new TypeDefUser(@namespace, name, baseType);
			module.Types.Add(type);
			module.UpdateRowId(type);
			return type;
		}
		static MethodDefUser AddMethod(TypeDef type, string name, MethodSig signature) {
			var method = new MethodDefUser(name, signature);
			type.Methods.Add(method);
			type.Module.UpdateRowId(method);
			return method;
		}
		static FieldDefUser AddField(TypeDef type, string name, FieldSig signature) {
			var field = new FieldDefUser(name, signature);
			type.Fields.Add(field);
			type.Module.UpdateRowId(field);
			return field;
		}
		static void AddOperandReferences(TypeDef owner, IMethod method, IField field, IMethod? methodSpec, ITypeDefOrRef? typeSpec) {
			var holder = AddMethod(owner, "OperandHolder" + owner.Methods.Count, MethodSig.CreateStatic(owner.Module.CorLibTypes.Void));
			holder.Body = new CilBody();
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Call, method));
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field));
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
			if (methodSpec is not null)
				holder.Body.Instructions.Add(Instruction.Create(OpCodes.Call, methodSpec));
			if (typeSpec is not null) {
				holder.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, typeSpec));
				holder.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
			}
			holder.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		}
		static void Assert(bool condition, string message) {
			if (!condition)
				throw new InvalidOperationException(message);
		}
		static void AssertSame(object expected, object? actual, string description) {
			if (!ReferenceEquals(expected, actual))
				throw new InvalidOperationException($"Expected {description} to preserve the exact dnlib object.");
		}
		static void AssertType<T>(object value, string description) {
			if (value is not T)
				throw new InvalidOperationException($"Expected {description} to be {typeof(T).Name}, but it was {value.GetType().Name}.");
		}
		static void AssertMethodMemberRef(object value, string description) {
			if (value is not MemberRef memberRef || !memberRef.IsMethodRef)
				throw new InvalidOperationException($"Expected {description} to be a method MemberRef.");
		}
		static void AssertFieldMemberRef(object value, string description) {
			if (value is not MemberRef memberRef || !memberRef.IsFieldRef)
				throw new InvalidOperationException($"Expected {description} to be a field MemberRef.");
		}
		static void AssertThrows(Action action, string description) {
			try {
				action();
			}
			catch (InvalidOperationException) {
				return;
			}
			throw new InvalidOperationException($"Expected {description} to be rejected.");
		}
	}
}
