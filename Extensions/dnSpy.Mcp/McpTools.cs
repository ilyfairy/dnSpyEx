using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.DotNet.Mono;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Debugger.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.DotNet.Writer;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Settings;
using dnSpy.Contracts.Scripting;
using dnSpy.Contracts.Documents.TreeView;
using ModelContextProtocol.Server;

namespace dnSpy.Mcp {
	[McpServerToolType]
	public sealed class DnSpyMcpTools {
		readonly McpServerController mcpServerController;
		readonly McpOutputLogger logger;
		readonly DbgManager dbgManager;
		readonly DbgCodeBreakpointsService dbgCodeBreakpointsService;
		readonly DbgCodeBreakpointHitCountService dbgCodeBreakpointHitCountService;
		readonly DbgCallStackService dbgCallStackService;
		readonly AttachableProcessesService attachableProcessesService;
		readonly DbgLanguageService dbgLanguageService;
		readonly DbgDotNetBreakpointFactory dbgDotNetBreakpointFactory;
		readonly DbgExceptionSettingsService dbgExceptionSettingsService;
		readonly IDsDocumentService documentService;
		readonly IDocumentTreeView documentTreeView;
		readonly IDocumentTabService documentTabService;
		readonly IDecompilerService decompilerService;
		readonly ILanguageCompilerProvider[] languageCompilerProviders;
		readonly ISettingsService settingsService;
		readonly IServiceLocator serviceLocator;
		static int nextToolCallId;
		static readonly object debugModulesCacheLock = new object();
		static string? lastDebugModulesCacheKey;
		static DateTime lastDebugModulesCacheUtc;
		static DebugModuleInfo[]? lastDebugModulesCacheValue;

		public DnSpyMcpTools(McpServerController mcpServerController, McpOutputLogger logger, DbgManager dbgManager, DbgCodeBreakpointsService dbgCodeBreakpointsService, DbgCodeBreakpointHitCountService dbgCodeBreakpointHitCountService, DbgCallStackService dbgCallStackService, AttachableProcessesService attachableProcessesService, DbgLanguageService dbgLanguageService, DbgDotNetBreakpointFactory dbgDotNetBreakpointFactory, DbgExceptionSettingsService dbgExceptionSettingsService, IDsDocumentService documentService, IDocumentTreeView documentTreeView, IDocumentTabService documentTabService, IDecompilerService decompilerService, IEnumerable<ILanguageCompilerProvider> languageCompilerProviders, ISettingsService settingsService, IServiceLocator serviceLocator) {
			this.mcpServerController = mcpServerController;
			this.logger = logger;
			this.dbgManager = dbgManager;
			this.dbgCodeBreakpointsService = dbgCodeBreakpointsService;
			this.dbgCodeBreakpointHitCountService = dbgCodeBreakpointHitCountService;
			this.dbgCallStackService = dbgCallStackService;
			this.attachableProcessesService = attachableProcessesService;
			this.dbgLanguageService = dbgLanguageService;
			this.dbgDotNetBreakpointFactory = dbgDotNetBreakpointFactory;
			this.dbgExceptionSettingsService = dbgExceptionSettingsService;
			this.documentService = documentService;
			this.documentTreeView = documentTreeView;
			this.documentTabService = documentTabService;
			this.decompilerService = decompilerService;
			this.languageCompilerProviders = languageCompilerProviders.OrderBy(a => a.Order).ToArray();
			this.settingsService = settingsService;
			this.serviceLocator = serviceLocator;
		}

		[McpServerTool(Name = "list_loaded_assemblies"), Description("Lists the assemblies and modules currently loaded in dnSpyEx.")]
		public LoadedDocumentInfo[] ListLoadedAssemblies() => LoggedCall("list_loaded_assemblies", string.Empty, () =>
			documentService.GetDocuments().Select(ToLoadedDocumentInfo).ToArray());

		[McpServerTool(Name = "list_decompilers"), Description("Lists the decompilers currently available in dnSpyEx.")]
		public DecompilerInfo[] ListDecompilers() => LoggedCall("list_decompilers", string.Empty, () =>
			decompilerService.AllDecompilers
				.OrderBy(a => a.OrderUI)
				.Select(a => new DecompilerInfo(a.UniqueNameUI, a.GenericNameUI, a.FileExtension, a.UniqueGuid.ToString(), a == decompilerService.Decompiler))
				.ToArray());

		[McpServerTool(Name = "load_assembly"), Description("Loads a .NET assembly or module into dnSpyEx and returns its document descriptor.")]
		public LoadedDocumentInfo LoadAssembly([Description("Absolute or relative path to the assembly or module to load. Example: C:\\temp\\MyApp.dll")] string path) =>
			LoggedCall("load_assembly", path, () => {
				if (string.IsNullOrWhiteSpace(path))
					throw new ArgumentException("Assembly path must not be empty.", nameof(path));

				var fullPath = Path.GetFullPath(path);
				if (!File.Exists(fullPath))
					throw new FileNotFoundException($"Assembly not found: {fullPath}", fullPath);

				var document = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(fullPath));
				if (document is null)
					throw new InvalidOperationException($"dnSpyEx could not load the assembly: {fullPath}");

				return ToLoadedDocumentInfo(document);
			});

		[McpServerTool(Name = "open_assembly"), Description("Alias of load_assembly. Loads a .NET assembly or module into dnSpyEx and returns its document descriptor.")]
		public LoadedDocumentInfo OpenAssembly([Description("Absolute or relative path to the assembly or module to load. Example: C:\\temp\\MyApp.dll")] string path) => LoadAssembly(path);

		[McpServerTool(Name = "close_assembly"), Description("Closes a loaded assembly or module.")]
		public BreakpointOperationResult CloseAssembly(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("close_assembly", documentId, () => {
				var document = ResolveDocument(documentId);
				documentService.Remove(document.Key);
				return new BreakpointOperationResult(true, $"Closed document '{document.Filename}'.");
			});

		[McpServerTool(Name = "clear_assemblies"), Description("Closes all loaded assemblies and modules.")]
		public BreakpointOperationResult ClearAssemblies() => LoggedBackgroundCall("clear_assemblies", string.Empty, () => {
			documentService.Clear();
			return new BreakpointOperationResult(true, "Cleared all loaded documents.");
		});

		[McpServerTool(Name = "reload_assembly"), Description("Reloads an already loaded assembly/module from disk.")]
		public LoadedDocumentInfo ReloadAssembly(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("reload_assembly", documentId, () => {
				var document = ResolveDocument(documentId);
				var filename = document.Filename;
				if (string.IsNullOrWhiteSpace(filename) || !File.Exists(filename))
					throw new FileNotFoundException($"Cannot reload '{documentId}' because its backing file could not be found.", filename);

				documentService.Remove(document.Key);
				var reloaded = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(filename));
				if (reloaded is null)
					throw new InvalidOperationException($"dnSpyEx could not reload assembly '{filename}'.");
				return ToLoadedDocumentInfo(reloaded);
			});

		[McpServerTool(Name = "save_assembly_to_file"), Description("Saves the edited in-memory assembly/module to disk.")]
		public SaveAssemblyResult SaveAssemblyToFile(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional output path. When omitted, overwrites the original file.")] string? outputPath = null) => LoggedCall("save_assembly_to_file", documentId, () => {
				var document = ResolveDocument(documentId);
				var module = document.ModuleDef ?? throw new InvalidOperationException($"Document '{document.Filename}' does not contain a module definition.");
				var targetPath = string.IsNullOrWhiteSpace(outputPath) ? document.Filename : Path.GetFullPath(outputPath);
				if (string.IsNullOrWhiteSpace(targetPath))
					return new SaveAssemblyResult(false, "Could not determine output path.", null, null);
				try {
					var directory = Path.GetDirectoryName(targetPath);
					if (!string.IsNullOrWhiteSpace(directory))
						Directory.CreateDirectory(directory);
					var options = new ModuleWriterOptions(module) {
						Logger = DummyLogger.NoThrowInstance,
					};
					module.Write(targetPath, options);
					return new SaveAssemblyResult(true, "Assembly saved.", targetPath, null);
				}
				catch (Exception ex) {
					var actual = UnwrapToolException(ex);
					return new SaveAssemblyResult(false, actual.Message, targetPath, actual.Message);
				}
			});

		[McpServerTool(Name = "get_module_info"), Description("Returns the current editable module metadata and PE/Cor20 settings.")]
		public ModuleInfoResult GetModuleInfo(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_module_info", documentId, () => {
				var document = ResolveDocument(documentId);
				var module = document.ModuleDef ?? throw new InvalidOperationException($"Document '{document.Filename}' does not contain a module definition.");
				return ToModuleInfoResult(document, module);
			});

		[McpServerTool(Name = "get_module_settings"), Description("Returns the current editable module settings shown by dnSpy's Edit Module dialog.")]
		public ModuleInfoResult GetModuleSettings(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => GetModuleInfo(documentId);

		[McpServerTool(Name = "edit_module"), Description("Edits module metadata and PE/Cor20 settings shown in dnSpy's Edit Module dialog.")]
		public ModuleEditResult EditModule(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional module name.")] string? name = null,
			[Description("Optional module kind, eg Windows, Console, Dll, NetModule.")] string? moduleKind = null,
			[Description("Optional CLR version preset: 1.0, 1.1, 2.0, 4.0.")] string? clrVersion = null,
			[Description("Optional MVID GUID string. Pass empty string to clear.")] string? mvid = null,
			[Description("Optional EncId GUID string. Pass empty string to clear.")] string? encId = null,
			[Description("Optional EncBaseId GUID string. Pass empty string to clear.")] string? encBaseId = null,
			[Description("Entry point kind: none, managed, native.")] string? entryPointKind = null,
			[Description("Managed entry point metadata token when entryPointKind=managed.")] string? managedEntryPointMetadataToken = null,
			[Description("Native entry point RVA when entryPointKind=native.")] uint? nativeEntryPointRva = null,
			[Description("Optional metadata version string, eg v4.0.30319.")] string? runtimeVersion = null,
			[Description("Optional tables header version, eg 0x0200 or 512.")] ushort? tablesHeaderVersion = null,
			[Description("Optional Cor20 runtime version, eg 0x00020005.")] uint? cor20HeaderRuntimeVersion = null,
			[Description("Optional machine, eg I386, AMD64, IA64, ARM64.")] string? machine = null,
			[Description("Characteristics.RelocsStripped flag override.")] bool? relocsStripped = null,
			[Description("Characteristics.ExecutableImage flag override.")] bool? executableImage = null,
			[Description("Characteristics.LineNumsStripped flag override.")] bool? lineNumsStripped = null,
			[Description("Characteristics.LocalSymsStripped flag override.")] bool? localSymsStripped = null,
			[Description("Characteristics.AggressiveWsTrim flag override.")] bool? aggressiveWsTrim = null,
			[Description("Characteristics.LargeAddressAware flag override.")] bool? largeAddressAware = null,
			[Description("Characteristics.Reserved1 flag override.")] bool? characteristicsReserved1 = null,
			[Description("Characteristics.BytesReversedLo flag override.")] bool? bytesReversedLo = null,
			[Description("Characteristics.Bit32Machine flag override.")] bool? bit32Machine = null,
			[Description("Characteristics.DebugStripped flag override.")] bool? debugStripped = null,
			[Description("Characteristics.RemovableRunFromSwap flag override.")] bool? removableRunFromSwap = null,
			[Description("Characteristics.NetRunFromSwap flag override.")] bool? netRunFromSwap = null,
			[Description("Characteristics.System flag override.")] bool? system = null,
			[Description("Characteristics.Dll flag override.")] bool? dll = null,
			[Description("Characteristics.UpSystemOnly flag override.")] bool? upSystemOnly = null,
			[Description("Characteristics.BytesReversedHi flag override.")] bool? bytesReversedHi = null,
			[Description("DllCharacteristics.Reserved1 flag override.")] bool? dllReserved1 = null,
			[Description("DllCharacteristics.Reserved2 flag override.")] bool? dllReserved2 = null,
			[Description("DllCharacteristics.Reserved3 flag override.")] bool? dllReserved3 = null,
			[Description("DllCharacteristics.Reserved4 flag override.")] bool? dllReserved4 = null,
			[Description("DllCharacteristics.Reserved5 flag override.")] bool? dllReserved5 = null,
			[Description("DllCharacteristics.HighEntropyVA flag override.")] bool? highEntropyVa = null,
			[Description("DllCharacteristics.DynamicBase flag override.")] bool? dynamicBase = null,
			[Description("DllCharacteristics.ForceIntegrity flag override.")] bool? forceIntegrity = null,
			[Description("DllCharacteristics.NxCompat flag override.")] bool? nxCompat = null,
			[Description("DllCharacteristics.NoIsolation flag override.")] bool? noIsolation = null,
			[Description("DllCharacteristics.NoSeh flag override.")] bool? noSeh = null,
			[Description("DllCharacteristics.NoBind flag override.")] bool? noBind = null,
			[Description("DllCharacteristics.AppContainer flag override.")] bool? appContainer = null,
			[Description("DllCharacteristics.WdmDriver flag override.")] bool? wdmDriver = null,
			[Description("DllCharacteristics.GuardCf flag override.")] bool? guardCf = null,
			[Description("DllCharacteristics.TerminalServerAware flag override.")] bool? terminalServerAware = null,
			[Description("Cor20 ILOnly flag override.")] bool? ilOnly = null,
			[Description("Cor20 32BitRequired flag override.")] bool? bit32Required = null,
			[Description("Cor20 ILLibrary flag override.")] bool? ilLibrary = null,
			[Description("Cor20 32BitPreferred flag override.")] bool? bit32Preferred = null,
			[Description("Cor20 TrackDebugData flag override.")] bool? trackDebugData = null,
			[Description("Cor20 StrongNameSigned flag override.")] bool? strongNameSigned = null) => LoggedCall("edit_module", documentId, () => {
				var document = ResolveDocument(documentId);
				var module = document.ModuleDef ?? throw new InvalidOperationException($"Document '{document.Filename}' does not contain a module definition.");

				if (name is not null)
					module.Name = name;
				if (!string.IsNullOrWhiteSpace(moduleKind)) {
					if (!Enum.TryParse(moduleKind.Trim(), true, out ModuleKind parsedModuleKind))
						return CreateModuleEditFailure($"Invalid moduleKind '{moduleKind}'.", module);
					module.Kind = parsedModuleKind;
				}
				if (mvid is not null) {
					if (!TryParseNullableGuidText(mvid, out var parsedGuid, out var guidError))
						return CreateModuleEditFailure(guidError ?? "Invalid MVID.", module);
					module.Mvid = parsedGuid;
				}
				if (encId is not null) {
					if (!TryParseNullableGuidText(encId, out var parsedGuid, out var guidError))
						return CreateModuleEditFailure(guidError ?? "Invalid EncId.", module);
					module.EncId = parsedGuid;
				}
				if (encBaseId is not null) {
					if (!TryParseNullableGuidText(encBaseId, out var parsedGuid, out var guidError))
						return CreateModuleEditFailure(guidError ?? "Invalid EncBaseId.", module);
					module.EncBaseId = parsedGuid;
				}
				if (!string.IsNullOrWhiteSpace(clrVersion)) {
					if (!TryApplyClrVersionPreset(module, clrVersion.Trim(), out var clrError))
						return CreateModuleEditFailure(clrError ?? $"Invalid clrVersion '{clrVersion}'.", module);
				}
				if (runtimeVersion is not null)
					module.RuntimeVersion = runtimeVersion;
				if (tablesHeaderVersion is not null)
					module.TablesHeaderVersion = tablesHeaderVersion;
				if (cor20HeaderRuntimeVersion is not null)
					module.Cor20HeaderRuntimeVersion = cor20HeaderRuntimeVersion;
				if (!string.IsNullOrWhiteSpace(machine)) {
					if (!Enum.TryParse(machine.Trim(), true, out Machine parsedMachine))
						return CreateModuleEditFailure($"Invalid machine '{machine}'.", module);
					module.Machine = parsedMachine;
				}

				var characteristics = module.Characteristics;
				characteristics = UpdateFlag(characteristics, Characteristics.RelocsStripped, relocsStripped);
				characteristics = UpdateFlag(characteristics, Characteristics.ExecutableImage, executableImage);
				characteristics = UpdateFlag(characteristics, Characteristics.LineNumsStripped, lineNumsStripped);
				characteristics = UpdateFlag(characteristics, Characteristics.LocalSymsStripped, localSymsStripped);
				characteristics = UpdateFlag(characteristics, Characteristics.AggressiveWsTrim, aggressiveWsTrim);
				characteristics = UpdateFlag(characteristics, Characteristics.LargeAddressAware, largeAddressAware);
				characteristics = UpdateFlag(characteristics, Characteristics.Reserved1, characteristicsReserved1);
				characteristics = UpdateFlag(characteristics, Characteristics.BytesReversedLo, bytesReversedLo);
				characteristics = UpdateFlag(characteristics, Characteristics.Bit32Machine, bit32Machine);
				characteristics = UpdateFlag(characteristics, Characteristics.DebugStripped, debugStripped);
				characteristics = UpdateFlag(characteristics, Characteristics.RemovableRunFromSwap, removableRunFromSwap);
				characteristics = UpdateFlag(characteristics, Characteristics.NetRunFromSwap, netRunFromSwap);
				characteristics = UpdateFlag(characteristics, Characteristics.System, system);
				characteristics = UpdateFlag(characteristics, Characteristics.Dll, dll);
				characteristics = UpdateFlag(characteristics, Characteristics.UpSystemOnly, upSystemOnly);
				characteristics = UpdateFlag(characteristics, Characteristics.BytesReversedHi, bytesReversedHi);
				module.Characteristics = characteristics;

				var dllChars = module.DllCharacteristics;
				dllChars = UpdateFlag(dllChars, DllCharacteristics.Reserved1, dllReserved1);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.Reserved2, dllReserved2);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.Reserved3, dllReserved3);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.Reserved4, dllReserved4);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.Reserved5, dllReserved5);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.HighEntropyVA, highEntropyVa);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.DynamicBase, dynamicBase);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.ForceIntegrity, forceIntegrity);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.NxCompat, nxCompat);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.NoIsolation, noIsolation);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.NoSeh, noSeh);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.NoBind, noBind);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.AppContainer, appContainer);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.WdmDriver, wdmDriver);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.GuardCf, guardCf);
				dllChars = UpdateFlag(dllChars, DllCharacteristics.TerminalServerAware, terminalServerAware);
				module.DllCharacteristics = dllChars;

				var cor20Flags = module.Cor20HeaderFlags;
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.ILOnly, ilOnly);
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.Bit32Required, bit32Required);
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.ILLibrary, ilLibrary);
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.Bit32Preferred, bit32Preferred);
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.TrackDebugData, trackDebugData);
				cor20Flags = UpdateFlag(cor20Flags, ComImageFlags.StrongNameSigned, strongNameSigned);
				module.Cor20HeaderFlags = cor20Flags;

				if (entryPointKind is not null) {
					switch (entryPointKind.Trim().ToLowerInvariant()) {
					case "none":
						module.ManagedEntryPoint = null;
						module.NativeEntryPoint = 0;
						module.Cor20HeaderFlags &= ~ComImageFlags.NativeEntryPoint;
						break;
					case "managed": {
						if (string.IsNullOrWhiteSpace(managedEntryPointMetadataToken))
							return CreateModuleEditFailure("managedEntryPointMetadataToken is required when entryPointKind=managed.", module);
						var method = ResolveMethodByMetadataToken(document, managedEntryPointMetadataToken!);
						if (method.Module != module)
							return CreateModuleEditFailure($"Managed entry point '{method.FullName}' does not belong to module '{module.Name}'.", module);
						module.ManagedEntryPoint = method;
						module.NativeEntryPoint = 0;
						module.Cor20HeaderFlags &= ~ComImageFlags.NativeEntryPoint;
						break;
					}
					case "native":
						if (nativeEntryPointRva is null)
							return CreateModuleEditFailure("nativeEntryPointRva is required when entryPointKind=native.", module);
						module.ManagedEntryPoint = null;
						module.NativeEntryPoint = (RVA)nativeEntryPointRva.Value;
						module.Cor20HeaderFlags |= ComImageFlags.NativeEntryPoint;
						break;
					default:
						return CreateModuleEditFailure($"Invalid entryPointKind '{entryPointKind}'. Expected none, managed, or native.", module);
					}
				}

				return CreateModuleEditSuccess("Module updated.", module);
			});

		[McpServerTool(Name = "update_module_settings"), Description("Updates the editable module settings shown by dnSpy's Edit Module dialog.")]
		public ModuleEditResult UpdateModuleSettings(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional module name.")] string? name = null,
			[Description("Optional module kind, eg Windows, Console, Dll, NetModule.")] string? moduleKind = null,
			[Description("Optional CLR version preset: 1.0, 1.1, 2.0, 4.0.")] string? clrVersion = null,
			[Description("Optional MVID GUID string. Pass empty string to clear.")] string? mvid = null,
			[Description("Optional EncId GUID string. Pass empty string to clear.")] string? encId = null,
			[Description("Optional EncBaseId GUID string. Pass empty string to clear.")] string? encBaseId = null,
			[Description("Entry point kind: none, managed, native.")] string? entryPointKind = null,
			[Description("Managed entry point metadata token when entryPointKind=managed.")] string? managedEntryPointMetadataToken = null,
			[Description("Native entry point RVA when entryPointKind=native.")] uint? nativeEntryPointRva = null,
			[Description("Optional metadata version string, eg v4.0.30319.")] string? runtimeVersion = null,
			[Description("Optional tables header version, eg 0x0200 or 512.")] ushort? tablesHeaderVersion = null,
			[Description("Optional Cor20 runtime version, eg 0x00020005.")] uint? cor20HeaderRuntimeVersion = null,
			[Description("Optional machine, eg I386, AMD64, IA64, ARM64.")] string? machine = null,
			[Description("Characteristics.RelocsStripped flag override.")] bool? relocsStripped = null,
			[Description("Characteristics.ExecutableImage flag override.")] bool? executableImage = null,
			[Description("Characteristics.LineNumsStripped flag override.")] bool? lineNumsStripped = null,
			[Description("Characteristics.LocalSymsStripped flag override.")] bool? localSymsStripped = null,
			[Description("Characteristics.AggressiveWsTrim flag override.")] bool? aggressiveWsTrim = null,
			[Description("Characteristics.LargeAddressAware flag override.")] bool? largeAddressAware = null,
			[Description("Characteristics.Reserved1 flag override.")] bool? characteristicsReserved1 = null,
			[Description("Characteristics.BytesReversedLo flag override.")] bool? bytesReversedLo = null,
			[Description("Characteristics.Bit32Machine flag override.")] bool? bit32Machine = null,
			[Description("Characteristics.DebugStripped flag override.")] bool? debugStripped = null,
			[Description("Characteristics.RemovableRunFromSwap flag override.")] bool? removableRunFromSwap = null,
			[Description("Characteristics.NetRunFromSwap flag override.")] bool? netRunFromSwap = null,
			[Description("Characteristics.System flag override.")] bool? system = null,
			[Description("Characteristics.Dll flag override.")] bool? dll = null,
			[Description("Characteristics.UpSystemOnly flag override.")] bool? upSystemOnly = null,
			[Description("Characteristics.BytesReversedHi flag override.")] bool? bytesReversedHi = null,
			[Description("DllCharacteristics.Reserved1 flag override.")] bool? dllReserved1 = null,
			[Description("DllCharacteristics.Reserved2 flag override.")] bool? dllReserved2 = null,
			[Description("DllCharacteristics.Reserved3 flag override.")] bool? dllReserved3 = null,
			[Description("DllCharacteristics.Reserved4 flag override.")] bool? dllReserved4 = null,
			[Description("DllCharacteristics.Reserved5 flag override.")] bool? dllReserved5 = null,
			[Description("DllCharacteristics.HighEntropyVA flag override.")] bool? highEntropyVa = null,
			[Description("DllCharacteristics.DynamicBase flag override.")] bool? dynamicBase = null,
			[Description("DllCharacteristics.ForceIntegrity flag override.")] bool? forceIntegrity = null,
			[Description("DllCharacteristics.NxCompat flag override.")] bool? nxCompat = null,
			[Description("DllCharacteristics.NoIsolation flag override.")] bool? noIsolation = null,
			[Description("DllCharacteristics.NoSeh flag override.")] bool? noSeh = null,
			[Description("DllCharacteristics.NoBind flag override.")] bool? noBind = null,
			[Description("DllCharacteristics.AppContainer flag override.")] bool? appContainer = null,
			[Description("DllCharacteristics.WdmDriver flag override.")] bool? wdmDriver = null,
			[Description("DllCharacteristics.GuardCf flag override.")] bool? guardCf = null,
			[Description("DllCharacteristics.TerminalServerAware flag override.")] bool? terminalServerAware = null,
			[Description("Cor20 ILOnly flag override.")] bool? ilOnly = null,
			[Description("Cor20 32BitRequired flag override.")] bool? bit32Required = null,
			[Description("Cor20 ILLibrary flag override.")] bool? ilLibrary = null,
			[Description("Cor20 32BitPreferred flag override.")] bool? bit32Preferred = null,
			[Description("Cor20 TrackDebugData flag override.")] bool? trackDebugData = null,
			[Description("Cor20 StrongNameSigned flag override.")] bool? strongNameSigned = null) => EditModule(documentId, name, moduleKind, clrVersion, mvid, encId, encBaseId, entryPointKind, managedEntryPointMetadataToken, nativeEntryPointRva, runtimeVersion, tablesHeaderVersion, cor20HeaderRuntimeVersion, machine, relocsStripped, executableImage, lineNumsStripped, localSymsStripped, aggressiveWsTrim, largeAddressAware, characteristicsReserved1, bytesReversedLo, bit32Machine, debugStripped, removableRunFromSwap, netRunFromSwap, system, dll, upSystemOnly, bytesReversedHi, dllReserved1, dllReserved2, dllReserved3, dllReserved4, dllReserved5, highEntropyVa, dynamicBase, forceIntegrity, nxCompat, noIsolation, noSeh, noBind, appContainer, wdmDriver, guardCf, terminalServerAware, ilOnly, bit32Required, ilLibrary, bit32Preferred, trackDebugData, strongNameSigned);

		[McpServerTool(Name = "list_module_custom_attributes"), Description("Lists module-level custom attributes.")]
		public ModuleCustomAttributeInfo[] ListModuleCustomAttributes(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("list_module_custom_attributes", documentId, () => {
				var module = ResolveDocument(documentId).ModuleDef ?? throw new InvalidOperationException("Target document does not contain a module definition.");
				return module.CustomAttributes.Select((a, i) => ToModuleCustomAttributeInfo(i, a)).ToArray();
			});

		[McpServerTool(Name = "remove_module_custom_attribute"), Description("Removes one module-level custom attribute by index.")]
		public ModuleEditResult RemoveModuleCustomAttribute(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Attribute index from list_module_custom_attributes.")] int index) => LoggedCall("remove_module_custom_attribute", $"{documentId}#{index}", () => {
				var module = ResolveDocument(documentId).ModuleDef ?? throw new InvalidOperationException("Target document does not contain a module definition.");
				if (index < 0 || index >= module.CustomAttributes.Count)
					return CreateModuleEditFailure($"Index {index} is out of range.", module);
				module.CustomAttributes.RemoveAt(index);
				return CreateModuleEditSuccess($"Removed module custom attribute at index {index}.", module);
			});

		[McpServerTool(Name = "add_module_custom_attribute"), Description("Adds one module-level custom attribute from constructor metadata token and fixed arguments.")]
		public ModuleCustomAttributeEditResult AddModuleCustomAttribute(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Constructor metadata token, eg 0x06001234 or 0x0A001234.")] string constructorMetadataToken,
			[Description("Constructor fixed arguments as strings, in parameter order.")] string[]? fixedArguments = null) => LoggedCall("add_module_custom_attribute", documentId, () => {
				var document = ResolveDocument(documentId);
				var module = document.ModuleDef ?? throw new InvalidOperationException("Target document does not contain a module definition.");
				try {
					var ctor = ResolveMethodLikeByMetadataToken(document, constructorMetadataToken);
					if (!string.Equals(ctor.Name, ".ctor", StringComparison.Ordinal))
						return new ModuleCustomAttributeEditResult(false, $"Method '{ctor.FullName}' is not an instance constructor.", Array.Empty<CompilerLikeDiagnostic>(), null);
					var arguments = fixedArguments ?? Array.Empty<string>();
					var expectedArgCount = ctor.MethodSig?.GetParamCount() ?? 0;
					if (arguments.Length != expectedArgCount)
						return new ModuleCustomAttributeEditResult(false, $"Constructor expects {expectedArgCount} fixed argument(s), but {arguments.Length} were provided.", Array.Empty<CompilerLikeDiagnostic>(), null);
					var attribute = new CustomAttribute(ctor);
					for (int i = 0; i < expectedArgCount; i++) {
						if (!TryCreateCAArgument(ctor.MethodSig!.Params[i], arguments[i], out var caArg, out var parseError))
							return new ModuleCustomAttributeEditResult(false, parseError ?? $"Could not parse argument {i}.", Array.Empty<CompilerLikeDiagnostic>(), null);
						attribute.ConstructorArguments.Add(caArg);
					}
					module.CustomAttributes.Add(attribute);
					return new ModuleCustomAttributeEditResult(true, "Module custom attribute added.", Array.Empty<CompilerLikeDiagnostic>(), ToModuleCustomAttributeInfo(module.CustomAttributes.Count - 1, attribute));
				}
				catch (Exception ex) {
					var actual = UnwrapToolException(ex);
					return new ModuleCustomAttributeEditResult(false, actual.Message, new[] { new CompilerLikeDiagnostic("Error", "MODCA001", actual.Message) }, null);
				}
			});

		[McpServerTool(Name = "add_type_from_csharp"), Description("Adds one or more top-level types to a module by compiling C# source, similar to dnSpy's Add Class command.")]
		public AddTypeFromCSharpResult AddTypeFromCSharp(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("C# source code containing one or more top-level type declarations.")] string sourceCode) => LoggedCall("add_type_from_csharp", documentId, () => {
				var document = ResolveDocument(documentId);
				var module = document.ModuleDef ?? throw new InvalidOperationException("Target document does not contain a module definition.");
				if (string.IsNullOrWhiteSpace(sourceCode))
					return new AddTypeFromCSharpResult(false, "Source code is empty.", Array.Empty<CompilerLikeDiagnostic>(), Array.Empty<TypeInfo>(), null);

				using var compileSession = CreateAddTypeCompilerSession(module);
				var compilation = CompileAddTypeSource(module, sourceCode, compileSession);
				if (compilation.Result is null || !compilation.Result.Value.Success)
					return new AddTypeFromCSharpResult(false, "Compilation failed.", compilation.Diagnostics, Array.Empty<TypeInfo>(), null);

				var compiledResult = compilation.Result.Value;
				var import = ImportCompiledTypes(module, compiledResult.RawFile!, compiledResult.DebugFile);
				if (!import.Success)
					return new AddTypeFromCSharpResult(false, import.Message, import.Diagnostics, Array.Empty<TypeInfo>(), null);

				var addedTypes = import.AddedTypes.Select(ToTypeInfo).ToArray();
				return new AddTypeFromCSharpResult(true, $"Added {addedTypes.Length} type(s).", import.Diagnostics, addedTypes, null);
			});

		[McpServerTool(Name = "delete_type"), Description("Deletes a type from the specified document by name or metadata token.")]
		public DeleteTypeResult DeleteType(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name.")] string? typeName = null,
			[Description("Optional metadata token such as 0x02000001.")] string? metadataToken = null) => LoggedCall("delete_type", documentId, () => {
				var document = ResolveDocument(documentId);
				var type = ResolveTypeForDeletion(document, typeName, metadataToken);
				if (type.IsGlobalModuleType)
					return new DeleteTypeResult(false, "Cannot delete the global module type.", null, null);

				var typeNode = Application.Current?.Dispatcher.Invoke(() => documentTreeView.FindNode(type)) ?? documentTreeView.FindNode(type);
				var ownerList = type.DeclaringType is null ? type.Module.Types : type.DeclaringType.NestedTypes;
				bool removed;
				if (typeNode is not null && typeNode.TreeNode.Parent is not null) {
					removed = (Application.Current?.Dispatcher.Invoke(() => typeNode.TreeNode.Parent.Children.Remove(typeNode.TreeNode)) ?? typeNode.TreeNode.Parent.Children.Remove(typeNode.TreeNode)) && ownerList.Remove(type);
				}
				else {
					removed = ownerList.Remove(type);
				}
				if (!removed)
					return new DeleteTypeResult(false, $"Could not remove type '{type.FullName}'.", type.FullName, $"0x{type.MDToken.Raw:X8}");
				return new DeleteTypeResult(true, $"Deleted type '{type.FullName}'.", type.FullName, $"0x{type.MDToken.Raw:X8}");
			});

		[McpServerTool(Name = "get_method_il_body"), Description("Returns the current CIL method body, including instructions, locals, and exception handlers.")]
		public MethodIlBodyResult GetMethodIlBody(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null) => LoggedCall("get_method_il_body", $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var method = ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				return ToMethodIlBodyResult(document, method);
			});

		[McpServerTool(Name = "apply_method_il_patch"), Description("Applies structured edits to a CIL method body, including instruction insert/update/delete, locals, and exception handlers.")]
		public MethodIlPatchResult ApplyMethodIlPatch(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional keepOldMaxStack override.")] bool? keepOldMaxStack = null,
			[Description("Optional initLocals override.")] bool? initLocals = null,
			[Description("Optional max stack override.")] ushort? maxStack = null,
			[Description("Optional local signature token override.")] uint? localVarSigTok = null,
			[Description("Instruction patch operations.")] MethodIlInstructionPatchOperation[]? instructionOperations = null,
			[Description("Local variable patch operations.")] MethodIlLocalPatchOperation[]? localOperations = null,
			[Description("Exception handler patch operations.")] MethodIlExceptionHandlerPatchOperation[]? exceptionHandlerOperations = null) => LoggedCall("apply_method_il_patch", $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var method = ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				if (method.MethodBody is not CilBody body) {
					if (method.MethodBody is not null)
						return new MethodIlPatchResult(false, "Target method does not have a CIL body.", Array.Empty<CompilerLikeDiagnostic>(), null);
					body = new CilBody();
					method.Body = body;
				}

				var diagnostics = new List<CompilerLikeDiagnostic>();
				var context = new MethodIlPatchContext(document, method, body);
				try {
					if (keepOldMaxStack is not null)
						body.KeepOldMaxStack = keepOldMaxStack.Value;
					if (initLocals is not null)
						body.InitLocals = initLocals.Value;
					if (maxStack is not null)
						body.MaxStack = maxStack.Value;
					if (localVarSigTok is not null)
						body.LocalVarSigTok = localVarSigTok.Value;

					ApplyLocalOperations(context, localOperations, diagnostics, deleteOnly: false);
					ApplyInstructionOperations(context, instructionOperations, diagnostics);
					ApplyLocalOperations(context, localOperations, diagnostics, deleteOnly: true);
					ApplyExceptionHandlerOperations(context, exceptionHandlerOperations, diagnostics);
					ValidateMethodBody(context, diagnostics);

					body.UpdateInstructionOffsets();
					TryOptimizeMethodBody(body);
					body.UpdateInstructionOffsets();
					documentTabService.RefreshModifiedDocument(document);
					return new MethodIlPatchResult(!diagnostics.Any(a => string.Equals(a.Severity, "Error", StringComparison.OrdinalIgnoreCase)), diagnostics.Count == 0 ? "IL patch applied." : "IL patch applied with diagnostics.", diagnostics.ToArray(), ToMethodIlBodyResult(document, method));
				}
				catch (Exception ex) {
					var actual = UnwrapToolException(ex);
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILPATCH001", actual.Message));
					return new MethodIlPatchResult(false, actual.Message, diagnostics.ToArray(), null);
				}
			});

		[McpServerTool(Name = "edit_assembly_basic_info"), Description("Edits assembly basic metadata (name, version, culture, hash algorithm, flags, processor arch, content type, public key).")]
		public AssemblyEditResult EditAssemblyBasicInfo(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional new assembly simple name.")] string? name = null,
			[Description("Optional new culture string. Use empty for neutral culture.")] string? culture = null,
			[Description("Optional version string, eg 1.2.3.4.")] string? version = null,
			[Description("Optional hash algorithm name, eg SHA1, SHA256, None.")] string? hashAlgorithm = null,
			[Description("Optional public key as hex string (no 0x prefix).") ] string? publicKeyHex = null,
			[Description("Optional processor architecture: None, MSIL, x86, IA64, AMD64, ARM, ARM64, NoPlatform.")] string? processorArch = null,
			[Description("Optional content type: Default or WindowsRuntime.")] string? contentType = null,
			[Description("Optional PublicKey flag override.")] bool? flagPublicKey = null,
			[Description("Optional ProcessorArchSpecified flag override.")] bool? flagProcessorArchSpecified = null,
			[Description("Optional Retargetable flag override.")] bool? flagRetargetable = null,
			[Description("Optional EnableJITCompileTracking flag override.")] bool? flagEnableJitCompileTracking = null,
			[Description("Optional DisableJITCompileOptimizer flag override.")] bool? flagDisableJitCompileOptimizer = null) => LoggedCall("edit_assembly_basic_info", documentId, () => {
				var document = ResolveDocument(documentId);
				var asm = document.AssemblyDef ?? throw new InvalidOperationException($"Document '{document.Filename}' is not an assembly.");

				if (name is not null)
					asm.Name = name;
				if (culture is not null)
					asm.Culture = culture;
				if (!string.IsNullOrWhiteSpace(version)) {
					if (!Version.TryParse(version.Trim(), out var parsedVersion))
						return new AssemblyEditResult(false, $"Invalid version '{version}'. Expected format like 1.2.3.4.", null, null, null, null, null, null);
					asm.Version = parsedVersion;
				}
				if (!string.IsNullOrWhiteSpace(hashAlgorithm)) {
					if (!Enum.TryParse(hashAlgorithm.Trim(), true, out dnlib.DotNet.AssemblyHashAlgorithm parsedHashAlgorithm))
						return new AssemblyEditResult(false, $"Invalid hashAlgorithm '{hashAlgorithm}'.", null, null, null, null, null, null);
					asm.HashAlgorithm = parsedHashAlgorithm;
				}
				if (publicKeyHex is not null) {
					if (!TryParseHexBytes(publicKeyHex, out var publicKeyBytes, out var publicKeyParseError))
						return new AssemblyEditResult(false, publicKeyParseError ?? "Invalid public key hex.", null, null, null, null, null, null);
					asm.PublicKey = new PublicKey(publicKeyBytes);
				}

				var attrs = asm.Attributes;
				if (!string.IsNullOrWhiteSpace(processorArch)) {
					if (!TryParseProcessorArch(processorArch.Trim(), out var processorArchBits))
						return new AssemblyEditResult(false, $"Invalid processorArch '{processorArch}'.", null, null, null, null, null, null);
					attrs = (attrs & ~AssemblyAttributes.PA_Mask) | processorArchBits;
				}
				if (!string.IsNullOrWhiteSpace(contentType)) {
					if (!TryParseContentType(contentType.Trim(), out var contentTypeBits))
						return new AssemblyEditResult(false, $"Invalid contentType '{contentType}'.", null, null, null, null, null, null);
					attrs = (attrs & ~AssemblyAttributes.ContentType_Mask) | contentTypeBits;
				}

				attrs = UpdateFlag(attrs, AssemblyAttributes.PublicKey, flagPublicKey);
				attrs = UpdateFlag(attrs, AssemblyAttributes.PA_Specified, flagProcessorArchSpecified);
				attrs = UpdateFlag(attrs, AssemblyAttributes.Retargetable, flagRetargetable);
				attrs = UpdateFlag(attrs, AssemblyAttributes.EnableJITcompileTracking, flagEnableJitCompileTracking);
				attrs = UpdateFlag(attrs, AssemblyAttributes.DisableJITcompileOptimizer, flagDisableJitCompileOptimizer);
				asm.Attributes = attrs;

				return new AssemblyEditResult(true, "Assembly basic info updated.", asm.Name, asm.Culture, asm.Version?.ToString(), asm.HashAlgorithm.ToString(), GetProcessorArchName(asm.Attributes), GetContentTypeName(asm.Attributes));
			});

		[McpServerTool(Name = "get_assembly_settings"), Description("Returns the current editable assembly settings shown by dnSpy's Edit Assembly dialog main page.")]
		public AssemblySettingsInfoResult GetAssemblySettings(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_assembly_settings", documentId, () => {
				var document = ResolveDocument(documentId);
				var asm = document.AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				return new AssemblySettingsInfoResult(
					document.Filename,
					asm.Name,
					asm.Culture,
					asm.Version?.ToString(),
					asm.HashAlgorithm.ToString(),
					BitConverter.ToString(asm.PublicKey?.Data ?? Array.Empty<byte>()).Replace("-", string.Empty),
					GetProcessorArchName(asm.Attributes),
					GetContentTypeName(asm.Attributes),
					(asm.Attributes & AssemblyAttributes.PublicKey) != 0,
					(asm.Attributes & AssemblyAttributes.PA_Specified) != 0,
					(asm.Attributes & AssemblyAttributes.Retargetable) != 0,
					(asm.Attributes & AssemblyAttributes.EnableJITcompileTracking) != 0,
					(asm.Attributes & AssemblyAttributes.DisableJITcompileOptimizer) != 0,
					asm.CustomAttributes.Count,
					asm.DeclSecurities.Count);
			});

		[McpServerTool(Name = "update_assembly_settings"), Description("Updates the editable assembly settings shown by dnSpy's Edit Assembly dialog main page.")]
		public AssemblyEditResult UpdateAssemblySettings(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional new assembly simple name.")] string? name = null,
			[Description("Optional new culture string. Use empty for neutral culture.")] string? culture = null,
			[Description("Optional version string, eg 1.2.3.4.")] string? version = null,
			[Description("Optional hash algorithm name, eg SHA1, SHA256, None.")] string? hashAlgorithm = null,
			[Description("Optional public key as hex string (no 0x prefix).") ] string? publicKeyHex = null,
			[Description("Optional processor architecture: None, MSIL, x86, IA64, AMD64, ARM, ARM64, NoPlatform.")] string? processorArch = null,
			[Description("Optional content type: Default or WindowsRuntime.")] string? contentType = null,
			[Description("Optional PublicKey flag override.")] bool? flagPublicKey = null,
			[Description("Optional ProcessorArchSpecified flag override.")] bool? flagProcessorArchSpecified = null,
			[Description("Optional Retargetable flag override.")] bool? flagRetargetable = null,
			[Description("Optional EnableJITCompileTracking flag override.")] bool? flagEnableJitCompileTracking = null,
			[Description("Optional DisableJITCompileOptimizer flag override.")] bool? flagDisableJitCompileOptimizer = null) => EditAssemblyBasicInfo(documentId, name, culture, version, hashAlgorithm, publicKeyHex, processorArch, contentType, flagPublicKey, flagProcessorArchSpecified, flagRetargetable, flagEnableJitCompileTracking, flagDisableJitCompileOptimizer);

		[McpServerTool(Name = "list_assembly_custom_attributes"), Description("Lists assembly-level custom attributes.")]
		public AssemblyCustomAttributeInfo[] ListAssemblyCustomAttributes(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("list_assembly_custom_attributes", documentId, () => {
				var asm = ResolveDocument(documentId).AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				return asm.CustomAttributes
					.Select((a, i) => ToAssemblyCustomAttributeInfo(i, a))
					.ToArray();
			});

		[McpServerTool(Name = "remove_assembly_custom_attribute"), Description("Removes one assembly-level custom attribute by index.")]
		public AssemblyEditResult RemoveAssemblyCustomAttribute(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Attribute index from list_assembly_custom_attributes.")] int index) => LoggedCall("remove_assembly_custom_attribute", $"{documentId}#{index}", () => {
				var asm = ResolveDocument(documentId).AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				if (index < 0 || index >= asm.CustomAttributes.Count)
					return new AssemblyEditResult(false, $"Index {index} is out of range.", null, null, null, null, null, null);
				asm.CustomAttributes.RemoveAt(index);
				return new AssemblyEditResult(true, $"Removed assembly custom attribute at index {index}.", asm.Name, asm.Culture, asm.Version?.ToString(), asm.HashAlgorithm.ToString(), GetProcessorArchName(asm.Attributes), GetContentTypeName(asm.Attributes));
			});

		[McpServerTool(Name = "add_assembly_custom_attribute"), Description("Adds one assembly-level custom attribute from constructor metadata token and fixed arguments.")]
		public AssemblyCustomAttributeEditResult AddAssemblyCustomAttribute(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Constructor metadata token, eg 0x06001234.")] string constructorMetadataToken,
			[Description("Constructor fixed arguments as strings, in parameter order.")] string[]? fixedArguments = null) => LoggedCall("add_assembly_custom_attribute", documentId, () => {
				var document = ResolveDocument(documentId);
				var asm = document.AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				try {
					var ctor = ResolveMethodLikeByMetadataToken(document, constructorMetadataToken);
					if (!string.Equals(ctor.Name, ".ctor", StringComparison.Ordinal))
						return new AssemblyCustomAttributeEditResult(false, $"Method '{ctor.FullName}' is not an instance constructor.", Array.Empty<CompilerLikeDiagnostic>(), null);

					var arguments = fixedArguments ?? Array.Empty<string>();
					var expectedArgCount = ctor.MethodSig?.GetParamCount() ?? 0;
					if (arguments.Length != expectedArgCount)
						return new AssemblyCustomAttributeEditResult(false, $"Constructor expects {expectedArgCount} fixed argument(s), but {arguments.Length} were provided.", Array.Empty<CompilerLikeDiagnostic>(), null);

					var attribute = new CustomAttribute(ctor);
					for (int i = 0; i < expectedArgCount; i++) {
						var param = ctor.MethodSig!.Params[i];
						if (!TryCreateCAArgument(param, arguments[i], out var caArg, out var parseError))
							return new AssemblyCustomAttributeEditResult(false, parseError ?? $"Could not parse argument {i}.", Array.Empty<CompilerLikeDiagnostic>(), null);
						attribute.ConstructorArguments.Add(caArg);
					}

					asm.CustomAttributes.Add(attribute);
					var index = asm.CustomAttributes.Count - 1;
					return new AssemblyCustomAttributeEditResult(true, "Assembly custom attribute added.", Array.Empty<CompilerLikeDiagnostic>(), ToAssemblyCustomAttributeInfo(index, attribute));
				}
				catch (Exception ex) {
					var actual = UnwrapToolException(ex);
					return new AssemblyCustomAttributeEditResult(false, actual.Message, new[] { new CompilerLikeDiagnostic("Error", "ASMED001", actual.Message) }, null);
				}
			});

		[McpServerTool(Name = "replace_assembly_security_declarations"), Description("Replaces assembly-level security declarations (Sec Decls).")]
		public AssemblySecurityEditResult ReplaceAssemblySecurityDeclarations(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("New security declarations. Existing declarations are removed before applying these entries.")] AssemblySecurityDeclarationEdit[] declarations) => LoggedCall("replace_assembly_security_declarations", documentId, () => {
				var document = ResolveDocument(documentId);
				var asm = document.AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				var module = document.ModuleDef ?? throw new InvalidOperationException("Target document does not have module metadata.");

				asm.DeclSecurities.Clear();
				var diagnostics = new List<CompilerLikeDiagnostic>();
				foreach (var decl in declarations ?? Array.Empty<AssemblySecurityDeclarationEdit>()) {
					if (!Enum.TryParse(decl.Action, true, out SecurityAction action)) {
						diagnostics.Add(new CompilerLikeDiagnostic("Error", "ASMED_SEC001", $"Invalid security action '{decl.Action}'."));
						continue;
					}

					try {
						var user = module.UpdateRowId(new DeclSecurityUser());
						user.Action = action;
						if (!string.IsNullOrWhiteSpace(decl.Net1xXml))
							user.SecurityAttributes.Add(SecurityAttribute.CreateFromXml(module, decl.Net1xXml));
						asm.DeclSecurities.Add(user);
					}
					catch (Exception ex) {
						diagnostics.Add(new CompilerLikeDiagnostic("Error", "ASMED_SEC002", ex.Message));
					}
				}

				var success = diagnostics.All(a => !string.Equals(a.Severity, "Error", StringComparison.OrdinalIgnoreCase));
				var message = success ? "Security declarations updated." : "Security declarations updated with errors.";
				return new AssemblySecurityEditResult(success, message, diagnostics.ToArray(), asm.DeclSecurities.Count);
			});

		[McpServerTool(Name = "apply_assembly_attributes_csharp"), Description("Applies a subset of [assembly: ...] attributes from C# source text and returns diagnostics instead of throwing.")]
		public AssemblyCSharpEditResult ApplyAssemblyAttributesCSharp(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("C# source containing [assembly: ...] attribute lines.")] string source) => LoggedCall("apply_assembly_attributes_csharp", documentId, () => {
				var document = ResolveDocument(documentId);
				var asm = document.AssemblyDef ?? throw new InvalidOperationException("Target document is not an assembly.");
				var diagnostics = new List<CompilerLikeDiagnostic>();
				var appliedCount = 0;

				if (string.IsNullOrWhiteSpace(source))
					return new AssemblyCSharpEditResult(false, "Source is empty.", new[] { new CompilerLikeDiagnostic("Error", "ASMED_CS001", "Source is empty.") });

				foreach (var parsed in ParseAssemblyAttributeLines(source, diagnostics)) {
					if (ApplyKnownAssemblyAttribute(document, asm, parsed.Name, parsed.Arguments, diagnostics))
						appliedCount++;
				}

				var success = diagnostics.All(a => !string.Equals(a.Severity, "Error", StringComparison.OrdinalIgnoreCase));
				if (appliedCount == 0)
					success = false;
				var message = success
					? $"C# assembly attribute edits applied. Applied count: {appliedCount}."
					: $"C# assembly attribute edits applied with diagnostics. Applied count: {appliedCount}.";
				return new AssemblyCSharpEditResult(success, message, diagnostics.ToArray());
			});

		[McpServerTool(Name = "list_attachable_processes"), Description("Lists processes that dnSpyEx can attach to.")]
		public AttachableProcessInfo[] ListAttachableProcesses(
			[Description("Optional process names. Supports wildcards like * and ?.")] string[]? processNames = null,
			[Description("Optional process ids to match.")] int[]? processIds = null,
			[Description("Optional attach provider names. See predefined attach providers.")] string[]? providerNames = null) => LoggedCall("list_attachable_processes", string.Empty, () => {
				var attachableProcesses = GetAttachableProcessesSafe(processNames, processIds, providerNames);
				return attachableProcesses
					.Where(IsUsableAttachableProcess)
					.GroupBy(a => $"{a.ProcessId}|{a.RuntimeKindGuid}|{a.RuntimeName}", StringComparer.OrdinalIgnoreCase)
					.Select(g => g.First())
					.OrderBy(a => a.ProcessId)
					.ThenBy(a => a.RuntimeName, StringComparer.OrdinalIgnoreCase)
					.Select(ToAttachableProcessInfo)
					.ToArray();
			});

		[McpServerTool(Name = "attach_to_process"), Description("Attaches dnSpyEx to a running process.")]
		public AttachProcessResult AttachToProcess(
			[Description("Process id to attach to.")] int processId,
			[Description("Optional process name filter. Supports wildcards like * and ?.")] string? processName = null,
			[Description("Optional runtime name to disambiguate multiple candidates.")] string? runtimeName = null,
			[Description("Optional attach provider names. See predefined attach providers.")] string[]? providerNames = null) => LoggedCall("attach_to_process", processId.ToString(), () => {
				var processNames = string.IsNullOrWhiteSpace(processName) ? null : new[] { processName.Trim() };
				var attachableProcesses = GetAttachableProcessesSafe(processNames, new[] { processId }, providerNames);
				if (attachableProcesses.Length == 0)
					return new AttachProcessResult(false, $"No attachable process candidates were found for PID {processId}.", null);

				var candidates = attachableProcesses;
				if (!string.IsNullOrWhiteSpace(runtimeName)) {
					var normalizedRuntimeName = runtimeName.Trim();
					candidates = candidates.Where(a => string.Equals(a.RuntimeName, normalizedRuntimeName, StringComparison.OrdinalIgnoreCase)).ToArray();
					if (candidates.Length == 0)
						return new AttachProcessResult(false, $"No attachable process candidates matched PID {processId} and runtime '{normalizedRuntimeName}'.", attachableProcesses.Select(ToAttachableProcessInfo).ToArray());
				}

				if (candidates.Length > 1)
					return new AttachProcessResult(false, $"Multiple attach candidates matched PID {processId}. Specify runtimeName to disambiguate.", candidates.Select(ToAttachableProcessInfo).ToArray());

				var attachableProcess = candidates[0];
				var error = dbgManager.Start(attachableProcess.GetOptions());
				if (!string.IsNullOrWhiteSpace(error))
					return new AttachProcessResult(false, error, new[] { ToAttachableProcessInfo(attachableProcess) });

				return new AttachProcessResult(true, $"Attached to PID {attachableProcess.ProcessId} ({attachableProcess.RuntimeName}).", new[] { ToAttachableProcessInfo(attachableProcess) });
			});

		[McpServerTool(Name = "evaluate_expression"), Description("Evaluates an expression in the current debugger context.")]
		public EvaluateExpressionResult EvaluateExpression(
			[Description("Expression to evaluate.")] string expression,
			[Description("Optional explicit language name; if omitted, uses the current runtime language.")] string? languageName = null) => LoggedCall("evaluate_expression", expression, () => {
				if (string.IsNullOrWhiteSpace(expression))
					throw new ArgumentException("Expression must not be empty.", nameof(expression));

				var evalInfo = TryCreateEvaluationInfo(languageName, out var errorMessage, out var language, out var frame);
				if (errorMessage is not null)
					return new EvaluateExpressionResult(false, expression, language?.Name, frame?.Thread?.Process?.Id, frame?.Thread?.Id, null, null, null, errorMessage);

				var evalResult = language!.ExpressionEvaluator.Evaluate(evalInfo!, expression, DbgEvaluationOptions.Expression | DbgEvaluationOptions.NoSideEffects, null);
				var typeText = string.Empty;
				string? rawValueText = null;
				if (evalResult.Value is DbgValue value) {
					var output = new DbgStringBuilderTextWriter();
					language.Formatter.FormatType(evalInfo!, output, value, DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords, null);
					typeText = output.Text;
					var valueOutput = new DbgStringBuilderTextWriter();
					language.Formatter.FormatValue(evalInfo!, valueOutput, value, DbgValueFormatterOptions.Namespaces | DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.FullString | DbgValueFormatterOptions.NoDebuggerDisplay, null);
					rawValueText = valueOutput.Text;
				}

				return new EvaluateExpressionResult(evalResult.Value is not null, expression, language.Name, frame!.Thread.Process.Id, frame.Thread.Id, typeText, rawValueText, evalResult.Error, null);
			});

		[McpServerTool(Name = "evaluate_debug_expression"), Description("Evaluates an expression in the current debugger context.")]
		public EvaluateExpressionResult EvaluateDebugExpression(
			[Description("Expression to evaluate.")] string expression,
			[Description("Optional explicit language name; if omitted, uses the current runtime language.")] string? languageName = null) => EvaluateExpression(expression, languageName);

		[McpServerTool(Name = "list_types"), Description("Lists types from a loaded document. Use documentId from list_loaded_assemblies or load_assembly.")]
		public TypeInfo[] ListTypes(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional substring filter matched against full type name or short name.")] string? filter = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("list_types", documentId, () => {
				var document = ResolveDocument(documentId);
				var normalizedFilter = NormalizeQuery(filter);
				return document.GetModules<ModuleDef>()
					.SelectMany(a => a.GetTypes())
					.Where(a => normalizedFilter is null || Contains(a.FullName, normalizedFilter, false) || Contains(a.Name, normalizedFilter, false))
					.OrderBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
					.Take(Math.Max(1, maxResults))
					.Select(ToTypeInfo)
					.ToArray();
			});

		[McpServerTool(Name = "list_methods"), Description("Lists methods of a type from a loaded document.")]
		public MethodInfoResult[] ListMethods(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the methods.")] string typeName,
			[Description("Optional substring filter matched against method name or full name.")] string? filter = null,
			[Description("Include constructors such as .ctor and .cctor.")] bool includeConstructors = true,
			[Description("Include special-name methods such as property accessors.")] bool includeSpecialName = false,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("list_methods", $"{documentId}::{typeName}", () => {
				var type = ResolveType(ResolveDocument(documentId), typeName);
				var normalizedFilter = NormalizeQuery(filter);
				return type.Methods
					.Where(a => includeConstructors || !a.IsConstructor)
					.Where(a => includeSpecialName || !a.IsSpecialName)
					.Where(a => normalizedFilter is null || Contains(a.Name, normalizedFilter, false) || Contains(a.FullName, normalizedFilter, false))
					.OrderBy(a => a.Name.String, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.Parameters.Count)
					.Take(Math.Max(1, maxResults))
					.Select(ToMethodInfoResult)
					.ToArray();
			});

		[McpServerTool(Name = "get_entry_point_info"), Description("Returns the managed entry point method of a loaded assembly, if any.")]
		public EntryPointInfoResult GetEntryPointInfo(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_entry_point_info", documentId, () => {
				var document = ResolveDocument(documentId);
				var entryPoint = document.GetModules<ModuleDef>().Select(a => a.EntryPoint).FirstOrDefault(a => a is not null);
				if (entryPoint is null)
					return new EntryPointInfoResult(false, document.Filename, null, null, $"Document '{document.Filename}' does not have a managed entry point.");
				return new EntryPointInfoResult(true, document.Filename, entryPoint.DeclaringType?.FullName, ToMethodInfoResult(entryPoint), null);
			});

		[McpServerTool(Name = "resolve_method_for_breakpoint"), Description("Resolves the best method candidate for a breakpoint request and returns fallback details instead of failing blindly.")]
		public MethodResolutionPreviewResult ResolveMethodForBreakpointTool(
			[Description("Document identifier. File paths are auto-loaded if needed.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null) => LoggedCall("resolve_method_for_breakpoint", $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var resolution = ResolveMethodForBreakpoint(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				return new MethodResolutionPreviewResult(resolution.Method is not null, resolution.Method is null ? null : ToMethodInfoResult(resolution.Method), resolution.Message, resolution.CandidateMethods);
			});

		[McpServerTool(Name = "search_symbols"), Description("Searches loaded assemblies for matching types and members.")]
		public SearchSymbolResult[] SearchSymbols(
			[Description("Search text or regex pattern when useRegex=true.")] string query,
			[Description("Optional document identifier to limit the search scope.")] string? documentId = null,
			[Description("Optional symbol kinds to include. Supported values: type, method, field, property, event.")] string[]? symbolKinds = null,
			[Description("Whether the search should be case-sensitive.")] bool caseSensitive = false,
			[Description("Whether query should be treated as a regular expression.")] bool useRegex = false,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("search_symbols", query, () => {
				if (string.IsNullOrWhiteSpace(query))
					throw new ArgumentException("Search query must not be empty.", nameof(query));

				var regex = useRegex ? CreateRegex(query, caseSensitive) : null;
				var kinds = NormalizeSymbolKinds(symbolKinds);
				var results = new List<SearchSymbolResult>();
				foreach (var document in EnumerateDocuments(documentId)) {
					foreach (var module in document.GetModules<ModuleDef>()) {
						foreach (var type in module.GetTypes()) {
							if (kinds.Contains("type") && IsSymbolMatch(type.FullName, type.Name, query, caseSensitive, regex))
								AddSearchResult(results, maxResults, ToSearchSymbolResult(document, type));

							if (kinds.Contains("method")) {
								foreach (var method in type.Methods) {
									if (IsSymbolMatch(method.FullName, method.Name, query, caseSensitive, regex))
										AddSearchResult(results, maxResults, ToSearchSymbolResult(document, type, method));
									if (results.Count >= maxResults)
										return results.ToArray();
								}
							}

							if (kinds.Contains("field")) {
								foreach (var field in type.Fields) {
									if (IsSymbolMatch(field.FullName, field.Name, query, caseSensitive, regex))
										AddSearchResult(results, maxResults, ToSearchSymbolResult(document, type, field));
									if (results.Count >= maxResults)
										return results.ToArray();
								}
							}

							if (kinds.Contains("property")) {
								foreach (var property in type.Properties) {
									if (IsSymbolMatch(property.FullName, property.Name, query, caseSensitive, regex))
										AddSearchResult(results, maxResults, ToSearchSymbolResult(document, type, property));
									if (results.Count >= maxResults)
										return results.ToArray();
								}
							}

							if (kinds.Contains("event")) {
								foreach (var ev in type.Events) {
									if (IsSymbolMatch(ev.FullName, ev.Name, query, caseSensitive, regex))
										AddSearchResult(results, maxResults, ToSearchSymbolResult(document, type, ev));
									if (results.Count >= maxResults)
										return results.ToArray();
								}
							}
						}
					}
				}
				return results.ToArray();
			});

		[McpServerTool(Name = "get_metadata_summary"), Description("Returns a structured metadata summary for a loaded assembly or module.")]
		public MetadataSummaryResult GetMetadataSummary(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_metadata_summary", documentId, () => {
				var document = ResolveDocument(documentId);
				var modules = document.GetModules<ModuleDef>().ToArray();
				var allTypes = modules.SelectMany(a => a.GetTypes()).ToArray();
				var assembly = document.AssemblyDef;
				var primaryModule = document.ModuleDef ?? modules.FirstOrDefault();
				return new MetadataSummaryResult(
					document.Filename,
					assembly?.FullName,
					primaryModule?.FullName,
					primaryModule?.Kind.ToString(),
					GetTargetFramework(assembly, primaryModule),
					primaryModule?.EntryPoint?.FullName,
					allTypes.Length,
					allTypes.Count(a => a.IsPublic || a.IsNestedPublic),
					allTypes.Sum(a => a.Methods.Count),
					allTypes.Sum(a => a.Fields.Count),
					allTypes.Sum(a => a.Properties.Count),
					allTypes.Sum(a => a.Events.Count),
					primaryModule?.Resources.Count ?? 0,
					primaryModule?.Assembly?.Modules.Count ?? modules.Length,
					(primaryModule?.GetAssemblyRefs().Select(a => a.FullName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray()) ?? Array.Empty<string>());
			});

		[McpServerTool(Name = "get_assembly_info"), Description("Returns basic assembly information, including entry point, target framework, attributes, and references.")]
		public AssemblyInfoResult GetAssemblyInfo(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_assembly_info", documentId, () => {
				var document = ResolveDocument(documentId);
				var assembly = document.AssemblyDef;
				var primaryModule = GetPrimaryModule(document);
				var referencedAssemblies = document.GetModules<ModuleDef>()
					.SelectMany(a => a.GetAssemblyRefs())
					.Select(a => a.FullName)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				var assemblyAttributes = assembly?.CustomAttributes.Select(a => a.AttributeType.FullName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
				return new AssemblyInfoResult(
					document.Filename,
					assembly?.FullName,
					assembly?.Name ?? document.GetShortName(),
					assembly?.Version.ToString(),
					assembly?.Culture,
					assembly?.PublicKeyToken?.ToString(),
					primaryModule?.EntryPoint?.FullName,
					GetTargetFramework(assembly, primaryModule),
					document.GetModules<ModuleDef>().Count(),
					primaryModule?.Resources.Count ?? 0,
					referencedAssemblies,
					assemblyAttributes);
			});

		[McpServerTool(Name = "get_assembly_summary"), Description("Returns display-oriented assembly information, including entry point, target framework, attributes, and references.")]
		public AssemblyInfoResult GetAssemblySummary(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => GetAssemblyInfo(documentId);

		[McpServerTool(Name = "list_assembly_references"), Description("Lists the assembly references used by a loaded assembly or module.")]
		public AssemblyReferenceInfo[] ListAssemblyReferences(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("list_assembly_references", documentId, () => {
				var document = ResolveDocument(documentId);
				return document.GetModules<ModuleDef>()
					.SelectMany(a => a.GetAssemblyRefs())
					.GroupBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
					.Select(g => g.First())
					.OrderBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
					.Select(ToAssemblyReferenceInfo)
					.ToArray();
			});

		[McpServerTool(Name = "list_resources"), Description("Lists manifest resources in a loaded assembly or module.")]
		public ResourceInfoResult[] ListResources(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("list_resources", documentId, () => {
				var document = ResolveDocument(documentId);
				return document.GetModules<ModuleDef>()
					.SelectMany(module => module.Resources.Select(resource => ToResourceInfo(module, resource)))
					.OrderBy(a => a.ModuleFilename, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
					.ToArray();
			});

		[McpServerTool(Name = "export_resource"), Description("Exports an embedded manifest resource to a file path.")]
		public ResourceExportResult ExportResource(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Resource name.")] string resourceName,
			[Description("Output file path.")] string outputPath,
			[Description("Optional module identifier to disambiguate a resource name across modules.")] string? moduleId = null) => LoggedCall("export_resource", $"{documentId}::{resourceName}", () => {
				var document = ResolveDocument(documentId);
				var (module, resource) = ResolveResource(document, resourceName, moduleId);
				if (resource is not EmbeddedResource embeddedResource)
					throw new InvalidOperationException($"Resource '{resource.Name}' in module '{module.FullName}' is not an embedded resource and cannot be exported as raw bytes.");

				var fullPath = Path.GetFullPath(outputPath);
				var directory = Path.GetDirectoryName(fullPath);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				var data = embeddedResource.CreateReader().ToArray();
				File.WriteAllBytes(fullPath, data);
				return new ResourceExportResult(document.Filename, module.FullName, resource.Name, resource.ResourceType.ToString(), fullPath, true, data.Length, null);
			});

		[McpServerTool(Name = "get_pe_info"), Description("Returns PE header and section information for a loaded document.")]
		public PeInfoResult GetPeInfo(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId) => LoggedCall("get_pe_info", documentId, () => {
				var document = ResolveDocument(documentId);
				var peImage = document.PEImage;
				if (peImage is null)
					throw new InvalidOperationException($"Document '{documentId}' does not have PE image information.");

				var fileHeader = peImage.ImageNTHeaders.FileHeader;
				var optionalHeader = peImage.ImageNTHeaders.OptionalHeader;
				var sections = peImage.ImageSectionHeaders
					.Select(a => new PeSectionInfo(DecodeSectionName(a.Name), (uint)a.VirtualAddress, a.VirtualSize, a.SizeOfRawData, (uint)a.PointerToRawData, a.Characteristics.ToString()))
					.ToArray();
				var timestamp = fileHeader.TimeDateStamp == 0 || fileHeader.TimeDateStamp >= 0x80000000 ? (DateTime?)null : DateTime.SpecifyKind(new DateTime(1970, 1, 1).AddSeconds(fileHeader.TimeDateStamp), DateTimeKind.Utc);
				return new PeInfoResult(
					document.Filename,
					peImage.ImageNTHeaders.FileHeader.Machine.ToString(),
					GetArchString(document.ModuleDef),
					fileHeader.Characteristics.ToString(),
					document.ModuleDef?.IsILOnly,
					document.ModuleDef?.Is32BitRequired,
					document.ModuleDef?.Is32BitPreferred,
					document.ModuleDef?.Cor20HeaderFlags.ToString(),
					optionalHeader.ImageBase,
					optionalHeader.FileAlignment,
					optionalHeader.SectionAlignment,
					optionalHeader.SizeOfImage,
					optionalHeader.SizeOfHeaders,
					optionalHeader.Subsystem.ToString(),
					optionalHeader.Magic.ToString(),
					fileHeader.TimeDateStamp,
					timestamp,
					sections);
			});

		[McpServerTool(Name = "decompile_assembly"), Description("Decompiles a loaded assembly or module to text.")]
		public DecompiledTextResult DecompileAssembly(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Optional decompiler name, for example C#.")] string? decompilerName = null,
			[Description("Optional maximum number of characters to return. When omitted, the full text is returned.")] int? maxLength = null) => LoggedCall("decompile_assembly", documentId, () => {
				var document = ResolveDocument(documentId);
				var decompiler = ResolveDecompiler(decompilerName);
				if (document.AssemblyDef is not null)
					return ApplyTextLimit(DecompileWithFallback(decompilerName, decompiler, document.Filename, (selectedDecompiler, output) => selectedDecompiler.Decompile(document.AssemblyDef, output, CreateDecompilationContext())), maxLength);
				if (document.ModuleDef is not null)
					return ApplyTextLimit(DecompileWithFallback(decompilerName, decompiler, document.Filename, (selectedDecompiler, output) => selectedDecompiler.Decompile(document.ModuleDef, output, CreateDecompilationContext())), maxLength);
				throw new InvalidOperationException($"Document '{documentId}' is not a .NET assembly or module.");
			});

		[McpServerTool(Name = "decompile_type"), Description("Decompiles a type from a loaded assembly.")]
		public DecompiledTextResult DecompileType(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name.")] string typeName,
			[Description("Optional decompiler name, for example C#.")] string? decompilerName = null) => LoggedCall("decompile_type", $"{documentId}::{typeName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var decompiler = ResolveDecompiler(decompilerName);
				return DecompileWithFallback(decompilerName, decompiler, type.FullName, (selectedDecompiler, output) => selectedDecompiler.Decompile(type, output, CreateDecompilationContext()));
			});

		[McpServerTool(Name = "decompile_method"), Description("Decompiles a method from a loaded assembly.")]
		public DecompiledTextResult DecompileMethod(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature, for example 'System.Void System.Console::WriteLine(System.String)'.") ] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads, for example ['System.String'].")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional decompiler name, for example C#.")] string? decompilerName = null) => LoggedCall("decompile_method", $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var method = ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				var decompiler = ResolveDecompiler(decompilerName);
				return DecompileWithFallback(decompilerName, decompiler, method.FullName, (selectedDecompiler, output) => selectedDecompiler.Decompile(method, output, CreateDecompilationContext()));
			});

		[McpServerTool(Name = "find_method_usages"), Description("Alias of find_callers. Finds methods that reference a target method.")]
		public UsageLocationInfo[] FindMethodUsages(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindMethodUsagesCore("find_callers", documentId, typeName, methodName, metadataToken, methodSignature, parameterTypes, parameterCount, searchDocumentId, maxResults);

		[McpServerTool(Name = "get_method_uses"), Description("Alias of find_callees. Lists methods, fields, and types referenced by a target method.")]
		public DependencyInfo[] GetMethodUses(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => GetMethodUsesCore("find_callees", documentId, typeName, methodName, metadataToken, methodSignature, parameterTypes, parameterCount, maxResults);

		[McpServerTool(Name = "find_type_usages"), Description("Finds types, fields, and methods that reference a target type.")]
		public UsageLocationInfo[] FindTypeUsages(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name.")] string typeName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("find_type_usages", $"{documentId}::{typeName}", () => {
				var document = ResolveDocument(documentId);
				var targetType = ResolveType(document, typeName);
				var results = new List<UsageLocationInfo>();

				foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
					foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
						if (TypeReferencesTarget(candidateType.BaseType, targetType) || candidateType.Interfaces.Any(a => TypeReferencesTarget(a.Interface, targetType))) {
							results.Add(CreateTypeUsageLocationInfo(searchDocument, candidateType, "type-definition"));
							if (results.Count >= Math.Max(1, maxResults))
								return results.ToArray();
						}

						foreach (var field in candidateType.Fields) {
							if (!TypeReferencesTarget(field.FieldType, targetType))
								continue;
							results.Add(CreateFieldUsageLocationInfo(searchDocument, candidateType, field, "field-signature"));
							if (results.Count >= Math.Max(1, maxResults))
								return results.ToArray();
						}

						foreach (var candidateMethod in candidateType.Methods) {
							if (!TryFindTypeUsageInMethod(candidateMethod, targetType, out var usageKind, out var ilOffset, out var opCode))
								continue;
							results.Add(CreateUsageLocationInfo(searchDocument, candidateType, candidateMethod, usageKind, ilOffset, opCode));
							if (results.Count >= Math.Max(1, maxResults))
								return results.ToArray();
						}
					}
				}

				return results.ToArray();
			});

		[McpServerTool(Name = "find_callers"), Description("Finds methods that call a target method.")]
		public UsageLocationInfo[] FindCallers(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindMethodUsages(documentId, typeName, methodName, metadataToken, methodSignature, parameterTypes, parameterCount, searchDocumentId, maxResults);

		[McpServerTool(Name = "find_callees"), Description("Finds methods, fields, and types used by a target method.")]
		public DependencyInfo[] FindCallees(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => GetMethodUses(documentId, typeName, methodName, metadataToken, methodSignature, parameterTypes, parameterCount, maxResults);

		[McpServerTool(Name = "find_field_reads"), Description("Finds methods that read a target field.")]
		public UsageLocationInfo[] FindFieldReads(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the field.")] string typeName,
			[Description("Field name.")] string fieldName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindFieldAccesses(documentId, typeName, fieldName, showWrites: false, searchDocumentId, maxResults);

		[McpServerTool(Name = "find_field_writes"), Description("Finds methods that write a target field.")]
		public UsageLocationInfo[] FindFieldWrites(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the field.")] string typeName,
			[Description("Field name.")] string fieldName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindFieldAccesses(documentId, typeName, fieldName, showWrites: true, searchDocumentId, maxResults);

		[McpServerTool(Name = "find_property_reads"), Description("Finds methods that read a target property.")]
		public UsageLocationInfo[] FindPropertyReads(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the property.")] string typeName,
			[Description("Property name.")] string propertyName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindPropertyAccesses(documentId, typeName, propertyName, isSetter: false, searchDocumentId, maxResults);

		[McpServerTool(Name = "find_property_writes"), Description("Finds methods that write a target property.")]
		public UsageLocationInfo[] FindPropertyWrites(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the property.")] string typeName,
			[Description("Property name.")] string propertyName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => FindPropertyAccesses(documentId, typeName, propertyName, isSetter: true, searchDocumentId, maxResults);

		[McpServerTool(Name = "find_base_types"), Description("Finds the base type chain for a type. If documentId is wrong, the tool falls back to other loaded documents.")]
		public TypeRelationInfo[] FindBaseTypes(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name.")] string typeName,
			[Description("Maximum number of results to return.")] int maxResults = 20) => LoggedCall("find_base_types", $"{documentId}::{typeName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var results = new List<TypeRelationInfo>();
				var current = type.BaseType;
				var distance = 1;
				while (current is not null && results.Count < Math.Max(1, maxResults)) {
					var resolved = current.ResolveTypeDef();
					results.Add(new TypeRelationInfo(document.Filename, type.FullName, resolved?.FullName ?? current.FullName, "base-type", distance, resolved is null ? null : $"0x{resolved.MDToken.Raw:X8}"));
					current = resolved?.BaseType;
					distance++;
				}
				return results.ToArray();
			});

		[McpServerTool(Name = "find_derived_types"), Description("Finds types derived from a target type. Prefer the assembly that defines the base type, but the tool can fall back across loaded documents.")]
		public TypeRelationInfo[] FindDerivedTypes(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name.")] string typeName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("find_derived_types", $"{documentId}::{typeName}", () => {
				var document = ResolveDocument(documentId);
				var targetType = ResolveType(document, typeName);
				var results = new List<TypeRelationInfo>();
				foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
					foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
						if (candidateType == targetType)
							continue;
						if (!TypesHierarchyHelpers.IsBaseType(targetType, candidateType, resolveTypeArguments: false))
							continue;
						results.Add(new TypeRelationInfo(searchDocument.Filename, candidateType.FullName, targetType.FullName, "derived-type", 1, $"0x{candidateType.MDToken.Raw:X8}"));
						if (results.Count >= Math.Max(1, maxResults))
							return results.ToArray();
					}
				}
				return results.ToArray();
			});

		[McpServerTool(Name = "find_interface_implementations"), Description("Finds types that implement a target interface. Prefer the assembly that defines the interface, but the tool can fall back across loaded documents.")]
		public TypeRelationInfo[] FindInterfaceImplementations(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Interface type full name or short name.")] string interfaceTypeName,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("find_interface_implementations", $"{documentId}::{interfaceTypeName}", () => {
				var document = ResolveDocument(documentId);
				var interfaceType = ResolveType(document, interfaceTypeName);
				if (!interfaceType.IsInterface)
					throw new InvalidOperationException($"Type '{interfaceType.FullName}' is not an interface.");

				var results = new List<TypeRelationInfo>();
				foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
					foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
						if (candidateType.IsInterface)
							continue;
						if (!TypeImplementsInterface(candidateType, interfaceType))
							continue;
						results.Add(new TypeRelationInfo(searchDocument.Filename, candidateType.FullName, interfaceType.FullName, "interface-implementation", 1, $"0x{candidateType.MDToken.Raw:X8}"));
						if (results.Count >= Math.Max(1, maxResults))
							return results.ToArray();
					}
				}
				return results.ToArray();
			});

		[McpServerTool(Name = "find_interface_method_implementations"), Description("Finds methods that implement a target interface method. Prefer the assembly that defines the interface, but the tool can fall back across loaded documents.")]
		public UsageLocationInfo[] FindInterfaceMethodImplementations(
			[Description("Document identifier. Prefer the Filename returned by list_loaded_assemblies or load_assembly.")] string documentId,
			[Description("Type full name or short name that owns the interface method.")] string interfaceTypeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional document identifier to limit the search scope. When omitted, all loaded documents are searched.")] string? searchDocumentId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("find_interface_method_implementations", $"{documentId}::{interfaceTypeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var interfaceType = ResolveType(document, interfaceTypeName);
				if (!interfaceType.IsInterface)
					throw new InvalidOperationException($"Type '{interfaceType.FullName}' is not an interface.");

				var interfaceMethod = ResolveMethod(document, interfaceType, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				var results = new List<UsageLocationInfo>();
				foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
					foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
						if (candidateType.IsInterface)
							continue;
						var implementedInterfaceRef = GetImplementedInterface(candidateType, interfaceType);
						if (implementedInterfaceRef is null)
							continue;

						foreach (var candidateMethod in candidateType.Methods) {
							if (!candidateMethod.IsVirtual && !candidateMethod.IsStatic)
								continue;
							if (candidateMethod.IsAbstract)
								continue;
							if (candidateMethod.Name != interfaceMethod.Name)
								continue;
							if (!TypesHierarchyHelpers.MatchInterfaceMethod(candidateMethod, interfaceMethod, implementedInterfaceRef))
								continue;

							results.Add(CreateUsageLocationInfo(searchDocument, candidateType, candidateMethod, "interface-implementation", null, null));
							if (results.Count >= Math.Max(1, maxResults))
								return results.ToArray();
						}
					}
				}
				return results.ToArray();
			});

		UsageLocationInfo[] FindMethodUsagesCore(string toolName, string documentId, string typeName, string methodName, string? metadataToken, string? methodSignature, string[]? parameterTypes, int? parameterCount, string? searchDocumentId, int maxResults) => LoggedCall(toolName, $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var method = ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				var results = new List<UsageLocationInfo>();

				foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
					foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
						foreach (var candidateMethod in candidateType.Methods) {
							if (!candidateMethod.HasBody)
								continue;

							foreach (var instruction in candidateMethod.Body.Instructions) {
								if (instruction.Operand is not IMethod methodRef || methodRef.IsField)
									continue;
								if (!MethodReferencesTarget(method, methodRef))
									continue;

								results.Add(CreateUsageLocationInfo(searchDocument, candidateType, candidateMethod, toolName, instruction.Offset, instruction.OpCode.Name));
								if (results.Count >= Math.Max(1, maxResults))
									return results.ToArray();
							}
						}
					}
				}

				return results.ToArray();
			});

		DependencyInfo[] GetMethodUsesCore(string toolName, string documentId, string typeName, string methodName, string? metadataToken, string? methodSignature, string[]? parameterTypes, int? parameterCount, int maxResults) => LoggedCall(toolName, $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var method = ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				var results = new List<DependencyInfo>();
				var seen = new HashSet<string>(StringComparer.Ordinal);

				if (!method.HasBody)
					return Array.Empty<DependencyInfo>();

				foreach (var instruction in method.Body.Instructions) {
					if (instruction.Operand is IMethod methodRef && !methodRef.IsField)
						AddDependency(results, seen, CreateDependencyInfoFromMethodRef(methodRef, instruction.Offset, instruction.OpCode.Name), maxResults);
					else if (instruction.Operand is IField fieldRef && !fieldRef.IsMethod)
						AddDependency(results, seen, CreateDependencyInfoFromFieldRef(fieldRef, instruction.Offset, instruction.OpCode.Name), maxResults);
					else if (instruction.Operand is ITypeDefOrRef typeRef)
						AddDependency(results, seen, CreateDependencyInfoFromTypeRef(typeRef, instruction.Offset, instruction.OpCode.Name), maxResults);

					if (results.Count >= Math.Max(1, maxResults))
						return results.ToArray();
				}

				return results.ToArray();
			});

		[McpServerTool(Name = "list_settings_sections"), Description("Lists persisted dnSpyEx settings sections.")]
		public SettingsSectionInfo[] ListSettingsSections(
			[Description("Optional substring filter matched against section path or name.")] string? filter = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("list_settings_sections", filter ?? string.Empty, () => {
				var normalizedFilter = NormalizeQuery(filter);
				return EnumerateSectionInfos(settingsService.Sections, string.Empty, 0)
					.Where(a => normalizedFilter is null || Contains(a.Path, normalizedFilter, false) || Contains(a.Name, normalizedFilter, false))
					.Take(Math.Max(1, maxResults))
					.ToArray();
			});

		[McpServerTool(Name = "get_settings_section"), Description("Reads a settings section and its children.")]
		public SettingsSectionData GetSettingsSection(
			[Description("Section path from list_settings_sections.")] string sectionPath,
			[Description("Maximum recursion depth when returning child sections.")] int maxDepth = 3) => LoggedCall("get_settings_section", sectionPath, () => {
				if (string.IsNullOrWhiteSpace(sectionPath))
					throw new ArgumentException("Section path must not be empty.", nameof(sectionPath));
				var section = ResolveSectionPath(sectionPath);
				return ToSettingsSectionData(section, sectionPath, 0, Math.Max(0, maxDepth));
			});

		[McpServerTool(Name = "search_settings"), Description("Searches settings section paths, attribute names, and values.")]
		public SettingsSearchResult[] SearchSettings(
			[Description("Search text or regex pattern when useRegex=true.")] string query,
			[Description("Whether the search should be case-sensitive.")] bool caseSensitive = false,
			[Description("Whether query should be treated as a regular expression.")] bool useRegex = false,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("search_settings", query, () => {
				if (string.IsNullOrWhiteSpace(query))
					throw new ArgumentException("Search query must not be empty.", nameof(query));
				var regex = useRegex ? CreateRegex(query, caseSensitive) : null;
				var results = new List<SettingsSearchResult>();
				foreach (var item in EnumerateSettingsValues(settingsService.Sections, string.Empty)) {
					if (!IsMatch(item.SectionPath, query, caseSensitive, regex) &&
						!IsMatch(item.AttributeName, query, caseSensitive, regex) &&
						!IsMatch(item.AttributeValue, query, caseSensitive, regex))
						continue;
					results.Add(item);
					if (results.Count >= Math.Max(1, maxResults))
						break;
				}
				return results.ToArray();
			});

		[McpServerTool(Name = "debug_session_status"), Description("Returns the current debugger session status, including current process, thread, and active frame info.")]
		public DebugSessionStatusResult GetDebugSessionStatus() => LoggedCall("debug_session_status", string.Empty, () => {
			var currentProcess = dbgManager.CurrentProcess.Current;
			var currentThread = dbgManager.CurrentThread.Current;
			var activeFrame = dbgCallStackService.ActiveFrame;
			return new DebugSessionStatusResult(
				dbgManager.IsDebugging,
				dbgManager.IsRunning,
				dbgManager.Processes.Length,
				currentProcess is null ? null : ToDebugProcessInfo(currentProcess),
				currentThread is null ? null : ToDebugThreadInfo(currentThread),
				activeFrame is null ? null : ToCallStackFrameInfo(activeFrame),
				dbgCallStackService.ActiveFrameIndex,
				dbgCallStackService.Frames.Frames.Count,
				dbgCallStackService.Frames.FramesTruncated);
		});

		[McpServerTool(Name = "get_debug_session_status"), Description("Returns the current debugger session status, including current process, thread, and active frame info.")]
		public DebugSessionStatusResult GetDebugSessionStatusAlias() => GetDebugSessionStatus();

		[McpServerTool(Name = "list_debug_processes"), Description("Lists all processes in the current debugger session.")]
		public DebugProcessInfo[] ListDebugProcesses() => LoggedCall("list_debug_processes", string.Empty, () =>
			dbgManager.Processes.Select(ToDebugProcessInfo).ToArray());

		[McpServerTool(Name = "list_debugger_processes"), Description("Lists all processes in the current debugger session.")]
		public DebugProcessInfo[] ListDebuggerProcesses() => ListDebugProcesses();

		[McpServerTool(Name = "list_debug_modules"), Description("Lists currently loaded runtime modules in the active debugger session.")]
		public DebugModuleInfo[] ListDebugModules(
			[Description("Optional process id to limit the results.")] int? processId = null,
			[Description("Optional substring matched against module name, filename, or app domain.")] string? filter = null,
			[Description("Maximum number of results to return.")] int maxResults = 500) => LoggedCall("list_debug_modules", processId?.ToString() ?? string.Empty, () => {
				var cacheKey = $"{processId?.ToString() ?? string.Empty}|{filter ?? string.Empty}|{Math.Max(1, maxResults)}";
				lock (debugModulesCacheLock) {
					if (string.Equals(lastDebugModulesCacheKey, cacheKey, StringComparison.Ordinal) &&
						lastDebugModulesCacheValue is not null &&
						(DateTime.UtcNow - lastDebugModulesCacheUtc).TotalMilliseconds <= 750)
						return lastDebugModulesCacheValue;
				}

				var normalizedFilter = NormalizeQuery(filter);
				var modules = EnumerateProcesses(processId)
					.SelectMany(a => a.Runtimes)
					.SelectMany(a => a.Modules)
					.Where(a => normalizedFilter is null || Contains(a.Name, normalizedFilter, false) || Contains(a.Filename, normalizedFilter, false) || Contains(a.AppDomain?.Name, normalizedFilter, false))
					.OrderBy(a => a.Process.Id)
					.ThenBy(a => a.Runtime.Name, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.Order)
					.Take(Math.Max(1, maxResults))
					.Select(ToDebugModuleInfo)
					.ToArray();
				lock (debugModulesCacheLock) {
					lastDebugModulesCacheKey = cacheKey;
					lastDebugModulesCacheUtc = DateTime.UtcNow;
					lastDebugModulesCacheValue = modules;
				}
				return modules;
			});

		[McpServerTool(Name = "get_default_debug_environment"), Description("Returns the default environment variables used when launching a debuggee.")]
		public DebugEnvironmentEntry[] GetDefaultDebugEnvironment(
			[Description("Optional substring filter matched against variable name or value.")] string? filter = null,
			[Description("Maximum number of results to return.")] int maxResults = 500) => LoggedCall("get_default_debug_environment", filter ?? string.Empty, () => {
				var normalizedFilter = NormalizeQuery(filter);
				return new DbgEnvironment().Environment
					.Where(a => normalizedFilter is null || Contains(a.Key, normalizedFilter, false) || Contains(a.Value, normalizedFilter, false))
					.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
					.Take(Math.Max(1, maxResults))
					.Select(a => new DebugEnvironmentEntry(a.Key, a.Value))
					.ToArray();
			});

		[McpServerTool(Name = "get_recent_log_messages"), Description("Returns recent MCP and debugger log messages captured by the embedded server.")]
		public LogMessageInfo[] GetRecentLogMessages(
			[Description("Optional log level filter, for example info or error.")] string? level = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("get_recent_log_messages", level ?? string.Empty, () =>
				logger.GetEntries(level, maxResults).Select(a => new LogMessageInfo(a.TimestampUtc, a.Level, a.Message)).ToArray());

		[McpServerTool(Name = "clear_debug_events"), Description("Clears buffered debugger events and output lines captured by the MCP server.")]
		public ClearDebugEventsResult ClearDebugEvents() => LoggedBackgroundCall("clear_debug_events", string.Empty, () =>
			new ClearDebugEventsResult(mcpServerController.ClearDebugEvents()));

		[McpServerTool(Name = "get_recent_debug_events"), Description("Returns recent structured debugger events such as module loads, exceptions, output, and entry point breaks.")]
		public DebugEventInfo[] GetRecentDebugEvents(
			[Description("Optional event kinds to include, for example ['module-loaded', 'exception-thrown'].")] string[]? eventKinds = null,
			[Description("Only events with a sequence greater than this value are returned.")] long? afterSequence = null,
			[Description("Optional process id to limit the results.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedBackgroundCall("get_recent_debug_events", string.Join(",", eventKinds ?? Array.Empty<string>()), () =>
				mcpServerController.GetRecentDebugEvents(eventKinds, maxResults, afterSequence, processId).Select(ToDebugEventInfo).ToArray());

		[McpServerTool(Name = "get_recent_debugger_events"), Description("Returns recent structured debugger events such as module loads, exceptions, output, and entry point breaks.")]
		public DebugEventInfo[] GetRecentDebuggerEvents(
			[Description("Optional event kinds to include, for example ['module-loaded', 'exception-thrown'].")] string[]? eventKinds = null,
			[Description("Only events with a sequence greater than this value are returned.")] long? afterSequence = null,
			[Description("Optional process id to limit the results.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => GetRecentDebugEvents(eventKinds, afterSequence, processId, maxResults);

		[McpServerTool(Name = "get_debug_output"), Description("Returns recent debugger output lines, including module loads, program output, and process exit messages.")]
		public DebugOutputLine[] GetDebugOutput(
			[Description("Only output lines with a sequence greater than this value are returned.")] long? afterSequence = null,
			[Description("Optional process id to filter output.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedBackgroundCall("get_debug_output", processId?.ToString() ?? string.Empty, () =>
				mcpServerController.GetRecentDebugOutput(maxResults, processId, afterSequence).Select(a => new DebugOutputLine(a.Sequence, a.TimestampUtc, a.Message, a.ProcessId, a.ProcessName, a.ProcessFilename)).ToArray());

		[McpServerTool(Name = "get_debugger_output"), Description("Returns recent debugger output lines, including module loads, program output, and process exit messages.")]
		public DebugOutputLine[] GetDebuggerOutput(
			[Description("Only output lines with a sequence greater than this value are returned.")] long? afterSequence = null,
			[Description("Optional process id to filter output.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => GetDebugOutput(afterSequence, processId, maxResults);

		[McpServerTool(Name = "wait_for_debug_event"), Description("Waits for the next debugger event after an optional sequence number.")]
		public DebugEventWaitResult WaitForDebugEvent(
			[Description("Optional event kinds to include, for example ['entry-point-break', 'exception-thrown'].")] string[]? eventKinds = null,
			[Description("Only events with a sequence greater than this value are considered.")] long? afterSequence = null,
			[Description("Optional process id to limit the wait to one process.")] int? processId = null,
			[Description("Timeout in milliseconds. Use -1 to wait indefinitely.")] int timeoutMilliseconds = 30000) => LoggedBackgroundCall("wait_for_debug_event", afterSequence?.ToString() ?? string.Empty, () => {
				var entry = mcpServerController.WaitForDebugEvent(eventKinds, afterSequence, timeoutMilliseconds, processId);
				return new DebugEventWaitResult(entry is null, entry is null ? null : ToDebugEventInfo(entry), entry?.Sequence ?? afterSequence ?? 0);
			});

		[McpServerTool(Name = "wait_for_debug_output"), Description("Waits for the next debugger output line, such as module load output, stdout/stderr, or process exit text.")]
		public DebugEventWaitResult WaitForDebugOutput(
			[Description("Only output lines with a sequence greater than this value are considered.")] long? afterSequence = null,
			[Description("Optional process id to limit the wait to one process.")] int? processId = null,
			[Description("Timeout in milliseconds. Use -1 to wait indefinitely.")] int timeoutMilliseconds = 30000) => LoggedBackgroundCall("wait_for_debug_output", afterSequence?.ToString() ?? string.Empty, () => {
				var entry = mcpServerController.WaitForDebugEvent(eventKinds: null, afterSequence, timeoutMilliseconds, processId, outputOnly: true);
				return new DebugEventWaitResult(entry is null, entry is null ? null : ToDebugEventInfo(entry), entry?.Sequence ?? afterSequence ?? 0);
			});

		[McpServerTool(Name = "start_debugging"), Description("Starts debugging a program with custom launch options similar to dnSpyEx's Debug Program dialog.")]
		public StartDebuggingResult StartDebugging(
			[Description("Executable or assembly to debug.")] string filename,
			[Description("Debug engine: .NET, .NET Framework, or Mono.")] string engine = ".NET",
			[Description("Program arguments passed to the debuggee.")] string? arguments = null,
			[Description("Working directory. Defaults to the debuggee directory.")] string? workingDirectory = null,
			[Description("Where to break: DontBreak, CreateProcess, ModuleCctorOrEntryPoint, or EntryPoint.")] string? breakKind = null,
			[Description("Whether to inherit dnSpyEx's current environment variables.")] bool inheritEnvironment = true,
			[Description("Environment variables to add or update.")] DebugEnvironmentEntry[]? environmentVariables = null,
			[Description("Environment variable names to remove from the inherited environment.")] string[]? removeEnvironmentVariables = null,
			[Description("Use host executable when engine is .NET.")] bool useHostExecutable = true,
			[Description("Optional host executable path, such as dotnet.exe.")] string? host = null,
			[Description("Optional host arguments, such as exec.")] string? hostArguments = null,
			[Description("Connection timeout in seconds for .NET or Mono startup.")] double? timeoutSeconds = null,
			[Description("Optional .NET Framework CLR version, such as v4.0.30319.")] string? debuggeeVersion = null,
			[Description("Optional Mono executable path.")] string? monoExePath = null,
			[Description("Optional Mono connection port. Use 0 for random.")] ushort? monoConnectionPort = null) => LoggedCall("start_debugging", filename, () => {
				if (string.IsNullOrWhiteSpace(filename))
					throw new ArgumentException("Filename must not be empty.", nameof(filename));

				var fullPath = Path.GetFullPath(filename);
				if (!File.Exists(fullPath))
					return new StartDebuggingResult(false, $"Debuggee not found: {fullPath}", null, fullPath, null, null, null);

				var normalizedEngine = NormalizeDebugEngine(engine);
				var normalizedLaunchPath = NormalizeDebuggeePath(normalizedEngine, fullPath, useHostExecutable, out var normalizationError);
				if (normalizationError is not null)
					return new StartDebuggingResult(false, normalizationError, normalizedEngine, fullPath, null, normalizedLaunchPath, null);
				var normalizationMessage = !string.Equals(fullPath, normalizedLaunchPath, StringComparison.OrdinalIgnoreCase) ? $"Normalized launch target from '{fullPath}' to '{normalizedLaunchPath}'." : null;

				var options = CreateStartDebuggingOptions(normalizedEngine, normalizedLaunchPath!, arguments, workingDirectory, breakKind, inheritEnvironment, environmentVariables, removeEnvironmentVariables, useHostExecutable, host, hostArguments, timeoutSeconds, debuggeeVersion, monoExePath, monoConnectionPort);
				var error = dbgManager.Start(options);
				if (!string.IsNullOrWhiteSpace(error))
					return new StartDebuggingResult(false, error, normalizedEngine, fullPath, null, normalizedLaunchPath, normalizationMessage);

				var currentProcess = dbgManager.CurrentProcess.Current ?? dbgManager.Processes.FirstOrDefault();
				return new StartDebuggingResult(true, null, normalizedEngine, fullPath, currentProcess is null ? null : ToDebugProcessInfo(currentProcess), normalizedLaunchPath, normalizationMessage);
			});

		[McpServerTool(Name = "list_threads"), Description("Lists threads in the current debugger session or in a specific process.")]
		public DebugThreadInfo[] ListThreads(
			[Description("Optional process id to limit the results.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => LoggedCall("list_threads", processId?.ToString() ?? string.Empty, () => {
				var threads = EnumerateProcesses(processId)
					.SelectMany(a => a.Threads)
					.OrderBy(a => a.Process.Id)
					.ThenBy(a => a.Id)
					.Take(Math.Max(1, maxResults))
					.Select(ToDebugThreadInfo)
					.ToArray();
				return threads;
			});

		[McpServerTool(Name = "list_debug_threads"), Description("Lists threads in the current debugger session or in a specific debugged process.")]
		public DebugThreadInfo[] ListDebugThreads(
			[Description("Optional process id to limit the results.")] int? processId = null,
			[Description("Maximum number of results to return.")] int maxResults = 200) => ListThreads(processId, maxResults);

		[McpServerTool(Name = "set_current_thread"), Description("Sets the current debug thread, which also changes the thread used by expression evaluation and call stack navigation.")]
		public DebugContextSelectionResult SetCurrentThread(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null) => LoggedCall("set_current_thread", $"pid={processId},tid={threadId},managedTid={managedThreadId}", () => {
				var thread = ResolveDebugThread(processId, threadId, managedThreadId);
				if (thread is null)
					return new DebugContextSelectionResult(false, "No matching debug thread is available.", null, null, null, null, null);
				dbgManager.CurrentThread.Current = thread;
				var activeFrame = dbgCallStackService.ActiveFrame;
				return new DebugContextSelectionResult(true, $"Current thread set to {thread.UIName}.", ToDebugThreadInfo(thread), activeFrame is null ? null : ToCallStackFrameInfo(activeFrame), dbgCallStackService.ActiveFrameIndex, dbgCallStackService.Frames.FramesTruncated, null);
			});

		[McpServerTool(Name = "get_call_stack"), Description("Gets stack frames for the current thread or a specified thread.")]
		public CallStackResult GetCallStack(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("Maximum number of frames to return when reading a non-active thread.")] int maxFrames = 64) => LoggedCall("get_call_stack", $"pid={processId},tid={threadId},managedTid={managedThreadId}", () => {
				var thread = ResolveDebugThread(processId, threadId, managedThreadId);
				if (thread is null)
					return new CallStackResult(
						new DebugThreadInfo(0, 0, null, string.Empty, string.Empty, string.Empty, false, 0, Array.Empty<string>(), null, null),
						-1,
						false,
						Array.Empty<CallStackFrameInfo>(),
						"No active debug thread is available.");

				if (dbgCallStackService.Thread == thread) {
					var framesInfo = dbgCallStackService.Frames;
					return new CallStackResult(
						ToDebugThreadInfo(thread),
						framesInfo.ActiveFrameIndex,
						framesInfo.FramesTruncated,
						framesInfo.Frames.Select(ToCallStackFrameInfo).ToArray());
				}

				var frames = thread.GetFrames(Math.Max(1, maxFrames));
				return new CallStackResult(
					ToDebugThreadInfo(thread),
					frames.Length == 0 ? -1 : 0,
					false,
					frames.Select(ToCallStackFrameInfo).ToArray());
			});

		[McpServerTool(Name = "get_debug_call_stack"), Description("Gets stack frames for the current debug thread or a specified debug thread.")]
		public CallStackResult GetDebugCallStack(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("Maximum number of frames to return when reading a non-active thread.")] int maxFrames = 64) => GetCallStack(processId, threadId, managedThreadId, maxFrames);

		[McpServerTool(Name = "set_active_call_stack_frame"), Description("Sets the active call stack frame by index. Optionally switches to another thread first.")]
		public DebugContextSelectionResult SetActiveCallStackFrame(
			[Description("Target frame index in the currently visible call stack.")] int frameIndex,
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id. If provided and different from the current thread, dnSpy will switch threads first.")] ulong? threadId = null,
			[Description("Optional managed thread id. If provided and different from the current thread, dnSpy will switch threads first.")] ulong? managedThreadId = null) => LoggedCall("set_active_call_stack_frame", $"frame={frameIndex},pid={processId},tid={threadId},managedTid={managedThreadId}", () => {
				if (frameIndex < 0)
					throw new InvalidOperationException("frameIndex must be >= 0.");
				var targetThread = ResolveDebugThread(processId, threadId, managedThreadId);
				if (targetThread is not null && dbgManager.CurrentThread.Current != targetThread)
					dbgManager.CurrentThread.Current = targetThread;
				var currentThread = dbgManager.CurrentThread.Current ?? dbgCallStackService.Thread;
				if (currentThread is null)
					return new DebugContextSelectionResult(false, "No active debug thread is available.", null, null, null, null, null);
				var framesInfo = dbgCallStackService.Frames;
				if ((uint)frameIndex >= (uint)framesInfo.Frames.Count)
					throw new InvalidOperationException($"Frame index {frameIndex} is out of range. Visible frame count: {framesInfo.Frames.Count}.");
				dbgCallStackService.ActiveFrameIndex = frameIndex;
				var activeFrame = dbgCallStackService.ActiveFrame;
				return new DebugContextSelectionResult(true, $"Active frame set to index {frameIndex}.", ToDebugThreadInfo(currentThread), activeFrame is null ? null : ToCallStackFrameInfo(activeFrame), dbgCallStackService.ActiveFrameIndex, dbgCallStackService.Frames.FramesTruncated, null);
			});

		[McpServerTool(Name = "break_all"), Description("Pauses all debugged processes.")]
		public DebugControlResult BreakAll() => LoggedCall("break_all", string.Empty, () => {
			if (!dbgManager.IsDebugging)
				throw new InvalidOperationException("No active debugger session.");
			dbgManager.BreakAll();
			return new DebugControlResult("break_all", "requested", dbgManager.IsDebugging, dbgManager.IsRunning);
		});

		[McpServerTool(Name = "pause_debugged_processes"), Description("Pauses all debugged processes.")]
		public DebugControlResult PauseDebuggedProcesses() => BreakAll();

		[McpServerTool(Name = "step_into"), Description("Single-steps into the next statement on the current or specified thread.")]
		public StepOperationResult StepInto(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => LoggedCall("step_into", $"pid={processId},tid={threadId},managedTid={managedThreadId}", () =>
				StepThread(processId, threadId, managedThreadId, singleProcessOnly ? DbgStepKind.StepIntoProcess : DbgStepKind.StepInto, "step_into"));

		[McpServerTool(Name = "step_debug_thread_into"), Description("Single-steps into the next statement on the current or specified debug thread.")]
		public StepOperationResult StepDebugThreadInto(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => StepInto(processId, threadId, managedThreadId, singleProcessOnly);

		[McpServerTool(Name = "step_over"), Description("Single-steps over the next statement on the current or specified thread.")]
		public StepOperationResult StepOver(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => LoggedCall("step_over", $"pid={processId},tid={threadId},managedTid={managedThreadId}", () =>
				StepThread(processId, threadId, managedThreadId, singleProcessOnly ? DbgStepKind.StepOverProcess : DbgStepKind.StepOver, "step_over"));

		[McpServerTool(Name = "step_debug_thread_over"), Description("Single-steps over the next statement on the current or specified debug thread.")]
		public StepOperationResult StepDebugThreadOver(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => StepOver(processId, threadId, managedThreadId, singleProcessOnly);

		[McpServerTool(Name = "step_out"), Description("Single-steps out of the current method on the current or specified thread.")]
		public StepOperationResult StepOut(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => LoggedCall("step_out", $"pid={processId},tid={threadId},managedTid={managedThreadId}", () =>
				StepThread(processId, threadId, managedThreadId, singleProcessOnly ? DbgStepKind.StepOutProcess : DbgStepKind.StepOut, "step_out"));

		[McpServerTool(Name = "step_debug_thread_out"), Description("Single-steps out of the current method on the current or specified debug thread.")]
		public StepOperationResult StepDebugThreadOut(
			[Description("Optional process id used when resolving a thread.")] int? processId = null,
			[Description("Optional native thread id.")] ulong? threadId = null,
			[Description("Optional managed thread id.")] ulong? managedThreadId = null,
			[Description("When true, only the selected process executes during the step.")] bool singleProcessOnly = false) => StepOut(processId, threadId, managedThreadId, singleProcessOnly);

		[McpServerTool(Name = "run_all"), Description("Continues all paused debugged processes.")]
		public DebugControlResult RunAll() => LoggedCall("run_all", string.Empty, () => {
			if (!dbgManager.IsDebugging)
				throw new InvalidOperationException("No active debugger session.");
			dbgManager.RunAll();
			return new DebugControlResult("run_all", "requested", dbgManager.IsDebugging, dbgManager.IsRunning);
		});

		[McpServerTool(Name = "continue_debugged_processes"), Description("Continues all paused debugged processes.")]
		public DebugControlResult ContinueDebuggedProcesses() => RunAll();

		[McpServerTool(Name = "stop_debugging"), Description("Stops debugging all processes in the current session.")]
		public DebugControlResult StopDebugging() => LoggedCall("stop_debugging", string.Empty, () => {
			if (!dbgManager.IsDebugging)
				throw new InvalidOperationException("No active debugger session.");
			dbgManager.StopDebuggingAll();
			return new DebugControlResult("stop_debugging", "requested", dbgManager.IsDebugging, dbgManager.IsRunning);
		});

		[McpServerTool(Name = "stop_debug_session"), Description("Stops debugging all processes in the current session.")]
		public DebugControlResult StopDebugSession() => StopDebugging();

		[McpServerTool(Name = "list_breakpoints"), Description("Lists all visible code breakpoints.")]
		public BreakpointInfo[] ListBreakpoints() => LoggedCall("list_breakpoints", string.Empty, () =>
			dbgCodeBreakpointsService.VisibleBreakpoints.Select(ToBreakpointInfo).OrderBy(a => a.Id).ToArray());

		[McpServerTool(Name = "set_method_breakpoint"), Description("Sets a .NET code breakpoint on a method token and optional IL offset.")]
		public BreakpointSetResult SetMethodBreakpoint(
			[Description("Document identifier. File paths are auto-loaded if needed.")] string documentId,
			[Description("Type full name or short name that owns the method.")] string typeName,
			[Description("Method name.")] string methodName,
			[Description("Optional metadata token such as 0x06001234. When provided, it takes precedence over name-based matching.")] string? metadataToken = null,
			[Description("Optional full method signature.")] string? methodSignature = null,
			[Description("Optional parameter type list used to disambiguate overloads.")] string[]? parameterTypes = null,
			[Description("Optional parameter count used to disambiguate overloads.")] int? parameterCount = null,
			[Description("Optional IL offset within the method body.")] uint ilOffset = 0,
			[Description("Whether the breakpoint starts enabled.")] bool isEnabled = true,
			[Description("Optional breakpoint condition expression.")] string? condition = null,
			[Description("Condition kind: IsTrue or WhenChanged.")] string? conditionKind = null,
			[Description("Optional hit count value.")] int? hitCount = null,
			[Description("Hit count kind: Equals, MultipleOf, GreaterThanOrEquals.")] string? hitCountKind = null,
			[Description("Optional filter expression.")] string? filter = null,
			[Description("Optional trace message. If set, a tracepoint is created.")] string? traceMessage = null,
			[Description("If traceMessage is set, true continues execution after logging.")] bool continueAfterTrace = true) => LoggedCall("set_method_breakpoint", $"{documentId}::{typeName}::{methodName}", () => {
				var document = ResolveDocument(documentId);
				var type = ResolveType(document, typeName);
				var resolution = ResolveMethodForBreakpoint(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount);
				if (resolution.Method is null)
					return new BreakpointSetResult(false, null, resolution.Message, resolution.CandidateMethods);

				var method = resolution.Method;
				var module = method.Module ?? throw new InvalidOperationException($"Method '{method.FullName}' has no module.");
				var moduleId = CreateModuleId(module);
				var settings = CreateBreakpointSettings(isEnabled, condition, conditionKind, hitCount, hitCountKind, filter, traceMessage, continueAfterTrace);
				var breakpoint = dbgDotNetBreakpointFactory.Create(moduleId, method.MDToken.Raw, ilOffset, settings);
				if (breakpoint is null) {
					var existing = dbgDotNetBreakpointFactory.TryGetBreakpoint(moduleId, method.MDToken.Raw, ilOffset);
					return new BreakpointSetResult(false, existing is null ? null : ToBreakpointInfo(existing), "A breakpoint already exists at the requested method and IL offset.", resolution.CandidateMethods);
				}
				return new BreakpointSetResult(true, ToBreakpointInfo(breakpoint), resolution.Message, resolution.CandidateMethods);
			});

		[McpServerTool(Name = "set_entry_point_breakpoint"), Description("Sets a breakpoint on the assembly entry point of a loaded document.")]
		public BreakpointSetResult SetEntryPointBreakpoint(
			[Description("Document identifier. File paths are auto-loaded if needed.")] string documentId,
			[Description("Optional IL offset within the entry point method body.")] uint ilOffset = 0,
			[Description("Whether the breakpoint starts enabled.")] bool isEnabled = true,
			[Description("Optional breakpoint condition expression.")] string? condition = null,
			[Description("Condition kind: IsTrue or WhenChanged.")] string? conditionKind = null,
			[Description("Optional hit count value.")] int? hitCount = null,
			[Description("Hit count kind: Equals, MultipleOf, GreaterThanOrEquals.")] string? hitCountKind = null,
			[Description("Optional filter expression.")] string? filter = null,
			[Description("Optional trace message. If set, a tracepoint is created.")] string? traceMessage = null,
			[Description("If traceMessage is set, true continues execution after logging.")] bool continueAfterTrace = true) => LoggedCall("set_entry_point_breakpoint", documentId, () => {
				var document = ResolveDocument(documentId);
				var entryPoint = document.GetModules<ModuleDef>().Select(a => a.EntryPoint).FirstOrDefault(a => a is not null);
				if (entryPoint is null)
					return new BreakpointSetResult(false, null, $"Document '{document.Filename}' does not have a managed entry point.", Array.Empty<MethodInfoResult>());

				var module = entryPoint.Module ?? throw new InvalidOperationException($"Method '{entryPoint.FullName}' has no module.");
				var moduleId = CreateModuleId(module);
				var settings = CreateBreakpointSettings(isEnabled, condition, conditionKind, hitCount, hitCountKind, filter, traceMessage, continueAfterTrace);
				var breakpoint = dbgDotNetBreakpointFactory.Create(moduleId, entryPoint.MDToken.Raw, ilOffset, settings);
				if (breakpoint is null) {
					var existing = dbgDotNetBreakpointFactory.TryGetBreakpoint(moduleId, entryPoint.MDToken.Raw, ilOffset);
					return new BreakpointSetResult(false, existing is null ? null : ToBreakpointInfo(existing), "A breakpoint already exists at the assembly entry point and IL offset.", new[] { ToMethodInfoResult(entryPoint) });
				}
				return new BreakpointSetResult(true, ToBreakpointInfo(breakpoint), "Breakpoint set on the assembly entry point.", new[] { ToMethodInfoResult(entryPoint) });
			});

		[McpServerTool(Name = "update_breakpoint"), Description("Updates an existing breakpoint's enabled state, condition, hit count, filter, trace message, or labels.")]
		public BreakpointUpdateResult UpdateBreakpoint(
			[Description("Breakpoint id returned by list_breakpoints or set_method_breakpoint.")] int breakpointId,
			[Description("Optional enabled state. Leave null to preserve the current value.")] bool? isEnabled = null,
			[Description("Optional breakpoint condition expression.")] string? condition = null,
			[Description("Set true to clear the current condition.")] bool clearCondition = false,
			[Description("Condition kind: IsTrue or WhenChanged.")] string? conditionKind = null,
			[Description("Optional hit count value.")] int? hitCount = null,
			[Description("Set true to clear the current hit count.")] bool clearHitCount = false,
			[Description("Hit count kind: Equals, MultipleOf, GreaterThanOrEquals.")] string? hitCountKind = null,
			[Description("Optional filter expression.")] string? filter = null,
			[Description("Set true to clear the current filter.")] bool clearFilter = false,
			[Description("Optional trace message.")] string? traceMessage = null,
			[Description("Set true to clear the current trace message.")] bool clearTrace = false,
			[Description("If traceMessage is set, true continues execution after logging.")] bool continueAfterTrace = true,
			[Description("Optional complete replacement for breakpoint labels.")] string[]? labels = null,
			[Description("Set true to clear all labels.")] bool clearLabels = false) => LoggedBackgroundCall("update_breakpoint", breakpointId.ToString(), () => {
				var breakpoint = ResolveBreakpoint(breakpointId);
				var settings = breakpoint.Settings;

				if (isEnabled is not null)
					settings.IsEnabled = isEnabled.Value;

				if (clearCondition)
					settings.Condition = null;
				else if (condition is not null)
					settings.Condition = new DbgCodeBreakpointCondition(ParseConditionKind(conditionKind), condition.Trim());

				if (clearHitCount)
					settings.HitCount = null;
				else if (hitCount is not null)
					settings.HitCount = new DbgCodeBreakpointHitCount(ParseHitCountKind(hitCountKind), hitCount.Value);

				if (clearFilter)
					settings.Filter = null;
				else if (filter is not null)
					settings.Filter = new DbgCodeBreakpointFilter(filter.Trim());

				if (clearTrace)
					settings.Trace = null;
				else if (traceMessage is not null)
					settings.Trace = new DbgCodeBreakpointTrace(traceMessage, continueAfterTrace);

				if (clearLabels)
					settings.Labels = EmptyBreakpointLabels;
				else if (labels is not null)
					settings.Labels = new ReadOnlyCollection<string>(labels.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray());

				dbgCodeBreakpointsService.Modify(breakpoint, settings);
				var refreshedBreakpoint = WaitForBreakpointSettings(breakpointId, settings, timeoutMilliseconds: 1000) ?? ResolveBreakpoint(breakpointId);
				var message = $"Updated breakpoint {breakpointId}.";
				if (refreshedBreakpoint.Settings != settings)
					message += " Update request was queued, but the final debugger state has not fully converged yet.";
				else if (isEnabled is not null && refreshedBreakpoint.IsEnabled != isEnabled.Value)
					message += $" Requested isEnabled={isEnabled.Value}, but actual isEnabled={refreshedBreakpoint.IsEnabled}.";
				return new BreakpointUpdateResult(true, ToBreakpointInfo(refreshedBreakpoint), message);
			});

		[McpServerTool(Name = "remove_breakpoint"), Description("Removes a breakpoint by id.")]
		public BreakpointOperationResult RemoveBreakpoint(
			[Description("Breakpoint id returned by list_breakpoints or set_method_breakpoint.")] int breakpointId) => LoggedCall("remove_breakpoint", breakpointId.ToString(), () => {
				var breakpoint = ResolveBreakpoint(breakpointId);
				dbgCodeBreakpointsService.Remove(breakpoint);
				return new BreakpointOperationResult(true, $"Removed breakpoint {breakpointId}.");
			});

		[McpServerTool(Name = "clear_breakpoints"), Description("Removes all visible code breakpoints.")]
		public BreakpointOperationResult ClearBreakpoints() => LoggedCall("clear_breakpoints", string.Empty, () => {
				var count = dbgCodeBreakpointsService.VisibleBreakpoints.Count();
				dbgCodeBreakpointsService.Clear();
				return new BreakpointOperationResult(true, $"Cleared {count} breakpoint(s).");
			});

		[McpServerTool(Name = "list_exception_settings"), Description("Lists debugger exception settings, including category default entries such as 'all CLR exceptions not in this list'.")]
		public ExceptionSettingsListResult ListExceptionSettings(
			[Description("Optional exception category filter. Accepts internal names like DotNet or MDA and friendly aliases like .NET or CLR.")] string? category = null,
			[Description("Optional substring filter matched against category, identifier, display name, description, or conditions.")] string? query = null,
			[Description("When true, only entries that currently break when thrown are returned.")] bool onlyEnabled = false,
			[Description("Maximum number of exception entries to return.")] int maxResults = 500) => LoggedBackgroundCall("list_exception_settings", category ?? string.Empty, () => {
				var normalizedCategory = NormalizeExceptionCategory(category, allowEmpty: true);
				var normalizedQuery = NormalizeQuery(query);
				var items = dbgExceptionSettingsService.Exceptions
					.Where(a => normalizedCategory is null || StringComparer.Ordinal.Equals(a.Definition.Id.Category, normalizedCategory))
					.Select(ToExceptionSettingInfo)
					.Where(a => !onlyEnabled || a.BreakWhenThrown)
					.Where(a => normalizedQuery is null || ExceptionSettingMatches(a, normalizedQuery))
					.OrderBy(a => a.CategoryDisplayName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.IsDefault ? 0 : 1)
					.ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
					.Take(Math.Max(1, maxResults))
					.ToArray();
				var categories = dbgExceptionSettingsService.CategoryDefinitions
					.Where(a => normalizedCategory is null || StringComparer.Ordinal.Equals(a.Name, normalizedCategory))
					.OrderBy(a => a.ShortDisplayName, StringComparer.OrdinalIgnoreCase)
					.Select(ToExceptionCategoryInfo)
					.ToArray();
				return new ExceptionSettingsListResult(categories, items);
			});

		[McpServerTool(Name = "set_all_exception_breaks"), Description("Enables or disables break-on-thrown for all exception settings in one or all categories, including category default entries and explicit exceptions.")]
		public ExceptionSettingsUpdateResult SetAllExceptionBreaks(
			[Description("True enables first-chance break on all targeted exceptions. False disables it.")] bool enabled,
			[Description("Optional exception category filter. Accepts internal names like DotNet or MDA and friendly aliases like .NET or CLR.")] string? category = null) => LoggedBackgroundCall("set_all_exception_breaks", category ?? string.Empty, () => {
				var categoryDefinitions = GetTargetExceptionCategories(category);
				var categoryNames = new HashSet<string>(categoryDefinitions.Select(a => a.Name), StringComparer.Ordinal);
				var snapshot = dbgExceptionSettingsService.Exceptions;
				var existing = snapshot.Where(a => categoryNames.Contains(a.Definition.Id.Category)).ToArray();
				var modify = new List<DbgExceptionIdAndSettings>(existing.Length);
				foreach (var item in existing) {
					var settings = new DbgExceptionSettings(SetStopFirstChance(item.Settings.Flags, enabled), item.Settings.Conditions);
					if (settings != item.Settings)
						modify.Add(new DbgExceptionIdAndSettings(item.Definition.Id, settings));
				}

				var existingIds = new HashSet<DbgExceptionId>(snapshot.Select(a => a.Definition.Id));
				var add = new List<DbgExceptionSettingsInfo>();
				foreach (var categoryDefinition in categoryDefinitions) {
					var defaultId = new DbgExceptionId(categoryDefinition.Name);
					if (existingIds.Contains(defaultId))
						continue;
					var definition = new DbgExceptionDefinition(defaultId, DbgExceptionDefinitionFlags.None, GetDefaultExceptionDescription(categoryDefinition));
					var settings = new DbgExceptionSettings(SetStopFirstChance(definition.Flags, enabled));
					add.Add(new DbgExceptionSettingsInfo(definition, settings));
				}

				if (add.Count > 0)
					dbgExceptionSettingsService.Add(add.ToArray());
				if (modify.Count > 0)
					dbgExceptionSettingsService.Modify(modify.ToArray());
				WaitForExceptionSettings(add.Select(a => (a.Definition.Id, a.Settings)).Concat(modify.Select(a => (a.Id, a.Settings))).ToArray(), 1500);

				var updated = dbgExceptionSettingsService.Exceptions
					.Where(a => categoryNames.Contains(a.Definition.Id.Category))
					.Select(ToExceptionSettingInfo)
					.OrderBy(a => a.CategoryDisplayName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.IsDefault ? 0 : 1)
					.ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				return new ExceptionSettingsUpdateResult(true, $"{(enabled ? "Enabled" : "Disabled")} break-on-thrown for {updated.Length} exception setting(s).", add.Count + modify.Count, updated);
			});

		[McpServerTool(Name = "enable_all_exception_breaks"), Description("Convenience wrapper that enables break-on-thrown for all exception settings in one or all categories.")]
		public ExceptionSettingsUpdateResult EnableAllExceptionBreaks(
			[Description("Optional exception category filter. Accepts internal names like DotNet or MDA and friendly aliases like .NET or CLR.")] string? category = null) => SetAllExceptionBreaks(true, category);

		[McpServerTool(Name = "disable_all_exception_breaks"), Description("Convenience wrapper that disables break-on-thrown for all exception settings in one or all categories.")]
		public ExceptionSettingsUpdateResult DisableAllExceptionBreaks(
			[Description("Optional exception category filter. Accepts internal names like DotNet or MDA and friendly aliases like .NET or CLR.")] string? category = null) => SetAllExceptionBreaks(false, category);

		[McpServerTool(Name = "set_exception_break_state"), Description("Enables or disables break-on-thrown for specific exceptions by name or code, matching dnSpy's Exception Settings window semantics.")]
		public ExceptionSettingsUpdateResult SetExceptionBreakState(
			[Description("Exception identifiers to update. For DotNet pass full type names like System.ArgumentNullException. For code-based categories pass decimal values or 0x-prefixed hex.")] string[] exceptionIdentifiers,
			[Description("True enables first-chance break on the specified exceptions. False disables it.")] bool enabled,
			[Description("Exception category. Defaults to DotNet and accepts aliases like .NET or CLR.")] string? category = null,
			[Description("When true, missing explicit entries are created when needed to enforce the requested state.")] bool createMissing = true) => LoggedBackgroundCall("set_exception_break_state", string.Join(",", exceptionIdentifiers ?? Array.Empty<string>()), () => {
				if (exceptionIdentifiers is null || exceptionIdentifiers.Length == 0)
					throw new ArgumentException("At least one exception identifier must be provided.", nameof(exceptionIdentifiers));

				var categoryDefinition = GetTargetExceptionCategory(category);
				var ids = exceptionIdentifiers
					.Where(a => !string.IsNullOrWhiteSpace(a))
					.Select(a => ParseExceptionId(categoryDefinition, a.Trim()))
					.Distinct()
					.ToArray();
				if (ids.Length == 0)
					throw new ArgumentException("At least one non-empty exception identifier must be provided.", nameof(exceptionIdentifiers));

				var modify = new List<DbgExceptionIdAndSettings>(ids.Length);
				var add = new List<DbgExceptionSettingsInfo>(ids.Length);
				foreach (var id in ids) {
					if (dbgExceptionSettingsService.TryGetSettings(id, out var existingSettings)) {
						var updatedSettings = new DbgExceptionSettings(SetStopFirstChance(existingSettings.Flags, enabled), existingSettings.Conditions);
						if (updatedSettings != existingSettings)
							modify.Add(new DbgExceptionIdAndSettings(id, updatedSettings));
						continue;
					}

					var effectiveSettings = dbgExceptionSettingsService.GetSettings(id);
					if (HasStopFirstChance(effectiveSettings.Flags) == enabled)
						continue;
					if (!createMissing)
						continue;

					var definition = CreateMissingExceptionDefinition(id, categoryDefinition);
					var settings = new DbgExceptionSettings(SetStopFirstChance(definition.Flags, enabled));
					add.Add(new DbgExceptionSettingsInfo(definition, settings));
				}

				if (add.Count > 0)
					dbgExceptionSettingsService.Add(add.ToArray());
				if (modify.Count > 0)
					dbgExceptionSettingsService.Modify(modify.ToArray());
				WaitForExceptionSettings(add.Select(a => (a.Definition.Id, a.Settings)).Concat(modify.Select(a => (a.Id, a.Settings))).ToArray(), 1500);

				var updatedIds = new HashSet<DbgExceptionId>(ids);
				var updated = dbgExceptionSettingsService.Exceptions
					.Where(a => updatedIds.Contains(a.Definition.Id))
					.Select(ToExceptionSettingInfo)
					.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				return new ExceptionSettingsUpdateResult(true, $"{(enabled ? "Enabled" : "Disabled")} break-on-thrown for {updated.Length} specified exception(s).", add.Count + modify.Count, updated);
			});

		[McpServerTool(Name = "reset_exception_settings"), Description("Restores the debugger exception settings window to its default state and removes user-added exceptions.")]
		public ExceptionSettingsUpdateResult ResetExceptionSettings() => LoggedBackgroundCall("reset_exception_settings", string.Empty, () => {
				dbgExceptionSettingsService.Reset();
				SpinWaitForExceptionReset(1500);
				var updated = dbgExceptionSettingsService.Exceptions
					.Select(ToExceptionSettingInfo)
					.OrderBy(a => a.CategoryDisplayName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(a => a.IsDefault ? 0 : 1)
					.ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				return new ExceptionSettingsUpdateResult(true, "Restored default exception settings.", updated.Length, updated);
			});

		[McpServerTool(Name = "restore_default_exception_settings"), Description("Alias of reset_exception_settings. Restores the debugger exception settings window to its default state.")]
		public ExceptionSettingsUpdateResult RestoreDefaultExceptionSettings() => ResetExceptionSettings();

		T LoggedCall<T>(string toolName, string detail, Func<T> action) {
			var callId = Interlocked.Increment(ref nextToolCallId);
			var detailText = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
			logger.WriteLine($"MCP tool call #{callId}: {toolName}{detailText}");
			try {
				var result = mcpServerController.RunOnUISync(action);
				logger.WriteLine($"MCP tool completed #{callId}: {toolName}");
				return result;
			}
			catch (Exception ex) {
				var actualException = UnwrapToolException(ex);
				logger.WriteError($"MCP tool failed #{callId}: {toolName}{detailText}: {actualException.Message}");
				if (!IsExpectedToolException(actualException))
					logger.WriteException(actualException);
				return CreateErrorResult<T>(toolName, actualException);
			}
		}

		T LoggedBackgroundCall<T>(string toolName, string detail, Func<T> action) {
			var callId = Interlocked.Increment(ref nextToolCallId);
			var detailText = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
			logger.WriteLine($"MCP tool call #{callId}: {toolName}{detailText}");
			try {
				var result = action();
				logger.WriteLine($"MCP tool completed #{callId}: {toolName}");
				return result;
			}
			catch (Exception ex) {
				var actualException = UnwrapToolException(ex);
				logger.WriteError($"MCP tool failed #{callId}: {toolName}{detailText}: {actualException.Message}");
				if (!IsExpectedToolException(actualException))
					logger.WriteException(actualException);
				return CreateErrorResult<T>(toolName, actualException);
			}
		}

		Exception UnwrapToolException(Exception ex) {
			while (true) {
				switch (ex) {
				case TargetInvocationException tie when tie.InnerException is not null:
					ex = tie.InnerException;
					continue;
				case AggregateException ae when ae.InnerExceptions.Count == 1:
					ex = ae.InnerExceptions[0];
					continue;
				default:
					return ex;
				}
			}
		}

		T CreateErrorResult<T>(string toolName, Exception ex) {
			var type = typeof(T);
			var errorMessage = ex.Message;

			if (type.IsArray) {
				var elementType = type.GetElementType();
				if (elementType is not null && TryCreateObjectErrorResult(elementType, toolName, errorMessage, out var element)) {
					var array = Array.CreateInstance(elementType, 1);
					array.SetValue(element, 0);
					return (T)(object)array;
				}
				return (T)(object)Array.CreateInstance(type.GetElementType() ?? typeof(object), 0);
			}

			if (TryCreateObjectErrorResult(type, toolName, errorMessage, out var result))
				return (T)result!;

			return default!;
		}

		bool TryCreateObjectErrorResult(Type type, string toolName, string errorMessage, out object? result) {
			result = null;
			if (type == typeof(string)) {
				result = errorMessage;
				return true;
			}

			var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
			if (constructors.Length == 0)
				return false;

			var ctor = constructors.OrderByDescending(a => a.GetParameters().Length).First();
			var parameters = ctor.GetParameters();
			var args = new object?[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
				args[i] = CreateFallbackValue(parameters[i].ParameterType, parameters[i].Name, toolName, errorMessage);

			result = ctor.Invoke(args);
			return true;
		}

		object? CreateFallbackValue(Type type, string? parameterName, string toolName, string errorMessage) {
			var name = parameterName ?? string.Empty;
			if (string.Equals(name, "errorMessage", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "message", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "error", StringComparison.OrdinalIgnoreCase))
				return errorMessage;
			if (string.Equals(name, "action", StringComparison.OrdinalIgnoreCase))
				return toolName;
			if (string.Equals(name, "status", StringComparison.OrdinalIgnoreCase))
				return "error";

			var underlyingType = Nullable.GetUnderlyingType(type);
			if (underlyingType is not null)
				return CreateFallbackValue(underlyingType, parameterName, toolName, errorMessage);

			if (type == typeof(string))
				return string.Empty;
			if (type == typeof(bool))
				return false;
			if (type == typeof(int))
				return string.Equals(name, "activeFrameIndex", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
			if (type == typeof(long))
				return 0L;
			if (type == typeof(uint))
				return 0U;
			if (type == typeof(ulong))
				return 0UL;
			if (type == typeof(double))
				return 0d;
			if (type == typeof(Guid))
				return Guid.Empty;
			if (type == typeof(DateTimeOffset))
				return DateTimeOffset.UtcNow;
			if (type == typeof(DateTime))
				return DateTime.UtcNow;
			if (type.IsArray)
				return Array.CreateInstance(type.GetElementType() ?? typeof(object), 0);
			if (type.IsValueType)
				return Activator.CreateInstance(type);
			return null;
		}

		AssemblyAttributes UpdateFlag(AssemblyAttributes value, AssemblyAttributes flag, bool? enabled) {
			if (enabled is null)
				return value;
			return enabled.Value ? value | flag : value & ~flag;
		}

		Characteristics UpdateFlag(Characteristics value, Characteristics flag, bool? enabled) {
			if (enabled is null)
				return value;
			return enabled.Value ? value | flag : value & ~flag;
		}

		DllCharacteristics UpdateFlag(DllCharacteristics value, DllCharacteristics flag, bool? enabled) {
			if (enabled is null)
				return value;
			return enabled.Value ? value | flag : value & ~flag;
		}

		ComImageFlags UpdateFlag(ComImageFlags value, ComImageFlags flag, bool? enabled) {
			if (enabled is null)
				return value;
			return enabled.Value ? value | flag : value & ~flag;
		}

		bool TryParseNullableGuidText(string text, out Guid? guid, out string? error) {
			guid = null;
			error = null;
			var normalized = (text ?? string.Empty).Trim();
			if (normalized.Length == 0)
				return true;
			if (!Guid.TryParse(normalized, out var parsed)) {
				error = $"Invalid GUID '{text}'.";
				return false;
			}
			guid = parsed;
			return true;
		}

		AddTypeCompilerSession CreateAddTypeCompilerSession(ModuleDef module) {
			var provider = ResolveAddTypeCompilerProvider();
			var compiler = provider.Create(CompilationKind.AddClass);
			var references = new CompilerReferenceSession(module, compiler.GetRequiredAssemblyReferences(module));
			var assemblyName = module.Assembly?.Name?.String ?? Path.GetFileNameWithoutExtension(module.Name) ?? "EditedAssembly";
			var publicKey = (module.Assembly?.PublicKey as PublicKey)?.Data;
			compiler.InitializeProject(new CompilerProjectInfo(assemblyName, publicKey, references.MetadataReferences, references, GetCompilerTargetPlatform(module)));
			return new AddTypeCompilerSession(compiler, references);
		}

		ILanguageCompilerProvider ResolveAddTypeCompilerProvider() {
			var currentDecompilerGuid = decompilerService.Decompiler.GenericGuid;
			var provider = languageCompilerProviders.FirstOrDefault(a => a.CanCompile(CompilationKind.AddClass) && a.Language == currentDecompilerGuid);
			if (provider is not null)
				return provider;
			provider = languageCompilerProviders.FirstOrDefault(a => a.CanCompile(CompilationKind.AddClass) && a.Language == DecompilerConstants.LANGUAGE_CSHARP);
			if (provider is not null)
				return provider;
			provider = languageCompilerProviders.FirstOrDefault(a => a.CanCompile(CompilationKind.AddClass));
			return provider ?? throw new InvalidOperationException("No language compiler provider is available for Add Class compilation.");
		}

		TargetPlatform GetCompilerTargetPlatform(ModuleDef module) {
			var flags = module.Cor20HeaderFlags;
			return module.Machine switch {
				Machine.I386 when (flags & ComImageFlags.Bit32Preferred) != 0 => TargetPlatform.AnyCpu32BitPreferred,
				Machine.I386 when (flags & ComImageFlags.Bit32Required) != 0 => TargetPlatform.X86,
				Machine.I386 => TargetPlatform.AnyCpu,
				Machine.AMD64 => TargetPlatform.X64,
				Machine.IA64 => TargetPlatform.Itanium,
				Machine.ARMNT or Machine.ARM => TargetPlatform.Arm,
				Machine.ARM64 => TargetPlatform.Arm64,
				_ => TargetPlatform.AnyCpu,
			};
		}

		(CompilationResult? Result, CompilerLikeDiagnostic[] Diagnostics) CompileAddTypeSource(ModuleDef module, string sourceCode, AddTypeCompilerSession session) {
			try {
				session.Compiler.AddDocuments(new[] { new CompilerDocumentInfo(sourceCode, "main.cs") });
				var result = session.Compiler.CompileAsync(CancellationToken.None).GetAwaiter().GetResult();
				var diagnostics = ToCompilerLikeDiagnostics(result.Diagnostics);
				if (!result.Success)
					return (null, diagnostics);
				return (result, diagnostics);
			}
			catch (Exception ex) {
				var actual = UnwrapToolException(ex);
				return (null, new[] { new CompilerLikeDiagnostic("Error", "ADDTYPE001", actual.Message) });
			}
		}

		(TypeDef[] AddedTypes, CompilerLikeDiagnostic[] Diagnostics, bool Success, string Message) ImportCompiledTypes(ModuleDef module, byte[] rawFile, DebugFileResult debugFile) {
			try {
				var asmEditorAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "dnSpy.AsmEditor.x", StringComparison.OrdinalIgnoreCase)) ?? Assembly.Load("dnSpy.AsmEditor.x");
				var importerType = asmEditorAssembly.GetType("dnSpy.AsmEditor.Compiler.ModuleImporter", throwOnError: true)!;
				var optionsType = asmEditorAssembly.GetType("dnSpy.AsmEditor.Compiler.ModuleImporterOptions", throwOnError: true)!;
				var importer = Activator.CreateInstance(importerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, args: new object[] { module, module.Context.AssemblyResolver }, culture: null) ?? throw new InvalidOperationException("Could not create ModuleImporter.");
				var importMethod = importerType.GetMethod("Import", BindingFlags.Instance | BindingFlags.Public, binder: null, types: new[] { typeof(byte[]), typeof(DebugFileResult), optionsType }, modifiers: null) ?? throw new InvalidOperationException("Could not find ModuleImporter.Import(byte[], DebugFileResult, ModuleImporterOptions).");
				importMethod.Invoke(importer, new object[] { rawFile, debugFile, Enum.ToObject(optionsType, 0) });

				var diagnostics = ToCompilerLikeDiagnostics((CompilerDiagnostic[])(importerType.GetProperty("Diagnostics", BindingFlags.Instance | BindingFlags.Public)!.GetValue(importer) ?? Array.Empty<CompilerDiagnostic>()));
				if (diagnostics.Any(a => string.Equals(a.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
					return (Array.Empty<TypeDef>(), diagnostics, false, "Import failed with diagnostics.");

				var addedTypes = new List<TypeDef>();
				var newTypes = (System.Collections.IEnumerable?)(importerType.GetProperty("NewNonNestedTypes", BindingFlags.Instance | BindingFlags.Public)!.GetValue(importer));
				if (newTypes is not null) {
					foreach (var newType in newTypes) {
						var targetType = (TypeDef?)newType.GetType().GetProperty("TargetType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(newType);
						if (targetType is null)
							continue;
						addedTypes.Add(targetType);
					}
				}

				ExecuteAddUpdatedNodesHelper(module, importer);

				if (addedTypes.Count == 0)
					return (Array.Empty<TypeDef>(), diagnostics, false, "Compilation succeeded, but no new top-level types were imported.");
				return (addedTypes.ToArray(), diagnostics, true, $"Imported {addedTypes.Count} type(s).");
			}
			catch (TargetInvocationException ex) {
				var actual = UnwrapToolException(ex.InnerException ?? ex);
				return (Array.Empty<TypeDef>(), new[] { new CompilerLikeDiagnostic("Error", "ADDTYPE002", actual.Message) }, false, actual.Message);
			}
			catch (Exception ex) {
				var actual = UnwrapToolException(ex);
				return (Array.Empty<TypeDef>(), new[] { new CompilerLikeDiagnostic("Error", "ADDTYPE003", actual.Message) }, false, actual.Message);
			}
		}

		CompilerLikeDiagnostic[] ToCompilerLikeDiagnostics(CompilerDiagnostic[] diagnostics) => diagnostics.Select(a => new CompilerLikeDiagnostic(a.Severity.ToString(), a.Id, a.Description, a.Filename, a.LineLocationSpan?.StartLinePosition.Line, a.LineLocationSpan?.StartLinePosition.Character)).ToArray();

		TypeDef ResolveTypeForDeletion(IDsDocument document, string? typeName, string? metadataToken) {
			if (!string.IsNullOrWhiteSpace(metadataToken)) {
				if (!TryParseMetadataToken(metadataToken, out var rawToken))
					throw new InvalidOperationException($"Invalid metadata token '{metadataToken}'. Expected a hex token such as 0x02000001.");
				foreach (var module in document.GetModules<ModuleDef>()) {
					if (module.ResolveToken(rawToken) is TypeDef type)
						return type;
				}
				throw new InvalidOperationException($"Could not resolve type metadata token '{metadataToken}' in document '{document.Filename}'.");
			}
			if (string.IsNullOrWhiteSpace(typeName))
				throw new ArgumentException("Either typeName or metadataToken must be provided.");
			var comparer = StringComparer.OrdinalIgnoreCase;
			var resolved = document.GetModules<ModuleDef>()
				.SelectMany(a => a.GetTypes())
				.FirstOrDefault(a => comparer.Equals(a.FullName, typeName) || comparer.Equals(a.ReflectionFullName, typeName) || comparer.Equals(a.Name, typeName));
			return resolved ?? throw new InvalidOperationException($"Could not find type '{typeName}' in document '{document.Filename}'.");
		}

		void ExecuteAddUpdatedNodesHelper(ModuleDef module, object importer) {
			var asmEditorAssembly = importer.GetType().Assembly;
			var providerType = asmEditorAssembly.GetType("dnSpy.AsmEditor.Compiler.IAddUpdatedNodesHelperProvider", throwOnError: true)!;
			var resolveMethod = typeof(IServiceLocator).GetMethod(nameof(IServiceLocator.Resolve))!.MakeGenericMethod(providerType);
			var provider = resolveMethod.Invoke(serviceLocator, null) ?? throw new InvalidOperationException("Could not resolve dnSpy.AsmEditor AddUpdatedNodesHelper provider.");
			var modNode = Application.Current?.Dispatcher.Invoke(() => documentTreeView.FindNode(module)) ?? documentTreeView.FindNode(module);
			if (modNode is null)
				throw new InvalidOperationException($"Could not find a document tree node for module '{module.Name}'.");
			var createMethod = providerType.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public)!;
			var helper = Application.Current?.Dispatcher.Invoke(() => createMethod.Invoke(provider, new object[] { modNode, importer })) ?? createMethod.Invoke(provider, new object[] { modNode, importer });
			if (helper is null)
				throw new InvalidOperationException("dnSpy.AsmEditor returned a null AddUpdatedNodesHelper.");
			var executeMethod = helper.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public)!;
			if (Application.Current?.Dispatcher is not null)
				Application.Current.Dispatcher.Invoke(() => executeMethod.Invoke(helper, null));
			else
				executeMethod.Invoke(helper, null);
		}

		MethodIlBodyResult ToMethodIlBodyResult(IDsDocument document, MethodDef method) {
			if (method.MethodBody is not CilBody body)
				return new MethodIlBodyResult(document.Filename, method.FullName, false, null, null, null, null, null, Array.Empty<MethodIlInstructionInfo>(), Array.Empty<MethodIlLocalInfo>(), Array.Empty<MethodIlExceptionHandlerInfo>(), "Target method does not have a CIL body.");

			body.UpdateInstructionOffsets();
			var instructionMap = body.Instructions.Select((instruction, index) => new { instruction, index }).ToDictionary(a => a.instruction, a => a.index);
			return new MethodIlBodyResult(
				document.Filename,
				method.FullName,
				true,
				body.KeepOldMaxStack,
				body.InitLocals,
				body.MaxStack,
				body.LocalVarSigTok,
				body.Instructions.Count,
				body.Instructions.Select((instruction, index) => ToMethodIlInstructionInfo(method, body, instructionMap, instruction, index)).ToArray(),
				body.Variables.Select((local, index) => new MethodIlLocalInfo(index, local.Type?.FullName ?? string.Empty, local.Name, local.Attributes.ToString())).ToArray(),
				body.ExceptionHandlers.Select((eh, index) => ToMethodIlExceptionHandlerInfo(body, instructionMap, eh, index)).ToArray(),
				null);
		}

		MethodIlInstructionInfo ToMethodIlInstructionInfo(MethodDef method, CilBody body, Dictionary<Instruction, int> instructionMap, Instruction instruction, int index) {
			var operandInfo = ToMethodIlOperandInfo(method, body, instructionMap, instruction.Operand);
			return new MethodIlInstructionInfo(index, unchecked((int)instruction.Offset), $"IL_{instruction.Offset:X4}", instruction.OpCode.Name, instruction.OpCode.Code.ToString(), instruction.OpCode.OperandType.ToString(), operandInfo, instruction.SequencePoint?.ToString());
		}

		MethodIlOperandInfo ToMethodIlOperandInfo(MethodDef method, CilBody body, Dictionary<Instruction, int> instructionMap, object? operand) {
			switch (operand) {
			case null:
				return new MethodIlOperandInfo("none", null, null, null, null, null, null, null, null, null, null, null, null, null, null);
			case Instruction target:
					return new MethodIlOperandInfo("instruction", $"IL_{target.Offset:X4}", instructionMap.TryGetValue(target, out var targetIndex) ? targetIndex : null, unchecked((int)target.Offset), null, null, null, null, null, null, null, null, null, null, null);
			case Instruction[] targets:
				return new MethodIlOperandInfo("instruction-list", string.Join(", ", targets.Select(a => $"IL_{a.Offset:X4}")), null, null, targets.Select(a => instructionMap.TryGetValue(a, out var idx) ? idx : -1).ToArray(), targets.Select(a => unchecked((int)a.Offset)).ToArray(), null, null, null, null, null, null, null, null, null);
			case Local local:
				return new MethodIlOperandInfo("local", local.ToString(), body.Variables.IndexOf(local), null, null, null, null, null, null, null, null, null, null, null, null);
			case Parameter parameter:
				return new MethodIlOperandInfo("parameter", parameter.ToString(), method.Parameters.IndexOf(parameter), null, null, null, null, null, null, null, null, null, null, null, null);
			case string str:
				return new MethodIlOperandInfo("string", str, null, null, null, null, null, null, null, null, null, str, null, null, null);
			case sbyte value:
				return new MethodIlOperandInfo("int32", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, value, value, null, null, null, null, null);
			case byte value:
				return new MethodIlOperandInfo("int32", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, value, value, null, null, null, null, null);
			case int value:
				return new MethodIlOperandInfo("int32", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, value, value, null, null, null, null, null);
			case long value:
				return new MethodIlOperandInfo("int64", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, null, value, null, null, null, null, null);
			case float value:
				return new MethodIlOperandInfo("float32", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, null, null, value, null, null, null, null);
			case double value:
				return new MethodIlOperandInfo("float64", value.ToString(CultureInfo.InvariantCulture), null, null, null, null, null, null, null, null, value, null, null, null, null);
			case IMethod methodRef when !methodRef.IsField:
				return new MethodIlOperandInfo("method", methodRef.FullName, null, null, null, null, null, null, null, null, null, null, methodRef.DeclaringType?.FullName, methodRef.Name, $"0x{methodRef.MDToken.Raw:X8}");
			case IField fieldRef when !fieldRef.IsMethod:
				return new MethodIlOperandInfo("field", fieldRef.FullName, null, null, null, null, null, null, null, null, null, null, fieldRef.DeclaringType?.FullName, fieldRef.Name, $"0x{fieldRef.MDToken.Raw:X8}");
			case ITypeDefOrRef typeRef:
				return new MethodIlOperandInfo("type", typeRef.FullName, null, null, null, null, null, null, null, null, null, null, typeRef.FullName, null, (typeRef as IMDTokenProvider) is IMDTokenProvider provider ? $"0x{provider.MDToken.Raw:X8}" : null);
			case CallingConventionSig sig:
				return new MethodIlOperandInfo("signature", sig.ToString(), null, null, null, null, null, null, null, null, null, sig.ToString(), null, null, null);
			default:
				return new MethodIlOperandInfo("raw", operand.ToString(), null, null, null, null, null, null, null, null, null, null, null, null, null);
			}
		}

		MethodIlExceptionHandlerInfo ToMethodIlExceptionHandlerInfo(CilBody body, Dictionary<Instruction, int> instructionMap, ExceptionHandler eh, int index) => new MethodIlExceptionHandlerInfo(
			index,
			eh.HandlerType.ToString(),
			TryGetInstructionIndex(instructionMap, eh.TryStart),
			eh.TryStart is null ? null : unchecked((int)eh.TryStart.Offset),
			TryGetInstructionIndex(instructionMap, eh.TryEnd),
			eh.TryEnd is null ? null : unchecked((int)eh.TryEnd.Offset),
			eh.TryEnd is null,
			TryGetInstructionIndex(instructionMap, eh.HandlerStart),
			eh.HandlerStart is null ? null : unchecked((int)eh.HandlerStart.Offset),
			TryGetInstructionIndex(instructionMap, eh.HandlerEnd),
			eh.HandlerEnd is null ? null : unchecked((int)eh.HandlerEnd.Offset),
			eh.HandlerEnd is null,
			TryGetInstructionIndex(instructionMap, eh.FilterStart),
			eh.FilterStart is null ? null : unchecked((int)eh.FilterStart.Offset),
			eh.CatchType?.FullName);

		int? TryGetInstructionIndex(Dictionary<Instruction, int> instructionMap, Instruction? instruction) => instruction is not null && instructionMap.TryGetValue(instruction, out var index) ? index : null;

		void ApplyLocalOperations(MethodIlPatchContext context, MethodIlLocalPatchOperation[]? operations, List<CompilerLikeDiagnostic> diagnostics, bool deleteOnly) {
			foreach (var operation in operations ?? Array.Empty<MethodIlLocalPatchOperation>()) {
				var action = NormalizePatchAction(operation.Action);
				var isDelete = string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase);
				if (isDelete != deleteOnly)
					continue;
				try {
					switch (action) {
					case "add": {
						var local = CreateLocal(operation.Local, context.Method.Module);
						var insertIndex = operation.Index is null ? context.Body.Variables.Count : Math.Clamp(operation.Index.Value, 0, context.Body.Variables.Count);
						context.Body.Variables.Insert(insertIndex, local);
						break;
					}
					case "update": {
						var index = RequireIndex(operation.Index, "local update");
						var local = GetLocalAt(context, index);
						UpdateLocal(local, operation.Local, context.Method.Module);
						break;
					}
					case "delete":
					case "remove": {
						var index = RequireIndex(operation.Index, "local delete");
						var local = GetLocalAt(context, index);
						context.Body.Variables.Remove(local);
						break;
					}
					default:
						diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILLOCAL001", $"Unsupported local operation '{operation.Action}'."));
						break;
					}
				}
				catch (Exception ex) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILLOCAL002", UnwrapToolException(ex).Message));
				}
			}
		}

		void ApplyInstructionOperations(MethodIlPatchContext context, MethodIlInstructionPatchOperation[]? operations, List<CompilerLikeDiagnostic> diagnostics) {
			foreach (var operation in operations ?? Array.Empty<MethodIlInstructionPatchOperation>()) {
				var action = NormalizePatchAction(operation.Action);
				try {
					switch (action) {
					case "append":
						foreach (var newInstruction in CreateInstructions(context, operation.Instructions))
							context.Body.Instructions.Add(newInstruction);
						break;
					case "prepend":
						InsertInstructions(context.Body.Instructions, 0, CreateInstructions(context, operation.Instructions));
						break;
					case "insert-before": {
						var anchorIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "insert-before");
						InsertInstructions(context.Body.Instructions, anchorIndex, CreateInstructions(context, operation.Instructions));
						break;
					}
					case "insert-after": {
						var anchorIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "insert-after");
						InsertInstructions(context.Body.Instructions, anchorIndex + 1, CreateInstructions(context, operation.Instructions));
						break;
					}
					case "replace": {
						var targetIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "replace");
						context.Body.Instructions.RemoveAt(targetIndex);
						InsertInstructions(context.Body.Instructions, targetIndex, CreateInstructions(context, operation.Instructions));
						break;
					}
					case "update": {
						var targetIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "update");
						var existing = context.Body.Instructions[targetIndex];
						ApplyInstructionDefinition(context, existing, SingleInstruction(operation.Instructions, action), allowLabelResolution: true);
						break;
					}
					case "delete":
					case "remove": {
						var targetIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "delete");
						context.Body.Instructions.RemoveAt(targetIndex);
						break;
					}
					case "nop": {
						var targetIndex = ResolveInstructionIndex(context, operation.Index, operation.Offset, "nop");
						var instruction = context.Body.Instructions[targetIndex];
						instruction.OpCode = OpCodes.Nop;
						instruction.Operand = null;
						break;
					}
					default:
						diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILINST001", $"Unsupported instruction operation '{operation.Action}'."));
						break;
					}
				}
				catch (Exception ex) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILINST002", UnwrapToolException(ex).Message));
				}
			}
		}

		void ApplyExceptionHandlerOperations(MethodIlPatchContext context, MethodIlExceptionHandlerPatchOperation[]? operations, List<CompilerLikeDiagnostic> diagnostics) {
			foreach (var operation in operations ?? Array.Empty<MethodIlExceptionHandlerPatchOperation>()) {
				var action = NormalizePatchAction(operation.Action);
				try {
					switch (action) {
					case "add": {
						context.Body.ExceptionHandlers.Insert(operation.Index is null ? context.Body.ExceptionHandlers.Count : Math.Clamp(operation.Index.Value, 0, context.Body.ExceptionHandlers.Count), CreateExceptionHandler(context, operation.ExceptionHandler));
						break;
					}
					case "update": {
						var index = RequireIndex(operation.Index, "exception handler update");
						var eh = GetExceptionHandlerAt(context, index);
						UpdateExceptionHandler(context, eh, operation.ExceptionHandler);
						break;
					}
					case "delete":
					case "remove": {
						var index = RequireIndex(operation.Index, "exception handler delete");
						context.Body.ExceptionHandlers.RemoveAt(index);
						break;
					}
					default:
						diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILEH001", $"Unsupported exception handler operation '{operation.Action}'."));
						break;
					}
				}
				catch (Exception ex) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILEH002", UnwrapToolException(ex).Message));
				}
			}
		}

		void ValidateMethodBody(MethodIlPatchContext context, List<CompilerLikeDiagnostic> diagnostics) {
			var instructions = context.Body.Instructions;
			var instructionSet = new HashSet<Instruction>(instructions);
			foreach (var instruction in instructions) {
				if (instruction.Operand is Instruction target && !instructionSet.Contains(target))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID001", $"Instruction '{instruction.OpCode.Name}' references a branch target that is no longer in the method body."));
				if (instruction.Operand is Instruction[] targets && targets.Any(a => !instructionSet.Contains(a)))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID002", $"Instruction '{instruction.OpCode.Name}' references a switch target that is no longer in the method body."));
				if (instruction.Operand is Local local && !context.Body.Variables.Contains(local))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID003", $"Instruction '{instruction.OpCode.Name}' references a local variable that is no longer in the locals list."));
			}
			foreach (var eh in context.Body.ExceptionHandlers) {
				if (eh.TryStart is not null && !instructionSet.Contains(eh.TryStart))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID004", "Exception handler TryStart is not in the method body."));
				if (eh.TryEnd is not null && !instructionSet.Contains(eh.TryEnd))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID005", "Exception handler TryEnd is not in the method body."));
				if (eh.HandlerStart is not null && !instructionSet.Contains(eh.HandlerStart))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID006", "Exception handler HandlerStart is not in the method body."));
				if (eh.HandlerEnd is not null && !instructionSet.Contains(eh.HandlerEnd))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID007", "Exception handler HandlerEnd is not in the method body."));
				if (eh.FilterStart is not null && !instructionSet.Contains(eh.FilterStart))
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ILVALID008", "Exception handler FilterStart is not in the method body."));
			}
		}

		List<Instruction> CreateInstructions(MethodIlPatchContext context, MethodIlInstructionDefinition[]? definitions) {
			if (definitions is null || definitions.Length == 0)
				throw new InvalidOperationException("At least one instruction definition is required.");
			var created = new List<Instruction>(definitions.Length);
			var labels = new Dictionary<string, Instruction>(StringComparer.OrdinalIgnoreCase);
			foreach (var definition in definitions) {
				var instruction = new Instruction();
				instruction.OpCode = ResolveOpCode(definition.OpCode);
				created.Add(instruction);
				if (!string.IsNullOrWhiteSpace(definition.Label))
					labels[definition.Label.Trim()] = instruction;
			}
			for (int i = 0; i < definitions.Length; i++)
				ApplyInstructionDefinition(context, created[i], definitions[i], allowLabelResolution: true, labels);
			return created;
		}

		void ApplyInstructionDefinition(MethodIlPatchContext context, Instruction instruction, MethodIlInstructionDefinition definition, bool allowLabelResolution, Dictionary<string, Instruction>? labels = null) {
			instruction.OpCode = ResolveOpCode(definition.OpCode);
			instruction.Operand = ResolveInstructionOperand(context, instruction.OpCode, definition.Operand, labels, allowLabelResolution);
		}

		object? ResolveInstructionOperand(MethodIlPatchContext context, OpCode opCode, MethodIlOperandSpec? operandSpec, Dictionary<string, Instruction>? labels, bool allowLabelResolution) {
			switch (opCode.OperandType) {
			case OperandType.InlineNone:
			case OperandType.InlinePhi:
				return null;
			case OperandType.ShortInlineBrTarget:
			case OperandType.InlineBrTarget:
				return ResolveInstructionReference(context, operandSpec, labels, allowLabelResolution, allowBodyEnd: false);
			case OperandType.InlineSwitch:
				return ResolveInstructionReferences(context, operandSpec, labels, allowLabelResolution);
			case OperandType.ShortInlineVar:
			case OperandType.InlineVar:
				return ResolveVariableOperand(context, operandSpec);
			case OperandType.InlineString:
				return operandSpec?.Text ?? throw new InvalidOperationException("String operand requires text.");
			case OperandType.InlineI:
				return operandSpec?.Int32 ?? throw new InvalidOperationException("InlineI operand requires Int32.");
			case OperandType.InlineI8:
				return operandSpec?.Int64 ?? throw new InvalidOperationException("InlineI8 operand requires Int64.");
			case OperandType.InlineR:
				return operandSpec?.Float64 ?? throw new InvalidOperationException("InlineR operand requires Float64.");
			case OperandType.ShortInlineR:
				return operandSpec?.Float32 is float value ? value : operandSpec?.Float64 is double d ? (float)d : throw new InvalidOperationException("ShortInlineR operand requires Float32 or Float64.");
			case OperandType.ShortInlineI:
				return ResolveShortInlineI(opCode, operandSpec);
			case OperandType.InlineType:
				return ResolveTypeOperand(context, operandSpec);
			case OperandType.InlineMethod:
				return ResolveMethodOperand(context, operandSpec);
			case OperandType.InlineField:
				return ResolveFieldOperand(context, operandSpec);
			case OperandType.InlineTok:
				return ResolveTokenOperand(context, operandSpec);
			case OperandType.InlineSig:
				return ResolveInlineSigOperand(context, operandSpec);
			default:
				throw new InvalidOperationException($"Unsupported operand type '{opCode.OperandType}'.");
			}
		}

		CallingConventionSig ResolveInlineSigOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			if (operandSpec is null)
				throw new InvalidOperationException("InlineSig operand requires signature details.");
			var callingConvention = ParseCallingConvention(operandSpec.CallingConventionName, operandSpec.HasThis, operandSpec.ExplicitThis, operandSpec.GenericParameterCount);
			var methodSig = new MethodSig(callingConvention, (uint)Math.Max(operandSpec.GenericParameterCount ?? 0, 0)) {
				RetType = ParseTypeSig(context.Method.Module, operandSpec.ReturnTypeName ?? "void"),
			};
			foreach (var parameterType in operandSpec.ParameterTypes ?? Array.Empty<string>())
				methodSig.Params.Add(ParseTypeSig(context.Method.Module, parameterType));
			foreach (var parameterType in operandSpec.ParameterTypesAfterSentinel ?? Array.Empty<string>())
				methodSig.ParamsAfterSentinel.Add(ParseTypeSig(context.Method.Module, parameterType));
			return methodSig;
		}

		dnlib.DotNet.CallingConvention ParseCallingConvention(string? callingConventionName, bool? hasThis, bool? explicitThis, int? genericParameterCount) {
			var convention = string.IsNullOrWhiteSpace(callingConventionName) ? dnlib.DotNet.CallingConvention.Default : Enum.TryParse(callingConventionName, true, out dnlib.DotNet.CallingConvention parsed) ? parsed : throw new InvalidOperationException($"Invalid calling convention '{callingConventionName}'.");
			convention = hasThis == true ? convention | dnlib.DotNet.CallingConvention.HasThis : convention & ~dnlib.DotNet.CallingConvention.HasThis;
			convention = explicitThis == true ? convention | dnlib.DotNet.CallingConvention.ExplicitThis : convention & ~dnlib.DotNet.CallingConvention.ExplicitThis;
			convention = (genericParameterCount ?? 0) > 0 ? convention | dnlib.DotNet.CallingConvention.Generic : convention & ~dnlib.DotNet.CallingConvention.Generic;
			return convention;
		}

		object ResolveShortInlineI(OpCode opCode, MethodIlOperandSpec? operandSpec) {
			if (operandSpec?.Int32 is not int value)
				throw new InvalidOperationException("ShortInlineI operand requires Int32.");
			return opCode.Code == Code.Unaligned ? (byte)value : (sbyte)value;
		}

		object ResolveVariableOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			if (operandSpec?.Kind is null)
				throw new InvalidOperationException("Variable operand requires kind 'local' or 'parameter'.");
			if (string.Equals(operandSpec.Kind, "local", StringComparison.OrdinalIgnoreCase))
				return GetLocalAt(context, RequireIndex(operandSpec.Index, "local operand"));
			if (string.Equals(operandSpec.Kind, "parameter", StringComparison.OrdinalIgnoreCase))
				return GetParameterAt(context.Method, RequireIndex(operandSpec.Index, "parameter operand"));
			throw new InvalidOperationException($"Unsupported variable operand kind '{operandSpec.Kind}'.");
		}

		Instruction ResolveInstructionReference(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec, Dictionary<string, Instruction>? labels, bool allowLabelResolution, bool allowBodyEnd) {
			if (allowLabelResolution && !string.IsNullOrWhiteSpace(operandSpec?.Label) && labels is not null && labels.TryGetValue(operandSpec.Label.Trim(), out var labeledInstruction))
				return labeledInstruction;
			var index = operandSpec?.TargetInstructionIndex ?? operandSpec?.Index;
			if (index is not null)
				return GetInstructionAt(context, index.Value);
			var offset = operandSpec?.TargetInstructionOffset ?? operandSpec?.Offset;
			if (offset is not null)
				return GetInstructionAtOffset(context, offset.Value);
			if (allowBodyEnd && operandSpec?.BodyEnd == true)
				throw new InvalidOperationException("Body-end references are only valid for exception handler boundaries, not for branch instructions.");
			throw new InvalidOperationException("Instruction operand requires targetInstructionIndex, targetInstructionOffset, or label.");
		}

		Instruction[] ResolveInstructionReferences(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec, Dictionary<string, Instruction>? labels, bool allowLabelResolution) {
			if (operandSpec?.TargetInstructionIndices is not null)
				return operandSpec.TargetInstructionIndices.Select(a => GetInstructionAt(context, a)).ToArray();
			if (operandSpec?.TargetInstructionOffsets is not null)
				return operandSpec.TargetInstructionOffsets.Select(a => GetInstructionAtOffset(context, a)).ToArray();
			throw new InvalidOperationException("Switch operands require targetInstructionIndices or targetInstructionOffsets.");
		}

		ITypeDefOrRef ResolveTypeOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			var typeName = operandSpec?.TypeName ?? operandSpec?.Text;
			if (string.IsNullOrWhiteSpace(typeName))
				throw new InvalidOperationException("Type operand requires typeName.");
			var type = ResolveType(context.Document, typeName!);
			return type.Module == context.Method.Module ? type : context.Method.Module.Import(type);
		}

		IMethod ResolveMethodOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			if (!string.IsNullOrWhiteSpace(operandSpec?.MetadataToken)) {
				var method = ResolveMethodLikeByMetadataToken(context.Document, operandSpec.MetadataToken!);
				return method.Module == context.Method.Module ? method : context.Method.Module.Import(method);
			}
			var declaringTypeName = operandSpec?.DeclaringTypeName ?? operandSpec?.TypeName;
			var memberName = operandSpec?.MemberName ?? operandSpec?.Text;
			if (string.IsNullOrWhiteSpace(declaringTypeName) || string.IsNullOrWhiteSpace(memberName))
				throw new InvalidOperationException("Method operand requires metadataToken or declaringTypeName/memberName.");
			var type = ResolveType(context.Document, declaringTypeName!);
			var resolvedMethod = ResolveMethod(context.Document, type, memberName!, null, operandSpec?.MethodSignature, operandSpec?.ParameterTypes, operandSpec?.ParameterCount);
			return resolvedMethod.Module == context.Method.Module ? resolvedMethod : context.Method.Module.Import(resolvedMethod);
		}

		IField ResolveFieldOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			if (!string.IsNullOrWhiteSpace(operandSpec?.MetadataToken)) {
				if (!TryParseMetadataToken(operandSpec.MetadataToken!, out var rawToken))
					throw new InvalidOperationException($"Invalid field metadata token '{operandSpec.MetadataToken}'.");
				var provider = context.Document.GetModules<ModuleDef>().Select(a => a.ResolveToken(rawToken)).OfType<IField>().FirstOrDefault();
				if (provider is null)
					throw new InvalidOperationException($"Could not resolve field token '{operandSpec.MetadataToken}'.");
				return provider.Module == context.Method.Module ? provider : context.Method.Module.Import(provider);
			}
			var declaringTypeName = operandSpec?.DeclaringTypeName ?? operandSpec?.TypeName;
			var memberName = operandSpec?.MemberName ?? operandSpec?.Text;
			if (string.IsNullOrWhiteSpace(declaringTypeName) || string.IsNullOrWhiteSpace(memberName))
				throw new InvalidOperationException("Field operand requires metadataToken or declaringTypeName/memberName.");
			var type = ResolveType(context.Document, declaringTypeName!);
			var field = ResolveField(type, memberName!);
			return field.Module == context.Method.Module ? field : context.Method.Module.Import(field);
		}

		object ResolveTokenOperand(MethodIlPatchContext context, MethodIlOperandSpec? operandSpec) {
			if (operandSpec is null)
				throw new InvalidOperationException("Token operand requires operand details.");
			return NormalizePatchAction(operandSpec.Kind) switch {
				"type" => ResolveTypeOperand(context, operandSpec),
				"field" => ResolveFieldOperand(context, operandSpec),
				"method" => ResolveMethodOperand(context, operandSpec),
				_ => throw new InvalidOperationException($"InlineTok operand kind '{operandSpec.Kind}' must be 'type', 'field', or 'method'."),
			};
		}

		Local CreateLocal(MethodIlLocalDefinition? definition, ModuleDef ownerModule) {
			var local = new Local(null);
			UpdateLocal(local, definition, ownerModule);
			return local;
		}

		void UpdateLocal(Local local, MethodIlLocalDefinition? definition, ModuleDef ownerModule) {
			if (definition is null)
				throw new InvalidOperationException("Local definition is required.");
			if (!string.IsNullOrWhiteSpace(definition.TypeName))
				local.Type = ParseTypeSig(ownerModule, definition.TypeName);
			if (definition.Name is not null)
				local.Name = definition.Name;
			if (!string.IsNullOrWhiteSpace(definition.Attributes) && Enum.TryParse(definition.Attributes, true, out dnlib.DotNet.Pdb.PdbLocalAttributes attrs))
				local.Attributes = attrs;
		}

		ExceptionHandler CreateExceptionHandler(MethodIlPatchContext context, MethodIlExceptionHandlerDefinition? definition) {
			var eh = new ExceptionHandler();
			UpdateExceptionHandler(context, eh, definition);
			return eh;
		}

		void UpdateExceptionHandler(MethodIlPatchContext context, ExceptionHandler eh, MethodIlExceptionHandlerDefinition? definition) {
			if (definition is null)
				throw new InvalidOperationException("Exception handler definition is required.");
			if (!string.IsNullOrWhiteSpace(definition.HandlerType)) {
				if (!Enum.TryParse(definition.HandlerType, true, out ExceptionHandlerType handlerType))
					throw new InvalidOperationException($"Invalid exception handler type '{definition.HandlerType}'.");
				eh.HandlerType = handlerType;
			}
			if (definition.TryStartIndex is not null || definition.TryStartOffset is not null || definition.TryStartBodyEnd is not null)
				eh.TryStart = ResolveExceptionBoundary(context, definition.TryStartIndex, definition.TryStartOffset, definition.TryStartBodyEnd, "TryStart");
			if (definition.TryEndIndex is not null || definition.TryEndOffset is not null || definition.TryEndBodyEnd is not null)
				eh.TryEnd = ResolveExceptionBoundary(context, definition.TryEndIndex, definition.TryEndOffset, definition.TryEndBodyEnd, "TryEnd");
			if (definition.HandlerStartIndex is not null || definition.HandlerStartOffset is not null || definition.HandlerStartBodyEnd is not null)
				eh.HandlerStart = ResolveExceptionBoundary(context, definition.HandlerStartIndex, definition.HandlerStartOffset, definition.HandlerStartBodyEnd, "HandlerStart");
			if (definition.HandlerEndIndex is not null || definition.HandlerEndOffset is not null || definition.HandlerEndBodyEnd is not null)
				eh.HandlerEnd = ResolveExceptionBoundary(context, definition.HandlerEndIndex, definition.HandlerEndOffset, definition.HandlerEndBodyEnd, "HandlerEnd");
			if (definition.FilterStartIndex is not null || definition.FilterStartOffset is not null || definition.FilterStartBodyEnd is not null)
				eh.FilterStart = ResolveExceptionBoundary(context, definition.FilterStartIndex, definition.FilterStartOffset, definition.FilterStartBodyEnd, "FilterStart");
			if (definition.CatchTypeName is not null)
				eh.CatchType = string.IsNullOrWhiteSpace(definition.CatchTypeName) ? null : ResolveTypeOperand(context, new MethodIlOperandSpec("type", TypeName: definition.CatchTypeName));
		}

		Instruction? ResolveExceptionBoundary(MethodIlPatchContext context, int? index, int? offset, bool? bodyEnd, string boundaryName) {
			if (bodyEnd == true)
				return null;
			if (index is not null)
				return GetInstructionAt(context, index.Value);
			if (offset is not null)
				return GetInstructionAtOffset(context, offset.Value);
			if (string.Equals(boundaryName, "FilterStart", StringComparison.Ordinal))
				return null;
			throw new InvalidOperationException($"{boundaryName} requires an instruction index, offset, or bodyEnd=true.");
		}

		TypeSig ParseTypeSig(ModuleDef ownerModule, string? typeName) {
			if (string.IsNullOrWhiteSpace(typeName))
				throw new InvalidOperationException("Type name must not be empty.");
			var trimmed = typeName.Trim();
			if (trimmed.EndsWith("[]", StringComparison.Ordinal))
				return new SZArraySig(ParseTypeSig(ownerModule, trimmed[..^2]));
			if (trimmed.EndsWith("&", StringComparison.Ordinal))
				return new ByRefSig(ParseTypeSig(ownerModule, trimmed[..^1]));
			if (trimmed.EndsWith("*", StringComparison.Ordinal))
				return new PtrSig(ParseTypeSig(ownerModule, trimmed[..^1]));
			return NormalizeSimpleTypeSig(ownerModule, trimmed) ?? TypeNameParser.ParseReflection(ownerModule, trimmed, null).ToTypeSig();
		}

		TypeSig? NormalizeSimpleTypeSig(ModuleDef ownerModule, string typeName) => typeName switch {
			"void" => ownerModule.CorLibTypes.Void,
			"bool" or "System.Boolean" => ownerModule.CorLibTypes.Boolean,
			"char" or "System.Char" => ownerModule.CorLibTypes.Char,
			"sbyte" or "System.SByte" => ownerModule.CorLibTypes.SByte,
			"byte" or "System.Byte" => ownerModule.CorLibTypes.Byte,
			"short" or "System.Int16" => ownerModule.CorLibTypes.Int16,
			"ushort" or "System.UInt16" => ownerModule.CorLibTypes.UInt16,
			"int" or "System.Int32" => ownerModule.CorLibTypes.Int32,
			"uint" or "System.UInt32" => ownerModule.CorLibTypes.UInt32,
			"long" or "System.Int64" => ownerModule.CorLibTypes.Int64,
			"ulong" or "System.UInt64" => ownerModule.CorLibTypes.UInt64,
			"float" or "System.Single" => ownerModule.CorLibTypes.Single,
			"double" or "System.Double" => ownerModule.CorLibTypes.Double,
			"string" or "System.String" => ownerModule.CorLibTypes.String,
			"object" or "System.Object" => ownerModule.CorLibTypes.Object,
			"nint" or "System.IntPtr" => ownerModule.CorLibTypes.IntPtr,
			"nuint" or "System.UIntPtr" => ownerModule.CorLibTypes.UIntPtr,
			_ => null,
		};

		OpCode ResolveOpCode(string? opcodeName) {
			if (string.IsNullOrWhiteSpace(opcodeName))
				throw new InvalidOperationException("OpCode must not be empty.");
			var normalized = opcodeName.Trim();
			var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
			foreach (var field in fields) {
				if (field.GetValue(null) is not OpCode opCode)
					continue;
				if (string.Equals(opCode.Name, normalized, StringComparison.OrdinalIgnoreCase) || string.Equals(field.Name, normalized.Replace('.', '_').Replace('-', '_'), StringComparison.OrdinalIgnoreCase))
					return opCode;
			}
			throw new InvalidOperationException($"Unknown IL opcode '{opcodeName}'.");
		}

		string NormalizePatchAction(string? action) => (action ?? string.Empty).Trim().ToLowerInvariant();

		MethodIlInstructionDefinition SingleInstruction(MethodIlInstructionDefinition[]? definitions, string action) {
			if (definitions is null || definitions.Length != 1)
				throw new InvalidOperationException($"Instruction operation '{action}' requires exactly one instruction definition.");
			return definitions[0];
		}

		int ResolveInstructionIndex(MethodIlPatchContext context, int? index, int? offset, string operationName) {
			if (index is not null)
				return RequireInstructionIndex(context, index.Value, operationName);
			if (offset is not null)
				return RequireInstructionIndex(context, GetInstructionAtOffset(context, offset.Value), operationName);
			throw new InvalidOperationException($"Instruction operation '{operationName}' requires index or offset.");
		}

		int RequireInstructionIndex(MethodIlPatchContext context, int index, string operationName) {
			if ((uint)index >= (uint)context.Body.Instructions.Count)
				throw new InvalidOperationException($"Instruction index {index} is out of range for operation '{operationName}'.");
			return index;
		}

		int RequireInstructionIndex(MethodIlPatchContext context, Instruction instruction, string operationName) {
			var index = context.Body.Instructions.IndexOf(instruction);
			if (index < 0)
				throw new InvalidOperationException($"Instruction reference for operation '{operationName}' is not present in the current method body.");
			return index;
		}

		Instruction GetInstructionAt(MethodIlPatchContext context, int index) {
			if ((uint)index >= (uint)context.Body.Instructions.Count)
				throw new InvalidOperationException($"Instruction index {index} is out of range.");
			return context.Body.Instructions[index];
		}

		Instruction GetInstructionAtOffset(MethodIlPatchContext context, int offset) {
			context.Body.UpdateInstructionOffsets();
			var instruction = context.Body.Instructions.FirstOrDefault(a => a.Offset == offset);
			return instruction ?? throw new InvalidOperationException($"Could not find an instruction at IL offset 0x{offset:X4}.");
		}

		Local GetLocalAt(MethodIlPatchContext context, int index) {
			if ((uint)index >= (uint)context.Body.Variables.Count)
				throw new InvalidOperationException($"Local index {index} is out of range.");
			return context.Body.Variables[index];
		}

		Parameter GetParameterAt(MethodDef method, int index) {
			if ((uint)index >= (uint)method.Parameters.Count)
				throw new InvalidOperationException($"Parameter index {index} is out of range.");
			return method.Parameters[index];
		}

		ExceptionHandler GetExceptionHandlerAt(MethodIlPatchContext context, int index) {
			if ((uint)index >= (uint)context.Body.ExceptionHandlers.Count)
				throw new InvalidOperationException($"Exception handler index {index} is out of range.");
			return context.Body.ExceptionHandlers[index];
		}

		void InsertInstructions(IList<Instruction> instructions, int index, IList<Instruction> newInstructions) {
			for (int i = 0; i < newInstructions.Count; i++)
				instructions.Insert(index + i, newInstructions[i]);
		}

		void TryOptimizeMethodBody(CilBody body) {
			var simplify = typeof(CilBody).GetMethod("SimplifyBranches", BindingFlags.Instance | BindingFlags.Public);
			simplify?.Invoke(body, null);
			var optimize = typeof(CilBody).GetMethod("OptimizeBranches", BindingFlags.Instance | BindingFlags.Public);
			optimize?.Invoke(body, null);
		}

		int RequireIndex(int? index, string purpose) => index ?? throw new InvalidOperationException($"An index is required for {purpose}.");

		sealed class MethodIlPatchContext {
			public IDsDocument Document { get; }
			public MethodDef Method { get; }
			public CilBody Body { get; }
			public MethodIlPatchContext(IDsDocument document, MethodDef method, CilBody body) {
				Document = document;
				Method = method;
				Body = body;
			}
		}

		sealed class AddTypeCompilerSession : IDisposable {
			public ILanguageCompiler Compiler { get; }
			readonly CompilerReferenceSession references;
			public AddTypeCompilerSession(ILanguageCompiler compiler, CompilerReferenceSession references) {
				Compiler = compiler;
				this.references = references;
			}
			public void Dispose() {
				Compiler.Dispose();
				references.Dispose();
			}
		}

		unsafe sealed class CompilerReferenceSession : IDisposable, IAssemblyReferenceResolver {
			readonly ModuleDef editedModule;
			readonly List<AllocatedCompilerMetadataReference> allocatedReferences = new List<AllocatedCompilerMetadataReference>();
			readonly Dictionary<string, CompilerMetadataReference> assemblyReferences = new Dictionary<string, CompilerMetadataReference>(StringComparer.OrdinalIgnoreCase);
			readonly HashSet<string> visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public CompilerMetadataReference[] MetadataReferences => allocatedReferences.Select(a => a.Reference).ToArray();

			public CompilerReferenceSession(ModuleDef editedModule, IEnumerable<string> extraAssemblyReferences) {
				this.editedModule = editedModule;
				AddEditedModuleReference();
				foreach (var asmRef in editedModule.GetAssemblyRefs())
					Resolve(asmRef);
				foreach (var extra in extraAssemblyReferences ?? Array.Empty<string>()) {
					if (string.IsNullOrWhiteSpace(extra))
						continue;
					var parsed = new AssemblyNameInfo(extra.Trim());
					Resolve(parsed.ToAssemblyRef());
				}
			}

			public CompilerMetadataReference? Resolve(IAssembly asmRef) {
				var key = asmRef.FullName;
				if (assemblyReferences.TryGetValue(key, out var existing))
					return existing;
				var resolvedAssembly = editedModule.Context.AssemblyResolver.Resolve(asmRef, editedModule);
				if (resolvedAssembly is null)
					return null;
				var reference = AddModuleReference(resolvedAssembly.ManifestModule, preferCurrentState: false);
				if (reference is not null)
					assemblyReferences[key] = reference.Value;
				return reference;
			}

			void AddEditedModuleReference() {
				var reference = AddModuleReference(editedModule, preferCurrentState: true);
				if (reference is null)
					throw new InvalidOperationException($"Could not create a compiler metadata reference for module '{editedModule.Name}'.");
				if (editedModule.Assembly is not null)
					assemblyReferences[editedModule.Assembly.FullName] = reference.Value;
			}

			CompilerMetadataReference? AddModuleReference(ModuleDef module, bool preferCurrentState) {
				var referenceBytes = GetReferenceBytes(module, preferCurrentState, out var filename);
				if (referenceBytes is null || referenceBytes.Length == 0)
					return null;
				var allocated = new AllocatedCompilerMetadataReference(referenceBytes, module.IsManifestModule, module.Assembly, filename);
				allocatedReferences.Add(allocated);
				return allocated.Reference;
			}

			byte[]? GetReferenceBytes(ModuleDef module, bool preferCurrentState, out string? filename) {
				filename = string.IsNullOrWhiteSpace(module.Location) ? null : module.Location;
				if (!preferCurrentState && filename is not null && visitedFiles.Add(filename) && File.Exists(filename))
					return File.ReadAllBytes(filename);
				using var ms = new MemoryStream();
				module.Write(ms, new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance });
				return ms.ToArray();
			}

			public void Dispose() {
				foreach (var reference in allocatedReferences)
					reference.Dispose();
				allocatedReferences.Clear();
			}
		}

		unsafe sealed class AllocatedCompilerMetadataReference : IDisposable {
			readonly IntPtr buffer;
			public CompilerMetadataReference Reference { get; }
			public AllocatedCompilerMetadataReference(byte[] bytes, bool isAssemblyReference, IAssembly? assembly, string? filename) {
				buffer = Marshal.AllocHGlobal(bytes.Length);
				Marshal.Copy(bytes, 0, buffer, bytes.Length);
				Reference = isAssemblyReference ? CompilerMetadataReference.CreateAssemblyReference(buffer.ToPointer(), bytes.Length, assembly, filename) : CompilerMetadataReference.CreateModuleReference(buffer.ToPointer(), bytes.Length, assembly, filename);
			}
			public void Dispose() {
				if (buffer != IntPtr.Zero)
					Marshal.FreeHGlobal(buffer);
			}
		}

		bool TryApplyClrVersionPreset(ModuleDef module, string value, out string? error) {
			error = null;
			switch (value) {
			case "1.0":
				module.Cor20HeaderRuntimeVersion = 0x00020000;
				module.TablesHeaderVersion = 0x0100;
				module.RuntimeVersion = MDHeaderRuntimeVersion.MS_CLR_10;
				return true;
			case "1.1":
				module.Cor20HeaderRuntimeVersion = 0x00020000;
				module.TablesHeaderVersion = 0x0100;
				module.RuntimeVersion = MDHeaderRuntimeVersion.MS_CLR_11;
				return true;
			case "2.0":
			case "2.0-3.5":
			case "2.0 - 3.5":
				module.Cor20HeaderRuntimeVersion = 0x00020005;
				module.TablesHeaderVersion = 0x0200;
				module.RuntimeVersion = MDHeaderRuntimeVersion.MS_CLR_20;
				return true;
			case "4.0":
			case "4.0-4.8":
			case "4.0 - 4.8":
				module.Cor20HeaderRuntimeVersion = 0x00020005;
				module.TablesHeaderVersion = 0x0200;
				module.RuntimeVersion = MDHeaderRuntimeVersion.MS_CLR_40;
				return true;
			default:
				error = $"Invalid clrVersion '{value}'. Expected 1.0, 1.1, 2.0, or 4.0.";
				return false;
			}
		}

		ModuleEditResult CreateModuleEditSuccess(string message, ModuleDef module) => new ModuleEditResult(
			true,
			message,
			module.Name,
			module.Kind.ToString(),
			module.RuntimeVersion,
			$"0x{module.TablesHeaderVersion:X}",
			$"0x{module.Cor20HeaderRuntimeVersion:X}",
			module.Machine.ToString(),
			module.ManagedEntryPoint is not null ? "managed" : module.NativeEntryPoint != 0 ? "native" : "none",
			module.ManagedEntryPoint is MethodDef method ? method.FullName : null,
			module.NativeEntryPoint == 0 ? null : $"0x{((uint)module.NativeEntryPoint):X}");

		ModuleEditResult CreateModuleEditFailure(string message, ModuleDef module) => new ModuleEditResult(
			false,
			message,
			module.Name,
			module.Kind.ToString(),
			module.RuntimeVersion,
			$"0x{module.TablesHeaderVersion:X}",
			$"0x{module.Cor20HeaderRuntimeVersion:X}",
			module.Machine.ToString(),
			module.ManagedEntryPoint is not null ? "managed" : module.NativeEntryPoint != 0 ? "native" : "none",
			module.ManagedEntryPoint is MethodDef method ? method.FullName : null,
			module.NativeEntryPoint == 0 ? null : $"0x{((uint)module.NativeEntryPoint):X}");

		ModuleInfoResult ToModuleInfoResult(IDsDocument document, ModuleDef module) => new ModuleInfoResult(
			document.Filename,
			module.Name,
			module.Kind.ToString(),
			GetClrVersionDisplay(module),
			module.Mvid,
			module.EncId,
			module.EncBaseId,
			module.ManagedEntryPoint is not null ? "managed" : module.NativeEntryPoint != 0 ? "native" : "none",
			module.ManagedEntryPoint is MethodDef method ? method.FullName : null,
			module.ManagedEntryPoint is MethodDef epMethod ? $"0x{epMethod.MDToken.Raw:X8}" : null,
			module.NativeEntryPoint == 0 ? null : $"0x{((uint)module.NativeEntryPoint):X}",
			module.RuntimeVersion,
			$"0x{module.TablesHeaderVersion:X}",
			$"0x{module.Cor20HeaderRuntimeVersion:X}",
			module.Machine.ToString(),
			(module.Characteristics & Characteristics.RelocsStripped) != 0,
			(module.Characteristics & Characteristics.ExecutableImage) != 0,
			(module.Characteristics & Characteristics.LineNumsStripped) != 0,
			(module.Characteristics & Characteristics.LocalSymsStripped) != 0,
			(module.Characteristics & Characteristics.AggressiveWsTrim) != 0,
			(module.Characteristics & Characteristics.LargeAddressAware) != 0,
			(module.Characteristics & Characteristics.Reserved1) != 0,
			(module.Characteristics & Characteristics.BytesReversedLo) != 0,
			(module.Characteristics & Characteristics.Bit32Machine) != 0,
			(module.Characteristics & Characteristics.DebugStripped) != 0,
			(module.Characteristics & Characteristics.RemovableRunFromSwap) != 0,
			(module.Characteristics & Characteristics.NetRunFromSwap) != 0,
			(module.Characteristics & Characteristics.System) != 0,
			(module.Characteristics & Characteristics.Dll) != 0,
			(module.Characteristics & Characteristics.UpSystemOnly) != 0,
			(module.Characteristics & Characteristics.BytesReversedHi) != 0,
			(module.DllCharacteristics & DllCharacteristics.Reserved1) != 0,
			(module.DllCharacteristics & DllCharacteristics.Reserved2) != 0,
			(module.DllCharacteristics & DllCharacteristics.Reserved3) != 0,
			(module.DllCharacteristics & DllCharacteristics.Reserved4) != 0,
			(module.DllCharacteristics & DllCharacteristics.Reserved5) != 0,
			(module.DllCharacteristics & DllCharacteristics.HighEntropyVA) != 0,
			(module.DllCharacteristics & DllCharacteristics.DynamicBase) != 0,
			(module.DllCharacteristics & DllCharacteristics.ForceIntegrity) != 0,
			(module.DllCharacteristics & DllCharacteristics.NxCompat) != 0,
			(module.DllCharacteristics & DllCharacteristics.NoIsolation) != 0,
			(module.DllCharacteristics & DllCharacteristics.NoSeh) != 0,
			(module.DllCharacteristics & DllCharacteristics.NoBind) != 0,
			(module.DllCharacteristics & DllCharacteristics.AppContainer) != 0,
			(module.DllCharacteristics & DllCharacteristics.WdmDriver) != 0,
			(module.DllCharacteristics & DllCharacteristics.GuardCf) != 0,
			(module.DllCharacteristics & DllCharacteristics.TerminalServerAware) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.ILOnly) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.Bit32Required) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.ILLibrary) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.Bit32Preferred) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.TrackDebugData) != 0,
			(module.Cor20HeaderFlags & ComImageFlags.StrongNameSigned) != 0,
			module.CustomAttributes.Count);

		string GetClrVersionDisplay(ModuleDef module) {
			if (module.IsClr10)
				return "1.0";
			if (module.IsClr11)
				return "1.1";
			if (module.IsClr20)
				return "2.0 - 3.5";
			if (module.IsClr40)
				return "4.0 - 4.8";
			return "Unknown";
		}

		bool TryParseHexBytes(string text, out byte[] bytes, out string? error) {
			bytes = Array.Empty<byte>();
			error = null;
			var normalized = (text ?? string.Empty).Trim();
			if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				normalized = normalized.Substring(2);
			normalized = normalized.Replace(" ", string.Empty).Replace("-", string.Empty);
			if (normalized.Length == 0) {
				bytes = Array.Empty<byte>();
				return true;
			}
			if ((normalized.Length & 1) != 0) {
				error = "Hex string length must be even.";
				return false;
			}
			var data = new byte[normalized.Length / 2];
			for (int i = 0; i < data.Length; i++) {
				if (!byte.TryParse(normalized.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) {
					error = $"Invalid hex byte at index {i}.";
					return false;
				}
				data[i] = b;
			}
			bytes = data;
			return true;
		}

		bool TryParseProcessorArch(string value, out AssemblyAttributes bits) {
			bits = 0;
			switch (value.ToLowerInvariant()) {
			case "none": bits = AssemblyAttributes.PA_None; return true;
			case "msil": bits = AssemblyAttributes.PA_MSIL; return true;
			case "x86": bits = AssemblyAttributes.PA_x86; return true;
			case "ia64": bits = AssemblyAttributes.PA_IA64; return true;
			case "amd64": bits = AssemblyAttributes.PA_AMD64; return true;
			case "arm": bits = AssemblyAttributes.PA_ARM; return true;
			case "arm64": bits = AssemblyAttributes.PA_ARM64; return true;
			case "noplatform": bits = AssemblyAttributes.PA_NoPlatform; return true;
			default: return false;
			}
		}

		string GetProcessorArchName(AssemblyAttributes attrs) {
			var pa = attrs & AssemblyAttributes.PA_Mask;
			if (pa == AssemblyAttributes.PA_None) return "None";
			if (pa == AssemblyAttributes.PA_MSIL) return "MSIL";
			if (pa == AssemblyAttributes.PA_x86) return "x86";
			if (pa == AssemblyAttributes.PA_IA64) return "IA64";
			if (pa == AssemblyAttributes.PA_AMD64) return "AMD64";
			if (pa == AssemblyAttributes.PA_ARM) return "ARM";
			if (pa == AssemblyAttributes.PA_ARM64) return "ARM64";
			if (pa == AssemblyAttributes.PA_NoPlatform) return "NoPlatform";
			return pa.ToString();
		}

		bool TryParseContentType(string value, out AssemblyAttributes bits) {
			bits = 0;
			switch (value.ToLowerInvariant()) {
			case "default": bits = AssemblyAttributes.ContentType_Default; return true;
			case "windowsruntime": bits = AssemblyAttributes.ContentType_WindowsRuntime; return true;
			default: return false;
			}
		}

		string GetContentTypeName(AssemblyAttributes attrs) {
			var ct = attrs & AssemblyAttributes.ContentType_Mask;
			if (ct == AssemblyAttributes.ContentType_Default) return "Default";
			if (ct == AssemblyAttributes.ContentType_WindowsRuntime) return "WindowsRuntime";
			return ct.ToString();
		}

		AssemblyCustomAttributeInfo ToAssemblyCustomAttributeInfo(int index, CustomAttribute attribute) {
			var ctorName = attribute.Constructor?.FullName ?? string.Empty;
			var token = (attribute.Constructor as IMethodDefOrRef)?.MDToken.Raw ?? 0;
			var args = attribute.ConstructorArguments.Select(a => CAArgumentToDisplayText(a)).ToArray();
			return new AssemblyCustomAttributeInfo(index, ctorName, token == 0 ? null : $"0x{token:X8}", args, attribute.ToString());
		}

		ModuleCustomAttributeInfo ToModuleCustomAttributeInfo(int index, CustomAttribute attribute) {
			var ctorName = attribute.Constructor?.FullName ?? string.Empty;
			var token = (attribute.Constructor as IMethodDefOrRef)?.MDToken.Raw ?? 0;
			var args = attribute.ConstructorArguments.Select(a => CAArgumentToDisplayText(a)).ToArray();
			return new ModuleCustomAttributeInfo(index, ctorName, token == 0 ? null : $"0x{token:X8}", args, attribute.ToString());
		}

		string CAArgumentToDisplayText(CAArgument arg) {
			if (arg.Value is null)
				return "null";
			if (arg.Value is UTF8String utf8)
				return utf8.String ?? string.Empty;
			return Convert.ToString(arg.Value, CultureInfo.InvariantCulture) ?? string.Empty;
		}

		bool TryCreateCAArgument(TypeSig typeSig, string text, out CAArgument argument, out string? error) {
			argument = default;
			error = null;
			var elementType = typeSig.RemovePinnedAndModifiers().GetElementType();
			object? value;
			switch (elementType) {
			case ElementType.Boolean:
				if (!bool.TryParse(text, out var b)) { error = $"Could not parse bool '{text}'."; return false; }
				value = b;
				break;
			case ElementType.Char:
				if (text.Length != 1) { error = "Char argument must be exactly one character."; return false; }
				value = text[0];
				break;
			case ElementType.I1:
				if (!sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i1)) { error = $"Could not parse sbyte '{text}'."; return false; }
				value = i1;
				break;
			case ElementType.U1:
				if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u1)) { error = $"Could not parse byte '{text}'."; return false; }
				value = u1;
				break;
			case ElementType.I2:
				if (!short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i2)) { error = $"Could not parse short '{text}'."; return false; }
				value = i2;
				break;
			case ElementType.U2:
				if (!ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u2)) { error = $"Could not parse ushort '{text}'."; return false; }
				value = u2;
				break;
			case ElementType.I4:
				if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i4)) { error = $"Could not parse int '{text}'."; return false; }
				value = i4;
				break;
			case ElementType.U4:
				if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u4)) { error = $"Could not parse uint '{text}'."; return false; }
				value = u4;
				break;
			case ElementType.I8:
				if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i8)) { error = $"Could not parse long '{text}'."; return false; }
				value = i8;
				break;
			case ElementType.U8:
				if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u8)) { error = $"Could not parse ulong '{text}'."; return false; }
				value = u8;
				break;
			case ElementType.R4:
				if (!float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var r4)) { error = $"Could not parse float '{text}'."; return false; }
				value = r4;
				break;
			case ElementType.R8:
				if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var r8)) { error = $"Could not parse double '{text}'."; return false; }
				value = r8;
				break;
			case ElementType.String:
				value = text;
				break;
			default:
				error = $"Unsupported constructor argument type '{typeSig.TypeName}'.";
				return false;
			}

			argument = new CAArgument(typeSig, value);
			return true;
		}

		readonly struct ParsedAssemblyAttributeLine {
			public string Name { get; }
			public string[] Arguments { get; }
			public ParsedAssemblyAttributeLine(string name, string[] arguments) {
				Name = name;
				Arguments = arguments;
			}
		}

		IEnumerable<ParsedAssemblyAttributeLine> ParseAssemblyAttributeLines(string source, List<CompilerLikeDiagnostic> diagnostics) {
			var lines = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
			var regex = new Regex("^\\s*\\[assembly\\s*:\\s*([A-Za-z0-9_\\.]+)\\s*(?:\\((.*)\\))?\\s*\\]\\s*$", RegexOptions.Compiled);
			for (int i = 0; i < lines.Length; i++) {
				var line = lines[i];
				if (string.IsNullOrWhiteSpace(line))
					continue;
				if (!line.Contains("[assembly:", StringComparison.OrdinalIgnoreCase) && !line.Contains("[module:", StringComparison.OrdinalIgnoreCase))
					continue;
				if (line.Contains("[module:", StringComparison.OrdinalIgnoreCase)) {
					diagnostics.Add(new CompilerLikeDiagnostic("Warning", "ASMED_CS_MOD", $"Module-level attribute on line {i + 1} is currently ignored."));
					continue;
				}
				var match = regex.Match(line);
				if (!match.Success) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ASMED_CS_PARSE", $"Could not parse assembly attribute line {i + 1}."));
					continue;
				}
				var name = match.Groups[1].Value.Trim();
				var args = SplitAttributeArguments(match.Groups[2].Success ? match.Groups[2].Value : string.Empty).ToArray();
				yield return new ParsedAssemblyAttributeLine(name, args);
			}
		}

		IEnumerable<string> SplitAttributeArguments(string argsText) {
			if (string.IsNullOrWhiteSpace(argsText))
				yield break;
			var sb = new StringBuilder();
			var inString = false;
			for (int i = 0; i < argsText.Length; i++) {
				var ch = argsText[i];
				if (ch == '"' && (i == 0 || argsText[i - 1] != '\\'))
					inString = !inString;
				if (ch == ',' && !inString) {
					yield return TrimAttributeArgument(sb.ToString());
					sb.Clear();
					continue;
				}
				sb.Append(ch);
			}
			if (sb.Length != 0)
				yield return TrimAttributeArgument(sb.ToString());
		}

		string TrimAttributeArgument(string value) {
			var trimmed = value.Trim();
			if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
				return trimmed.Substring(1, trimmed.Length - 2).Replace("\\\"", "\"");
			return trimmed;
		}

		bool ApplyKnownAssemblyAttribute(IDsDocument document, AssemblyDef asm, string attributeName, string[] args, List<CompilerLikeDiagnostic> diagnostics) {
			var simpleName = attributeName.Contains('.') ? attributeName.Substring(attributeName.LastIndexOf('.') + 1) : attributeName;
			switch (simpleName) {
			case "AssemblyVersion":
				if (args.Length != 1 || !Version.TryParse(args[0], out var v)) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ASMED_CS_VER", $"AssemblyVersion requires one version string argument."));
					return false;
				}
				asm.Version = v;
				return true;
			case "AssemblyTitle":
			case "AssemblyProduct":
			case "AssemblyCompany":
			case "AssemblyConfiguration":
			case "AssemblyCopyright":
			case "AssemblyFileVersion":
			case "AssemblyInformationalVersion":
			case "AssemblyDefaultAlias":
			case "NeutralResourcesLanguage":
				return TryApplyAssemblyCustomAttributeByName(document, asm, attributeName, args, diagnostics);
			case "AssemblyMetadata":
				return TryApplyAssemblyCustomAttributeByName(document, asm, attributeName, args, diagnostics);
			default:
				return TryApplyAssemblyCustomAttributeByName(document, asm, attributeName, args, diagnostics);
			}
		}

		bool TryApplyAssemblyCustomAttributeByName(IDsDocument document, AssemblyDef asm, string attributeName, string[] args, List<CompilerLikeDiagnostic> diagnostics) {
			var ctor = ResolveAttributeConstructorByName(document, attributeName, args.Length);
			if (ctor is null) {
				diagnostics.Add(new CompilerLikeDiagnostic("Warning", "ASMED_CS_UNSUPPORTED", $"Unsupported or unresolved assembly attribute '{attributeName}'."));
				return false;
			}

			var attribute = new CustomAttribute(ctor);
			for (int i = 0; i < args.Length; i++) {
				var param = ctor.MethodSig!.Params[i];
				if (!TryCreateCAArgument(param, args[i], out var caArg, out var parseError)) {
					diagnostics.Add(new CompilerLikeDiagnostic("Error", "ASMED_CS_ARGPARSE", parseError ?? $"Could not parse argument {i} for attribute '{attributeName}'."));
					return false;
				}
				attribute.ConstructorArguments.Add(caArg);
			}

			asm.CustomAttributes.Add(attribute);
			return true;
		}

		IMethodDefOrRef? ResolveAttributeConstructorByName(IDsDocument document, string attributeName, int parameterCount) {
			var normalized = attributeName.Trim();
			var normalizedWithSuffix = normalized.EndsWith("Attribute", StringComparison.Ordinal) ? normalized : normalized + "Attribute";
			var candidates = new[] { normalized, normalizedWithSuffix };

			foreach (var module in documentService.GetDocuments().SelectMany(a => a.GetModules<ModuleDef>())) {
				foreach (var type in module.GetTypes()) {
					if (!candidates.Any(c => string.Equals(type.FullName, c, StringComparison.OrdinalIgnoreCase) || string.Equals(type.Name, c, StringComparison.OrdinalIgnoreCase)))
						continue;
					var ctor = type.Methods.FirstOrDefault(m => string.Equals(m.Name, ".ctor", StringComparison.Ordinal) && GetVisibleParameterCount(m) == parameterCount);
					if (ctor is not null)
						return ctor;
				}
			}

			return null;
		}

		IMethodDefOrRef ResolveMethodLikeByMetadataToken(IDsDocument document, string metadataToken) {
			if (!TryParseMetadataToken(metadataToken, out var rawToken))
				throw new InvalidOperationException($"Invalid metadata token '{metadataToken}'. Expected a hex token such as 0x06001234 or 0x0A001234.");

			foreach (var module in document.GetModules<ModuleDef>()) {
				if (module.ResolveToken(rawToken) is IMethodDefOrRef method)
					return method;
			}

			foreach (var module in documentService.GetDocuments().SelectMany(a => a.GetModules<ModuleDef>())) {
				if (module.ResolveToken(rawToken) is IMethodDefOrRef method)
					return method;
			}

			throw new InvalidOperationException($"Could not resolve metadata token '{metadataToken}'.");
		}

		bool TryParseMetadataToken(string metadataToken, out uint rawToken) {
			rawToken = 0;
			var tokenText = (metadataToken ?? string.Empty).Trim();
			if (tokenText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				tokenText = tokenText.Substring(2);
			if (uint.TryParse(tokenText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rawToken))
				return true;
			if (uint.TryParse(tokenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out rawToken))
				return true;
			return false;
		}

			bool IsExpectedToolException(Exception ex) =>
			ex is ArgumentException ||
			ex is InvalidOperationException ||
			ex is Win32Exception ||
			ex is FileNotFoundException ||
			ex is DirectoryNotFoundException ||
			ex is NotSupportedException;

			bool IsUsableAttachableProcess(AttachableProcess attachableProcess) =>
				attachableProcess.ProcessId > 0 &&
				!string.IsNullOrWhiteSpace(attachableProcess.Name) &&
				!string.IsNullOrWhiteSpace(attachableProcess.RuntimeName);

			AttachableProcess[] GetAttachableProcessesSafe(string[]? processNames, int[]? processIds, string[]? providerNames) {
				var normalizedProviderNames = providerNames?
					.Where(a => !string.IsNullOrWhiteSpace(a))
					.Select(a => a.Trim())
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray();

				if (normalizedProviderNames is not { Length: > 0 }) {
					normalizedProviderNames = new[] {
						PredefinedAttachProgramOptionsProviderNames.DotNet,
						PredefinedAttachProgramOptionsProviderNames.DotNetFramework,
						PredefinedAttachProgramOptionsProviderNames.UnityEditor,
						PredefinedAttachProgramOptionsProviderNames.UnityPlayer,
					};
				}

				var results = new List<AttachableProcess>();
				Exception? lastException = null;
				foreach (var providerName in normalizedProviderNames) {
					try {
						var providerResults = attachableProcessesService.GetAttachableProcessesAsync(processNames, processIds, new[] { providerName }, CancellationToken.None).GetAwaiter().GetResult();
						results.AddRange(providerResults);
					}
					catch (Exception ex) {
						lastException = UnwrapToolException(ex);
						logger.WriteError($"Attach provider '{providerName}' failed during process enumeration: {lastException.Message}");
						if (!IsExpectedToolException(lastException))
							logger.WriteException(lastException);
					}
				}

				if (results.Count == 0 && lastException is not null)
					logger.WriteLine("All attach providers failed or produced no attachable matches; returning an empty result set.");

				return results.ToArray();
			}

			DecompiledTextResult DecompileWithFallback(string? requestedDecompilerName, IDecompiler decompiler, string target, Action<IDecompiler, StringBuilderDecompilerOutput> decompileAction) {
				var output = new StringBuilderDecompilerOutput();
				try {
					decompileAction(decompiler, output);
					return new DecompiledTextResult(decompiler.UniqueNameUI, target, output.GetText());
				}
				catch (Exception ex) {
					var actualException = UnwrapToolException(ex);
					logger.WriteError($"Decompiler '{decompiler.UniqueNameUI}' failed for '{target}': {actualException.Message}");
					if (!IsExpectedToolException(actualException))
						logger.WriteException(actualException);

					var fallbackDecompiler = ResolveFallbackDecompiler(decompiler, requestedDecompilerName);
					if (fallbackDecompiler is null)
						return new DecompiledTextResult(decompiler.UniqueNameUI, target, string.Empty, actualException.Message);

					var fallbackOutput = new StringBuilderDecompilerOutput();
					try {
						decompileAction(fallbackDecompiler, fallbackOutput);
					}
					catch {
					}
					return new DecompiledTextResult(fallbackDecompiler.UniqueNameUI, target, fallbackOutput.GetText(), $"Primary decompiler '{decompiler.UniqueNameUI}' failed: {actualException.Message}");
				}
			}

			IDecompiler? ResolveFallbackDecompiler(IDecompiler primaryDecompiler, string? requestedDecompilerName) {
				if (!string.IsNullOrWhiteSpace(requestedDecompilerName) &&
					string.Equals(primaryDecompiler.GenericNameUI, requestedDecompilerName, StringComparison.OrdinalIgnoreCase))
					return null;

				return decompilerService.AllDecompilers.FirstOrDefault(a =>
					!ReferenceEquals(a, primaryDecompiler) && (
					string.Equals(a.UniqueNameUI, "IL", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(a.GenericNameUI, "IL", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(a.FileExtension, ".il", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(a.FileExtension, "il", StringComparison.OrdinalIgnoreCase)));
			}

		LoadedDocumentInfo ToLoadedDocumentInfo(IDsDocument document) => new LoadedDocumentInfo(
			document.GetShortName(),
			document.Filename,
			document.AssemblyDef?.FullName,
			document.ModuleDef?.FullName,
			document.IsAutoLoaded,
			document.Children.Count);

		TypeInfo ToTypeInfo(TypeDef type) => new TypeInfo(
			type.FullName,
			type.Name,
			type.Namespace,
			type.IsPublic || type.IsNestedPublic,
			type.IsNested,
			type.Module?.Location ?? string.Empty);

		MethodInfoResult ToMethodInfoResult(MethodDef method) => new MethodInfoResult(
			method.Name.String,
			method.FullName,
			method.ReturnType.FullName,
			GetVisibleParameterCount(method),
			GetVisibleParameterTypes(method),
			method.IsStatic,
			method.IsConstructor,
			method.IsVirtual,
			method.IsSpecialName,
			$"0x{method.MDToken.Raw:X8}");

		SearchSymbolResult ToSearchSymbolResult(IDsDocument document, TypeDef type) => new SearchSymbolResult("type", type.Name.String, type.FullName, type.Namespace, type.FullName, document.Filename, $"0x{type.MDToken.Raw:X8}");
		SearchSymbolResult ToSearchSymbolResult(IDsDocument document, TypeDef type, MethodDef method) => new SearchSymbolResult("method", method.Name.String, method.FullName, type.FullName, method.FullName, document.Filename, $"0x{method.MDToken.Raw:X8}");
		SearchSymbolResult ToSearchSymbolResult(IDsDocument document, TypeDef type, FieldDef field) => new SearchSymbolResult("field", field.Name.String, field.FullName, type.FullName, field.FullName, document.Filename, $"0x{field.MDToken.Raw:X8}");
		SearchSymbolResult ToSearchSymbolResult(IDsDocument document, TypeDef type, PropertyDef property) => new SearchSymbolResult("property", property.Name.String, property.FullName, type.FullName, property.FullName, document.Filename, $"0x{property.MDToken.Raw:X8}");
		SearchSymbolResult ToSearchSymbolResult(IDsDocument document, TypeDef type, EventDef ev) => new SearchSymbolResult("event", ev.Name.String, ev.FullName, type.FullName, ev.FullName, document.Filename, $"0x{ev.MDToken.Raw:X8}");

		DebugProcessInfo ToDebugProcessInfo(DbgProcess process) => new DebugProcessInfo(
			process.Id,
			process.Name,
			process.Filename,
			GetDebugProcessDisplayName(process),
			process.State.ToString(),
			process.IsRunning,
			process.Bitness,
			process.Architecture.ToString(),
			process.OperatingSystem.ToString(),
			process.Debugging.ToArray(),
			string.Join(" | ", process.Debugging),
			process.Runtimes.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			process.Runtimes.SelectMany(a => a.AppDomains).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray(),
			process.Threads.Length,
			process.Runtimes.Length);

		DebugModuleInfo ToDebugModuleInfo(DbgModule module) => new DebugModuleInfo(
			module.Process.Id,
			module.Process.Name,
			module.Runtime.Name,
			module.AppDomain?.Name,
			module.Order,
			module.Name,
			module.Filename,
			module.IsExe,
			module.IsDynamic,
			module.IsInMemory,
			module.IsOptimized,
			module.Address,
			module.Size,
			module.Version,
			module.Timestamp);

		DebugThreadInfo ToDebugThreadInfo(DbgThread thread) => new DebugThreadInfo(
			thread.Process.Id,
			thread.Id,
			thread.ManagedId,
			thread.Name,
			thread.UIName,
			thread.Kind,
			thread.IsMain,
			thread.SuspendedCount,
			thread.State.Select(a => a.LocalizedState).ToArray(),
			thread.AppDomain?.Id,
			thread.AppDomain?.Name);

		CallStackFrameInfo ToCallStackFrameInfo(DbgStackFrame frame) => new CallStackFrameInfo(
			frame.Thread.Process.Id,
			frame.Thread.Id,
			frame.Module?.Name,
			frame.Module?.Filename,
			frame.FunctionToken == uint.MaxValue ? null : $"0x{frame.FunctionToken:X8}",
			frame.FunctionOffset,
			frame.Flags.ToString(),
			frame.Location?.Type,
			frame.Location?.ToString());

		BreakpointInfo ToBreakpointInfo(DbgCodeBreakpoint breakpoint) {
			var hitCount = dbgCodeBreakpointHitCountService.GetHitCount(breakpoint);
			var boundMessage = breakpoint.BoundBreakpointsMessage;
			var labels = breakpoint.Labels?.ToArray() ?? Array.Empty<string>();
			var locationDisplay = FormatBreakpointLocationDisplay(breakpoint.Location);
			return new BreakpointInfo(
				breakpoint.Id,
				breakpoint.IsEnabled,
				breakpoint.IsTemporary,
				breakpoint.IsHidden,
				breakpoint.IsOneShot,
				breakpoint.Location.Type,
				locationDisplay,
				breakpoint.BoundBreakpoints.Length,
				hitCount,
				breakpoint.Condition?.ToString(),
				breakpoint.HitCount?.ToString(),
				breakpoint.Filter?.Filter,
				breakpoint.Trace?.Message,
				breakpoint.Trace?.Continue,
				boundMessage.Severity.ToString(),
				boundMessage.Message,
				labels);
		}

		string FormatBreakpointLocationDisplay(DbgCodeLocation location) {
			if (location is DbgDotNetCodeLocation dotNetLocation)
				return FormatDotNetBreakpointLocation(dotNetLocation);
			var display = location.ToString();
			if (!string.IsNullOrWhiteSpace(display) && !display.Contains("Dbg", StringComparison.Ordinal))
				return display;
			return $"{location.Type}";
		}

		string FormatDotNetBreakpointLocation(DbgDotNetCodeLocation location) {
			var method = ResolveMethodByLocation(location);
			if (method is not null)
				return $"{method.FullName} (IL_{location.Offset:X4})";
			var moduleName = string.IsNullOrWhiteSpace(location.Module.ModuleName) ? "<unknown-module>" : Path.GetFileName(location.Module.ModuleName);
			return $"{moduleName}::0x{location.Token:X8} (IL_{location.Offset:X4})";
		}

		MethodDef? ResolveMethodByLocation(DbgDotNetCodeLocation location) {
			foreach (var module in documentService.GetDocuments().SelectMany(a => a.GetModules<ModuleDef>())) {
				if (CreateModuleId(module) != location.Module)
					continue;
				foreach (var method in module.GetTypes().SelectMany(a => a.Methods)) {
					if (method.MDToken.Raw == location.Token)
						return method;
				}
			}
			return null;
		}

		DecompiledTextResult ApplyTextLimit(DecompiledTextResult result, int? maxLength) {
			if (maxLength is null)
				return result;
			var normalizedLength = Math.Max(1, maxLength.Value);
			if (result.Text.Length <= normalizedLength)
				return result;
			var truncatedText = result.Text.Substring(0, normalizedLength);
			var message = result.ErrorMessage;
			if (string.IsNullOrWhiteSpace(message))
				message = $"Output truncated to {normalizedLength} characters.";
			else
				message += $" Output truncated to {normalizedLength} characters.";
			return new DecompiledTextResult(result.Decompiler, result.Target, truncatedText, message, true);
		}

		DebugEventInfo ToDebugEventInfo(McpDebugEventEntry entry) => new DebugEventInfo(entry.Sequence, entry.TimestampUtc, entry.Kind, entry.Severity, entry.Message, entry.IsOutputLine, entry.ProcessId, entry.ProcessName, entry.ProcessFilename, entry.RuntimeName, entry.ThreadId, entry.ModuleName, entry.ModuleFilename, entry.AppDomainName);

		IEnumerable<DbgProcess> EnumerateProcesses(int? processId) {
			if (processId is null)
				return dbgManager.Processes;
			var process = dbgManager.Processes.FirstOrDefault(a => a.Id == processId.Value);
			if (process is null)
				throw new InvalidOperationException($"Could not find debug process with id {processId.Value}.");
			return new[] { process };
		}

		DbgThread? ResolveDebugThread(int? processId, ulong? threadId, ulong? managedThreadId) {
			if (threadId is null && managedThreadId is null)
				return dbgManager.CurrentThread.Current ?? dbgCallStackService.Thread;
			var threads = EnumerateProcesses(processId).SelectMany(a => a.Threads);
			if (threadId is not null)
				threads = threads.Where(a => a.Id == threadId.Value);
			if (managedThreadId is not null)
				threads = threads.Where(a => a.ManagedId == managedThreadId.Value);
			var matches = threads.ToArray();
			if (matches.Length == 1)
				return matches[0];
			if (matches.Length == 0)
				throw new InvalidOperationException("Could not find a matching debug thread.");
			throw new InvalidOperationException("Multiple debug threads matched the supplied identifiers. Pass processId together with threadId or managedThreadId.");
		}

		IEnumerable<IDsDocument> EnumerateDocuments(string? documentId) {
			if (string.IsNullOrWhiteSpace(documentId))
				return documentService.GetDocuments();
			return new[] { ResolveDocument(documentId) };
		}

		IDsDocument ResolveDocument(string documentId) {
			if (string.IsNullOrWhiteSpace(documentId))
				throw new ArgumentException("Document identifier must not be empty.", nameof(documentId));

			var normalizedDocumentId = documentId.Trim();
			if (TryResolveLoadedDocument(normalizedDocumentId) is { } existingDocument)
				return existingDocument;

			if (LooksLikeExistingFilePath(normalizedDocumentId)) {
				var fullPath = Path.GetFullPath(normalizedDocumentId);
				var loadedDocument = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(fullPath));
				if (loadedDocument is not null)
					return loadedDocument;
			}

			if (TryResolveLoadedDocument(normalizedDocumentId) is { } resolvedDocument)
				return resolvedDocument;

			throw new InvalidOperationException($"Could not find or load a document matching '{documentId}'. If this is a file path, verify that the file exists and is a valid .NET assembly or module.");
		}

		IDsDocument? TryResolveLoadedDocument(string documentId) {
			var comparer = StringComparer.OrdinalIgnoreCase;
			string? fileName = null;
			try {
				fileName = Path.GetFileName(documentId);
			}
			catch {
			}
			var documents = documentService.GetDocuments();
			return documents.FirstOrDefault(a =>
				comparer.Equals(a.Filename, documentId) ||
				comparer.Equals(NormalizePath(a.Filename), NormalizePath(documentId)) ||
				(!string.IsNullOrEmpty(fileName) && comparer.Equals(Path.GetFileName(a.Filename), fileName)) ||
				comparer.Equals(a.GetShortName(), documentId) ||
				comparer.Equals(a.AssemblyDef?.Name, documentId) ||
				comparer.Equals(a.AssemblyDef?.FullName, documentId) ||
				comparer.Equals(a.ModuleDef?.Name, documentId) ||
				comparer.Equals(a.ModuleDef?.FullName, documentId));
		}

		bool LooksLikeExistingFilePath(string documentId) {
			try {
				return File.Exists(Path.GetFullPath(documentId));
			}
			catch {
				return false;
			}
		}

		string NormalizePath(string? path) {
			if (string.IsNullOrWhiteSpace(path))
				return string.Empty;
			try {
				return Path.GetFullPath(path.Trim());
			}
			catch {
				return path.Trim();
			}
		}

		IDecompiler ResolveDecompiler(string? decompilerName) {
			if (string.IsNullOrWhiteSpace(decompilerName))
				return decompilerService.Decompiler;

			var comparer = StringComparer.OrdinalIgnoreCase;
			var match = decompilerService.AllDecompilers.FirstOrDefault(a =>
				comparer.Equals(a.UniqueNameUI, decompilerName) ||
				comparer.Equals(a.GenericNameUI, decompilerName) ||
				comparer.Equals(a.UniqueGuid.ToString(), decompilerName));

			return match ?? throw new InvalidOperationException($"Could not find decompiler '{decompilerName}'. Call list_decompilers to inspect available names.");
		}

		TypeDef ResolveType(IDsDocument document, string typeName) {
			if (string.IsNullOrWhiteSpace(typeName))
				throw new ArgumentException("Type name must not be empty.", nameof(typeName));

			var type = ResolveTypeCore(new[] { document }, typeName);
			if (type is not null)
				return type;

			type = ResolveTypeCore(documentService.GetDocuments().Where(a => !ReferenceEquals(a, document)), typeName);
			return type ?? throw CreateTypeNotFoundException(document, typeName);
		}

		TypeDef? ResolveTypeCore(IEnumerable<IDsDocument> documents, string typeName) {
			var comparer = StringComparer.OrdinalIgnoreCase;
			foreach (var doc in documents) {
				var type = doc.GetModules<ModuleDef>()
					.SelectMany(a => a.GetTypes())
					.FirstOrDefault(a =>
						comparer.Equals(a.FullName, typeName) ||
						comparer.Equals(a.ReflectionFullName, typeName) ||
						comparer.Equals(a.Name, typeName));
				if (type is not null)
					return type;
			}
			return null;
		}

		FieldDef ResolveField(TypeDef type, string fieldName) {
			if (string.IsNullOrWhiteSpace(fieldName))
				throw new ArgumentException("Field name must not be empty.", nameof(fieldName));

			var comparer = StringComparer.OrdinalIgnoreCase;
			var field = type.Fields.FirstOrDefault(a => comparer.Equals(a.Name, fieldName) || comparer.Equals(a.FullName, fieldName));
			return field ?? throw new InvalidOperationException($"Could not find field '{fieldName}' in type '{type.FullName}'.");
		}

		PropertyDef ResolveProperty(TypeDef type, string propertyName) {
			if (string.IsNullOrWhiteSpace(propertyName))
				throw new ArgumentException("Property name must not be empty.", nameof(propertyName));

			var comparer = StringComparer.OrdinalIgnoreCase;
			var property = type.Properties.FirstOrDefault(a => comparer.Equals(a.Name, propertyName) || comparer.Equals(a.FullName, propertyName));
			return property ?? throw new InvalidOperationException($"Could not find property '{propertyName}' in type '{type.FullName}'.");
		}

		bool TypeImplementsInterface(TypeDef candidateType, TypeDef interfaceType) {
			foreach (var typeSig in TypesHierarchyHelpers.GetTypeAndBaseTypes(candidateType)) {
				var resolvedType = typeSig.ToTypeDefOrRef().ResolveTypeDef();
				if (resolvedType is null)
					continue;
				foreach (var interfaceImpl in resolvedType.Interfaces) {
					if (new SigComparer().Equals(interfaceImpl.Interface.GetScopeType(), interfaceType))
						return true;
				}
			}
			return false;
		}

		ITypeDefOrRef? GetImplementedInterface(TypeDef candidateType, TypeDef interfaceType) {
			foreach (var typeSig in TypesHierarchyHelpers.GetTypeAndBaseTypes(candidateType)) {
				var resolvedType = typeSig.ToTypeDefOrRef().ResolveTypeDef();
				if (resolvedType is null)
					continue;
				foreach (var interfaceImpl in resolvedType.Interfaces) {
					var interfaceRef = interfaceImpl.Interface;
					if (new SigComparer().Equals(interfaceRef.GetScopeType(), interfaceType))
						return interfaceRef;
				}
			}
			return null;
		}

		InvalidOperationException CreateTypeNotFoundException(IDsDocument document, string typeName) {
			var normalized = typeName.Trim();
			var candidates = document.GetModules<ModuleDef>()
				.SelectMany(a => a.GetTypes())
				.Where(a => Contains(a.FullName, normalized, false) || Contains(a.ReflectionFullName, normalized, false) || Contains(a.Name, normalized, false))
				.OrderBy(a => GetTypeCandidateScore(a, normalized))
				.ThenBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
				.Select(a => a.FullName)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Take(10)
				.ToArray();

			var message = $"Could not find type '{typeName}' in document '{document.Filename}'.";
			if (candidates.Length > 0)
				message += $" Candidate types: {string.Join(" | ", candidates)}.";
			message += " Call list_types with a filter to inspect available type names.";
			return new InvalidOperationException(message);
		}

		int GetTypeCandidateScore(TypeDef type, string query) {
			if (string.Equals(type.FullName, query, StringComparison.OrdinalIgnoreCase))
				return 0;
			if (string.Equals(type.ReflectionFullName, query, StringComparison.OrdinalIgnoreCase))
				return 1;
			if (string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase))
				return 2;
			if (Contains(type.Name, query, false))
				return 3;
			if (Contains(type.FullName, query, false))
				return 4;
			return 5;
		}

		MethodDef ResolveMethod(IDsDocument document, TypeDef type, string methodName, string? metadataToken, string? methodSignature, string[]? parameterTypes, int? parameterCount) {
			if (string.IsNullOrWhiteSpace(methodName))
				throw new ArgumentException("Method name must not be empty.", nameof(methodName));

			if (!string.IsNullOrWhiteSpace(metadataToken)) {
				var resolvedByToken = ResolveMethodByMetadataToken(document, metadataToken!);
				if (resolvedByToken.DeclaringType != type)
					throw new InvalidOperationException($"Metadata token '{metadataToken}' resolved to '{resolvedByToken.FullName}', which does not belong to type '{type.FullName}'.");
				return resolvedByToken;
			}

			var comparer = StringComparer.OrdinalIgnoreCase;
			var methods = type.Methods.Where(a => comparer.Equals(a.Name, methodName));
			if (!string.IsNullOrWhiteSpace(methodSignature))
				methods = methods.Where(a => comparer.Equals(a.FullName, methodSignature.Trim()));
			if (parameterTypes is not null && parameterTypes.Length > 0) {
				var normalizedParameterTypes = parameterTypes.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();
				methods = methods.Where(a => MethodParameterTypesMatch(a, normalizedParameterTypes));
			}
			if (parameterCount is not null)
				methods = methods.Where(a => GetVisibleParameterCount(a) == parameterCount.Value);

			var matches = methods.ToArray();
			if (matches.Length == 1)
				return matches[0];
			if (matches.Length == 0)
				throw new InvalidOperationException(CreateMethodNotFoundMessage(type, methodName, methodSignature, parameterTypes, parameterCount));
			throw new InvalidOperationException(CreateMethodAmbiguityMessage(type, methodName, matches));
		}

		MethodResolutionResult ResolveMethodForBreakpoint(IDsDocument document, TypeDef type, string methodName, string? metadataToken, string? methodSignature, string[]? parameterTypes, int? parameterCount) {
			try {
				return new MethodResolutionResult(ResolveMethod(document, type, methodName, metadataToken, methodSignature, parameterTypes, parameterCount), null, Array.Empty<MethodInfoResult>());
			}
			catch (InvalidOperationException ex) {
				var candidateMethods = type.Methods
					.Where(a => string.Equals(a.Name, methodName, StringComparison.OrdinalIgnoreCase))
					.OrderBy(a => GetVisibleParameterCount(a))
					.ThenBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
					.Take(10)
					.ToArray();
				var candidates = candidateMethods.Select(ToMethodInfoResult).ToArray();

				if (string.IsNullOrWhiteSpace(metadataToken) && string.IsNullOrWhiteSpace(methodSignature) && (parameterTypes is null || parameterTypes.Length == 0)) {
					var entryPoint = document.GetModules<ModuleDef>()
						.Select(a => a.EntryPoint)
						.FirstOrDefault(a => a is not null && a.DeclaringType == type && string.Equals(a.Name, methodName, StringComparison.OrdinalIgnoreCase));
					if (entryPoint is not null)
						return new MethodResolutionResult(entryPoint, $"Resolved '{methodName}' to the assembly entry point after exact matching failed.", candidates);

					if (candidateMethods.Length == 1)
						return new MethodResolutionResult(candidateMethods[0], $"Resolved '{methodName}' to the only candidate overload after exact matching failed.", candidates);
				}

				var candidateMessage = candidates.Length == 0 ? ex.Message : $"{ex.Message} Candidate methods: {string.Join(" | ", candidates.Select(a => $"{a.FullName} [{a.MetadataToken}]"))}";
				return new MethodResolutionResult(null, candidateMessage, candidates);
			}
		}

		MethodDef ResolveMethodByMetadataToken(IDsDocument document, string metadataToken) {
			if (!TryParseMetadataToken(metadataToken, out var rawToken))
				throw new InvalidOperationException($"Invalid metadata token '{metadataToken}'. Expected a hex token such as 0x06001234.");

			foreach (var module in document.GetModules<ModuleDef>()) {
				if (module.ResolveToken(rawToken) is MethodDef method)
					return method;
			}
			throw new InvalidOperationException($"Could not resolve metadata token '{metadataToken}' in document '{document.Filename}'.");
		}

		bool MethodReferencesTarget(MethodDef targetMethod, IMethod methodRef) =>
			new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.ReferenceCompareForMemberDefsInSameModule).Equals(targetMethod, methodRef);

		bool TypeReferencesTarget(IType? typeRef, TypeDef targetType) => TypeReferencesTarget(typeRef, targetType, 0);

		bool TypeReferencesTarget(IType? typeRef, TypeDef targetType, int level) {
			if (level >= 100 || typeRef is null)
				return false;
			if (new SigComparer().Equals(targetType, typeRef.GetScopeType()))
				return true;

			if (typeRef is TypeSig typeSig) {
				switch (typeSig) {
				case TypeDefOrRefSig defOrRefSig:
					return TypeReferencesTarget(defOrRefSig.TypeDefOrRef, targetType, level + 1);
				case FnPtrSig fnPtrSig:
					if (fnPtrSig.MethodSig is not null) {
						if (TypeReferencesTarget(fnPtrSig.MethodSig.RetType, targetType, level + 1))
							return true;
						foreach (var parameter in fnPtrSig.MethodSig.Params) {
							if (TypeReferencesTarget(parameter, targetType, level + 1))
								return true;
						}
						if (fnPtrSig.MethodSig.ParamsAfterSentinel is not null) {
							foreach (var parameter in fnPtrSig.MethodSig.ParamsAfterSentinel) {
								if (TypeReferencesTarget(parameter, targetType, level + 1))
									return true;
							}
						}
					}
					return false;
				case GenericInstSig genericInstSig:
					if (TypeReferencesTarget(genericInstSig.GenericType, targetType, level + 1))
						return true;
					foreach (var argument in genericInstSig.GenericArguments) {
						if (TypeReferencesTarget(argument, targetType, level + 1))
							return true;
					}
					return false;
				case PtrSig ptrSig:
					return TypeReferencesTarget(ptrSig.Next, targetType, level + 1);
				case ByRefSig byRefSig:
					return TypeReferencesTarget(byRefSig.Next, targetType, level + 1);
				case ArraySigBase arraySig:
					return TypeReferencesTarget(arraySig.Next, targetType, level + 1);
				case ModifierSig modifierSig:
					return TypeReferencesTarget(modifierSig.Modifier, targetType, level + 1) || TypeReferencesTarget(modifierSig.Next, targetType, level + 1);
				case PinnedSig pinnedSig:
					return TypeReferencesTarget(pinnedSig.Next, targetType, level + 1);
				}
			}

			if (typeRef is TypeSpec typeSpec)
				return TypeReferencesTarget(typeSpec.TypeSig, targetType, level + 1);

			return false;
		}

		bool TryFindTypeUsageInMethod(MethodDef method, TypeDef targetType, out string usageKind, out uint? ilOffset, out string? opCode) {
			usageKind = string.Empty;
			ilOffset = null;
			opCode = null;

			if (TypeReferencesTarget(method, targetType)) {
				usageKind = "method-signature";
				return true;
			}

			if (!method.HasBody)
				return false;

			foreach (var instruction in method.Body.Instructions) {
				if (instruction.Operand is ITypeDefOrRef typeRef && TypeReferencesTarget(typeRef, targetType)) {
					usageKind = "method-body";
					ilOffset = instruction.Offset;
					opCode = instruction.OpCode.Name;
					return true;
				}
				if (instruction.Operand is IField fieldRef && !fieldRef.IsMethod && (TypeReferencesTarget(fieldRef.DeclaringType, targetType) || TypeReferencesTarget(fieldRef.FieldSig.GetFieldType(), targetType))) {
					usageKind = "method-body";
					ilOffset = instruction.Offset;
					opCode = instruction.OpCode.Name;
					return true;
				}
				if (instruction.Operand is IMethod methodRef && !methodRef.IsField && TypeReferencesTarget(methodRef, targetType)) {
					usageKind = "method-body";
					ilOffset = instruction.Offset;
					opCode = instruction.OpCode.Name;
					return true;
				}
			}

			foreach (var local in method.Body.Variables) {
				if (TypeReferencesTarget(local.Type, targetType)) {
					usageKind = "local-variable";
					return true;
				}
			}

			foreach (var exceptionHandler in method.Body.ExceptionHandlers) {
				if (TypeReferencesTarget(exceptionHandler.CatchType, targetType)) {
					usageKind = "exception-handler";
					return true;
				}
			}

			return false;
		}

		UsageLocationInfo[] FindFieldAccesses(string documentId, string typeName, string fieldName, bool showWrites, string? searchDocumentId, int maxResults) => LoggedCall(showWrites ? "find_field_writes" : "find_field_reads", $"{documentId}::{typeName}::{fieldName}", () => {
			var document = ResolveDocument(documentId);
			var type = ResolveType(document, typeName);
			var field = ResolveField(type, fieldName);
			var results = new List<UsageLocationInfo>();

			foreach (var searchDocument in EnumerateDocuments(searchDocumentId)) {
				foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
					foreach (var candidateMethod in candidateType.Methods) {
						if (!TryFindFieldAccessInMethod(candidateMethod, field, showWrites, out var usageKind, out var ilOffset, out var opCode))
							continue;
						results.Add(CreateUsageLocationInfo(searchDocument, candidateType, candidateMethod, usageKind, ilOffset, opCode));
						if (results.Count >= Math.Max(1, maxResults))
							return results.ToArray();
					}
				}
			}

			return results.ToArray();
		});

		UsageLocationInfo[] FindPropertyAccesses(string documentId, string typeName, string propertyName, bool isSetter, string? searchDocumentId, int maxResults) => LoggedCall(isSetter ? "find_property_writes" : "find_property_reads", $"{documentId}::{typeName}::{propertyName}", () => {
			var document = ResolveDocument(documentId);
			var type = ResolveType(document, typeName);
			var property = ResolveProperty(type, propertyName);
			var accessor = isSetter ? property.SetMethod : property.GetMethod;
			if (accessor is null)
				return Array.Empty<UsageLocationInfo>();

			var results = new List<UsageLocationInfo>();
			var allSearchDocuments = EnumerateDocuments(searchDocumentId).ToArray();
			var orderedSearchDocuments = string.IsNullOrWhiteSpace(searchDocumentId)
				? allSearchDocuments
					.OrderByDescending(a => string.Equals(a.Filename, document.Filename, StringComparison.OrdinalIgnoreCase))
					.ToArray()
				: allSearchDocuments;
			foreach (var searchDocument in orderedSearchDocuments) {
				foreach (var candidateType in searchDocument.GetModules<ModuleDef>().SelectMany(a => a.GetTypes())) {
					foreach (var candidateMethod in candidateType.Methods) {
						if (!candidateMethod.HasBody)
							continue;
						foreach (var instruction in candidateMethod.Body.Instructions) {
							if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt && instruction.OpCode.Code != Code.Ldftn && instruction.OpCode.Code != Code.Ldvirtftn)
								continue;
							if (instruction.Operand is not IMethod methodRef || methodRef.IsField)
								continue;
							var resolved = methodRef.ResolveMethodDef();
							if (!MethodReferencesTarget(accessor, methodRef, resolved))
								continue;
							results.Add(CreateUsageLocationInfo(searchDocument, candidateType, candidateMethod, isSetter ? "property-write" : "property-read", instruction.Offset, instruction.OpCode.Name));
							if (results.Count >= Math.Max(1, maxResults))
								return results.ToArray();
						}
					}
				}
			}

			return results.ToArray();
		});

		bool TryFindFieldAccessInMethod(MethodDef method, FieldDef targetField, bool showWrites, out string usageKind, out uint? ilOffset, out string? opCode) {
			usageKind = string.Empty;
			ilOffset = null;
			opCode = null;

			if (!method.HasBody)
				return false;

			foreach (var instruction in method.Body.Instructions) {
				if (!IsFieldAccessOpcode(instruction.OpCode.Code, showWrites))
					continue;
				if (instruction.Operand is not IField fieldRef || fieldRef.IsMethod)
					continue;
				var resolved = fieldRef.ResolveFieldDef();
				if (!FieldReferencesTarget(targetField, fieldRef, resolved))
					continue;
				usageKind = showWrites ? "field-write" : "field-read";
				ilOffset = instruction.Offset;
				opCode = instruction.OpCode.Name;
				return true;
			}

			return false;
		}

		bool IsFieldAccessOpcode(Code code, bool showWrites) => showWrites ? code is Code.Stfld or Code.Stsfld : code is Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda or Code.Ldtoken;

		bool FieldReferencesTarget(FieldDef targetField, IField fieldRef, FieldDef? resolvedField = null) {
			if (resolvedField is not null && FieldReferencesTarget(targetField, resolvedField))
				return true;
			return new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.ReferenceCompareForMemberDefsInSameModule).Equals(targetField, fieldRef);
		}

		bool FieldReferencesTarget(FieldDef targetField, FieldDef candidateField) =>
			new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.ReferenceCompareForMemberDefsInSameModule).Equals(targetField, candidateField);

		bool MethodReferencesTarget(MethodDef targetMethod, IMethod methodRef, MethodDef? resolvedMethod = null) {
			if (resolvedMethod is not null && MethodReferencesTarget(targetMethod, resolvedMethod))
				return true;
			return new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.ReferenceCompareForMemberDefsInSameModule).Equals(targetMethod, methodRef);
		}

		bool MethodReferencesTarget(MethodDef targetMethod, MethodDef candidateMethod) =>
			new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable | SigComparerOptions.ReferenceCompareForMemberDefsInSameModule).Equals(targetMethod, candidateMethod);

		bool TypeReferencesTarget(IMethod? methodRef, TypeDef targetType) =>
			methodRef is not null && !methodRef.IsField &&
			(TypeReferencesTarget(methodRef.DeclaringType, targetType) ||
			 TypeReferencesTarget(methodRef.MethodSig.GetRetType(), targetType) ||
			 methodRef.GetParameters().Any(a => !a.IsHiddenThisParameter && TypeReferencesTarget(a.Type, targetType)));

		UsageLocationInfo CreateUsageLocationInfo(IDsDocument sourceDocument, TypeDef sourceType, MethodDef sourceMethod, string usageKind, uint? ilOffset, string? opCode) =>
			new UsageLocationInfo(sourceDocument.Filename, "method", sourceType.FullName, sourceMethod.Name.String, sourceMethod.FullName, $"0x{sourceMethod.MDToken.Raw:X8}", usageKind, ilOffset, opCode);

		UsageLocationInfo CreateTypeUsageLocationInfo(IDsDocument sourceDocument, TypeDef sourceType, string usageKind) =>
			new UsageLocationInfo(sourceDocument.Filename, "type", sourceType.FullName, sourceType.Name.String, sourceType.FullName, $"0x{sourceType.MDToken.Raw:X8}", usageKind, null, null);

		UsageLocationInfo CreateFieldUsageLocationInfo(IDsDocument sourceDocument, TypeDef sourceType, FieldDef sourceField, string usageKind) =>
			new UsageLocationInfo(sourceDocument.Filename, "field", sourceType.FullName, sourceField.Name.String, sourceField.FullName, $"0x{sourceField.MDToken.Raw:X8}", usageKind, null, null);

		DependencyInfo CreateDependencyInfoFromMethodRef(IMethod methodRef, uint ilOffset, string opCode) {
			var resolved = methodRef.ResolveMethodDef();
			var fullName = resolved?.FullName ?? methodRef.FullName;
			var declaringType = resolved?.DeclaringType?.FullName ?? methodRef.DeclaringType?.FullName;
			var sourceDocument = resolved?.Module is null ? null : GetModulePath(resolved.Module);
			var metadataToken = resolved is null ? null : $"0x{resolved.MDToken.Raw:X8}";
			return new DependencyInfo("method", methodRef.Name.String, fullName, declaringType, sourceDocument, metadataToken, ilOffset, opCode);
		}

		DependencyInfo CreateDependencyInfoFromFieldRef(IField fieldRef, uint ilOffset, string opCode) {
			var resolved = fieldRef.ResolveFieldDef();
			var fullName = resolved?.FullName ?? fieldRef.FullName;
			var declaringType = resolved?.DeclaringType?.FullName ?? fieldRef.DeclaringType?.FullName;
			var sourceDocument = resolved?.Module is null ? null : GetModulePath(resolved.Module);
			var metadataToken = resolved is null ? null : $"0x{resolved.MDToken.Raw:X8}";
			return new DependencyInfo("field", fieldRef.Name.String, fullName, declaringType, sourceDocument, metadataToken, ilOffset, opCode);
		}

		DependencyInfo CreateDependencyInfoFromTypeRef(ITypeDefOrRef typeRef, uint ilOffset, string opCode) {
			var resolved = typeRef.ResolveTypeDef();
			var fullName = resolved?.FullName ?? typeRef.FullName;
			var sourceDocument = resolved?.Module is null ? null : GetModulePath(resolved.Module);
			var metadataToken = resolved is null ? null : $"0x{resolved.MDToken.Raw:X8}";
			return new DependencyInfo("type", typeRef.Name.String, fullName, resolved?.Namespace ?? typeRef.Namespace, sourceDocument, metadataToken, ilOffset, opCode);
		}

		string? GetModulePath(ModuleDef module) {
			try {
				return string.IsNullOrWhiteSpace(module.Location) ? null : module.Location;
			}
			catch {
				return null;
			}
		}

		void AddDependency(List<DependencyInfo> results, HashSet<string> seen, DependencyInfo dependency, int maxResults) {
			var key = $"{dependency.Kind}|{dependency.FullName}|{dependency.IlOffset}|{dependency.OpCode}";
			if (!seen.Add(key) || results.Count >= Math.Max(1, maxResults))
				return;
			results.Add(dependency);
		}

		bool MethodParameterTypesMatch(MethodDef method, string[] expectedParameterTypes) {
			var actualParameterTypes = GetVisibleParameterTypes(method);
			if (actualParameterTypes.Length != expectedParameterTypes.Length)
				return false;
			for (int i = 0; i < actualParameterTypes.Length; i++) {
				if (!string.Equals(actualParameterTypes[i], expectedParameterTypes[i], StringComparison.OrdinalIgnoreCase))
					return false;
			}
			return true;
		}

		int GetVisibleParameterCount(MethodDef method) => method.MethodSig?.GetParamCount() ?? 0;

		string[] GetVisibleParameterTypes(MethodDef method) => method.MethodSig?.Params.Select(a => a.FullName).ToArray() ?? Array.Empty<string>();

		string CreateMethodNotFoundMessage(TypeDef type, string methodName, string? methodSignature, string[]? parameterTypes, int? parameterCount) {
			var filters = new List<string> {
				$"name='{methodName}'",
			};
			if (!string.IsNullOrWhiteSpace(methodSignature))
				filters.Add($"signature='{methodSignature.Trim()}'");
			if (parameterTypes is not null && parameterTypes.Length > 0)
				filters.Add($"parameterTypes=[{string.Join(", ", parameterTypes)}]");
			if (parameterCount is not null)
				filters.Add($"parameterCount={parameterCount.Value}");
			return $"Could not find a method in type '{type.FullName}' matching {string.Join(", ", filters)}.";
		}

		string CreateMethodAmbiguityMessage(TypeDef type, string methodName, MethodDef[] matches) {
			var candidateLines = matches
				.Take(10)
				.Select(a => $"{a.FullName} [token=0x{a.MDToken.Raw:X8}]")
				.ToArray();
			var moreText = matches.Length > candidateLines.Length ? $" (+{matches.Length - candidateLines.Length} more)" : string.Empty;
			return $"Multiple overloads of '{methodName}' were found in type '{type.FullName}'. Pass metadataToken, methodSignature, or parameterTypes to disambiguate. Candidates: {string.Join(" | ", candidateLines)}{moreText}";
		}

		ModuleId CreateModuleId(ModuleDef module) => string.IsNullOrWhiteSpace(module.Location) ? ModuleId.CreateInMemory(module) : ModuleId.CreateFromFile(module);

		static readonly ReadOnlyCollection<string> EmptyBreakpointLabels = new ReadOnlyCollection<string>(Array.Empty<string>());

		DbgCodeBreakpointSettings CreateBreakpointSettings(bool isEnabled, string? condition, string? conditionKind, int? hitCount, string? hitCountKind, string? filter, string? traceMessage, bool continueAfterTrace) {
			var settings = new DbgCodeBreakpointSettings {
				IsEnabled = isEnabled,
			};
			if (!string.IsNullOrWhiteSpace(condition))
				settings.Condition = new DbgCodeBreakpointCondition(ParseConditionKind(conditionKind), condition.Trim());
			if (hitCount is not null)
				settings.HitCount = new DbgCodeBreakpointHitCount(ParseHitCountKind(hitCountKind), hitCount.Value);
			if (!string.IsNullOrWhiteSpace(filter))
				settings.Filter = new DbgCodeBreakpointFilter(filter.Trim());
			if (!string.IsNullOrWhiteSpace(traceMessage))
				settings.Trace = new DbgCodeBreakpointTrace(traceMessage, continueAfterTrace);
			return settings;
		}

		DbgCodeBreakpointConditionKind ParseConditionKind(string? conditionKind) {
			if (string.IsNullOrWhiteSpace(conditionKind))
				return DbgCodeBreakpointConditionKind.IsTrue;
			if (Enum.TryParse(conditionKind, true, out DbgCodeBreakpointConditionKind value))
				return value;
			throw new InvalidOperationException("Unsupported conditionKind value. Supported values: IsTrue, WhenChanged.");
		}

		DbgCodeBreakpointHitCountKind ParseHitCountKind(string? hitCountKind) {
			if (string.IsNullOrWhiteSpace(hitCountKind))
				return DbgCodeBreakpointHitCountKind.Equals;
			if (Enum.TryParse(hitCountKind, true, out DbgCodeBreakpointHitCountKind value))
				return value;
			throw new InvalidOperationException("Unsupported hitCountKind value. Supported values: Equals, MultipleOf, GreaterThanOrEquals.");
		}

		DbgCodeBreakpoint ResolveBreakpoint(int breakpointId) {
			var breakpoint = dbgCodeBreakpointsService.Breakpoints.FirstOrDefault(a => a.Id == breakpointId);
			return breakpoint ?? throw new InvalidOperationException($"Could not find breakpoint with id {breakpointId}.");
		}

		ExceptionCategoryInfo ToExceptionCategoryInfo(DbgExceptionCategoryDefinition definition) => new ExceptionCategoryInfo(
			definition.Name,
			definition.DisplayName,
			definition.ShortDisplayName,
			(definition.Flags & DbgExceptionCategoryDefinitionFlags.Code) != 0 ? "code" : "name");

		ExceptionSettingInfo ToExceptionSettingInfo(DbgExceptionSettingsInfo info) {
			if (!dbgExceptionSettingsService.TryGetCategoryDefinition(info.Definition.Id.Category, out var categoryDefinition))
				categoryDefinition = new DbgExceptionCategoryDefinition(DbgExceptionCategoryDefinitionFlags.None, info.Definition.Id.Category, info.Definition.Id.Category, info.Definition.Id.Category);
			return new ExceptionSettingInfo(
				info.Definition.Id.Category,
				categoryDefinition.DisplayName,
				GetExceptionTargetKind(info.Definition.Id),
				FormatExceptionIdentifier(categoryDefinition, info.Definition.Id),
				GetExceptionDisplayName(categoryDefinition, info.Definition),
				info.Definition.Description,
				info.Definition.Id.IsDefaultId,
				HasStopFirstChance(info.Settings.Flags),
				HasStopSecondChance(info.Settings.Flags),
				info.Settings.Conditions.Select(a => new ExceptionConditionInfo(a.ConditionType.ToString(), a.Condition)).ToArray());
		}

		DbgExceptionCategoryDefinition[] GetTargetExceptionCategories(string? category) {
			var normalizedCategory = NormalizeExceptionCategory(category, allowEmpty: true);
			var definitions = dbgExceptionSettingsService.CategoryDefinitions;
			if (normalizedCategory is null)
				return definitions.ToArray();
			return definitions.Where(a => StringComparer.Ordinal.Equals(a.Name, normalizedCategory)).ToArray();
		}

		DbgExceptionCategoryDefinition GetTargetExceptionCategory(string? category) {
			var normalizedCategory = NormalizeExceptionCategory(category, allowEmpty: false) ?? throw new InvalidOperationException("Exception category could not be resolved.");
			if (dbgExceptionSettingsService.TryGetCategoryDefinition(normalizedCategory, out var definition))
				return definition;
			throw new InvalidOperationException($"Could not resolve exception category '{category}'.");
		}

		string? NormalizeExceptionCategory(string? category, bool allowEmpty) {
			if (string.IsNullOrWhiteSpace(category)) {
				if (allowEmpty)
					return null;
				return PredefinedExceptionCategories.DotNet;
			}

			var value = category.Trim();
			foreach (var definition in dbgExceptionSettingsService.CategoryDefinitions) {
				if (StringComparer.OrdinalIgnoreCase.Equals(definition.Name, value) ||
					StringComparer.OrdinalIgnoreCase.Equals(definition.DisplayName, value) ||
					StringComparer.OrdinalIgnoreCase.Equals(definition.ShortDisplayName, value))
					return definition.Name;
			}

			if (StringComparer.OrdinalIgnoreCase.Equals(value, ".NET") ||
				StringComparer.OrdinalIgnoreCase.Equals(value, "dotnet") ||
				StringComparer.OrdinalIgnoreCase.Equals(value, "clr") ||
				StringComparer.OrdinalIgnoreCase.Equals(value, "common language runtime exceptions"))
				return PredefinedExceptionCategories.DotNet;

			if (StringComparer.OrdinalIgnoreCase.Equals(value, "mda") ||
				StringComparer.OrdinalIgnoreCase.Equals(value, "managed debugging assistants"))
				return PredefinedExceptionCategories.MDA;

			throw new InvalidOperationException($"Unsupported exception category '{category}'. Use list_exception_settings to inspect available categories.");
		}

		DbgExceptionId ParseExceptionId(DbgExceptionCategoryDefinition categoryDefinition, string identifier) {
			if (string.IsNullOrWhiteSpace(identifier))
				throw new InvalidOperationException("Exception identifier must not be empty.");

			if ((categoryDefinition.Flags & DbgExceptionCategoryDefinitionFlags.Code) == 0)
				return new DbgExceptionId(categoryDefinition.Name, identifier);

			var trimmed = identifier.Trim();
			var numberStyle = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
			var numberText = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed.Substring(2) : trimmed;
			if ((categoryDefinition.Flags & DbgExceptionCategoryDefinitionFlags.UnsignedCode) != 0) {
				if (!uint.TryParse(numberText, numberStyle, CultureInfo.InvariantCulture, out var unsignedValue))
					throw new InvalidOperationException($"Could not parse exception code '{identifier}'.");
				return new DbgExceptionId(categoryDefinition.Name, unsignedValue);
			}
			if (!int.TryParse(numberText, numberStyle, CultureInfo.InvariantCulture, out var signedValue))
				throw new InvalidOperationException($"Could not parse exception code '{identifier}'.");
			return new DbgExceptionId(categoryDefinition.Name, signedValue);
		}

		DbgExceptionDefinition CreateMissingExceptionDefinition(DbgExceptionId id, DbgExceptionCategoryDefinition categoryDefinition) =>
			new DbgExceptionDefinition(id, DbgExceptionDefinitionFlags.StopSecondChance, GetExceptionDisplayName(categoryDefinition, id));

		string GetDefaultExceptionDescription(DbgExceptionCategoryDefinition categoryDefinition) => $"All {categoryDefinition.DisplayName} exceptions not in this list";

		string GetExceptionDisplayName(DbgExceptionCategoryDefinition categoryDefinition, DbgExceptionDefinition definition) =>
			GetExceptionDisplayName(categoryDefinition, definition.Id);

		string GetExceptionDisplayName(DbgExceptionCategoryDefinition categoryDefinition, DbgExceptionId id) {
			if (id.IsDefaultId)
				return GetDefaultExceptionDescription(categoryDefinition);
			if (id.HasCode)
				return FormatExceptionIdentifier(categoryDefinition, id);
			return id.Name ?? string.Empty;
		}

		string GetExceptionTargetKind(DbgExceptionId id) {
			if (id.IsDefaultId)
				return "default";
			if (id.HasCode)
				return "code";
			return "name";
		}

		string FormatExceptionIdentifier(DbgExceptionCategoryDefinition categoryDefinition, DbgExceptionId id) {
			if (id.IsDefaultId)
				return "<<default>>";
			if (!id.HasCode)
				return id.Name ?? string.Empty;

			if ((categoryDefinition.Flags & DbgExceptionCategoryDefinitionFlags.UnsignedCode) != 0) {
				var unsignedValue = unchecked((uint)id.Code);
				if ((categoryDefinition.Flags & DbgExceptionCategoryDefinitionFlags.DecimalCode) != 0)
					return unsignedValue.ToString(CultureInfo.InvariantCulture);
				return "0x" + unsignedValue.ToString("X8", CultureInfo.InvariantCulture);
			}

			if ((categoryDefinition.Flags & DbgExceptionCategoryDefinitionFlags.DecimalCode) != 0)
				return id.Code.ToString(CultureInfo.InvariantCulture);
			return "0x" + unchecked((uint)id.Code).ToString("X8", CultureInfo.InvariantCulture);
		}

		bool ExceptionSettingMatches(ExceptionSettingInfo info, string query) {
			if (Contains(info.Category, query, false) ||
				Contains(info.CategoryDisplayName, query, false) ||
				Contains(info.Identifier, query, false) ||
				Contains(info.DisplayName, query, false) ||
				Contains(info.Description, query, false))
				return true;
			return info.Conditions.Any(a => Contains(a.Type, query, false) || Contains(a.Value, query, false));
		}

		bool HasStopFirstChance(DbgExceptionDefinitionFlags flags) => (flags & DbgExceptionDefinitionFlags.StopFirstChance) != 0;
		bool HasStopSecondChance(DbgExceptionDefinitionFlags flags) => (flags & DbgExceptionDefinitionFlags.StopSecondChance) != 0;

		DbgExceptionDefinitionFlags SetStopFirstChance(DbgExceptionDefinitionFlags flags, bool enabled) {
			if (enabled)
				return flags | DbgExceptionDefinitionFlags.StopFirstChance;
			return flags & ~DbgExceptionDefinitionFlags.StopFirstChance;
		}

		void WaitForExceptionSettings((DbgExceptionId Id, DbgExceptionSettings Settings)[] expected, int timeoutMilliseconds) {
			if (expected.Length == 0)
				return;
			var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
			while (DateTime.UtcNow <= deadlineUtc) {
				var allMatched = true;
				foreach (var item in expected) {
					if (!dbgExceptionSettingsService.TryGetSettings(item.Id, out var actual) || actual != item.Settings) {
						allMatched = false;
						break;
					}
				}
				if (allMatched)
					return;
				Thread.Sleep(25);
			}
		}

		void SpinWaitForExceptionReset(int timeoutMilliseconds) {
			var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
			var minimumExpected = dbgExceptionSettingsService.CategoryDefinitions.Count;
			while (DateTime.UtcNow <= deadlineUtc) {
				if (dbgExceptionSettingsService.Exceptions.Length >= minimumExpected)
					return;
				Thread.Sleep(25);
			}
		}

		DbgCodeBreakpoint? WaitForBreakpointSettings(int breakpointId, DbgCodeBreakpointSettings expectedSettings, int timeoutMilliseconds) {
			var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
			while (DateTime.UtcNow <= deadlineUtc) {
				var breakpoint = dbgCodeBreakpointsService.Breakpoints.FirstOrDefault(a => a.Id == breakpointId);
				if (breakpoint is null)
					return null;
				if (breakpoint.Settings == expectedSettings)
					return breakpoint;
				Thread.Sleep(25);
			}
			return dbgCodeBreakpointsService.Breakpoints.FirstOrDefault(a => a.Id == breakpointId);
		}

		StepOperationResult StepThread(int? processId, ulong? threadId, ulong? managedThreadId, DbgStepKind stepKind, string actionName) {
			if (!dbgManager.IsDebugging)
				throw new InvalidOperationException("No active debugger session.");
			if (dbgManager.IsRunning == true)
				throw new InvalidOperationException("Cannot step while the debugger is running. Break first or wait until the process is paused.");

			var thread = ResolveDebugThread(processId, threadId, managedThreadId);
			if (thread is null)
				throw new InvalidOperationException("No active debug thread is available.");

			var stepper = thread.CreateStepper();
			if (!stepper.CanStep)
				stepper.Close();
			if (!stepper.CanStep)
				throw new InvalidOperationException("The selected thread cannot be stepped in its current state.");
			stepper.Step(stepKind, autoClose: true);
			return new StepOperationResult(actionName, stepKind.ToString(), ToDebugThreadInfo(thread), dbgManager.IsDebugging, dbgManager.IsRunning);
		}

		string GetDebugProcessDisplayName(DbgProcess process) {
			var target = process.Debugging.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
			if (!string.IsNullOrWhiteSpace(target))
				return target;
			if (!string.IsNullOrWhiteSpace(process.Filename))
				return process.Filename;
			return process.Name;
		}

		string NormalizeDebugEngine(string? engine) {
			if (string.IsNullOrWhiteSpace(engine))
				return ".NET";
			var value = engine.Trim();
			if (string.Equals(value, ".NET", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "dotnet", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "coreclr", StringComparison.OrdinalIgnoreCase))
				return ".NET";
			if (string.Equals(value, ".NET Framework", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "framework", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "netfx", StringComparison.OrdinalIgnoreCase))
				return ".NET Framework";
			if (string.Equals(value, "Mono", StringComparison.OrdinalIgnoreCase))
				return "Mono";
			throw new InvalidOperationException("Unsupported debug engine. Supported values: .NET, .NET Framework, Mono.");
		}

		string? NormalizeDebuggeePath(string engine, string fullPath, bool useHostExecutable, out string? error) {
			error = null;
			if (!string.Equals(engine, ".NET", StringComparison.Ordinal))
				return fullPath;
			if (!useHostExecutable)
				return fullPath;
			if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
				return fullPath;

			var siblingDll = Path.ChangeExtension(fullPath, ".dll");
			if (File.Exists(siblingDll))
				return siblingDll;

			error = $"Invalid launch configuration: engine '.NET' with useHostExecutable=true expects a .dll target for 'dotnet exec'. No sibling DLL was found for '{fullPath}'.";
			return null;
		}

		DebugProgramOptions CreateStartDebuggingOptions(string engine, string filename, string? arguments, string? workingDirectory, string? breakKind, bool inheritEnvironment, DebugEnvironmentEntry[]? environmentVariables, string[]? removeEnvironmentVariables, bool useHostExecutable, string? host, string? hostArguments, double? timeoutSeconds, string? debuggeeVersion, string? monoExePath, ushort? monoConnectionPort) {
			var normalizedBreakKind = NormalizeBreakKind(breakKind);
			switch (engine) {
			case ".NET": {
				var options = new DotNetStartDebuggingOptions {
					Filename = filename,
					CommandLine = arguments,
					WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(filename) : workingDirectory,
					BreakKind = normalizedBreakKind,
					UseHost = useHostExecutable,
					Host = string.IsNullOrWhiteSpace(host) ? null : host,
					HostArguments = string.IsNullOrWhiteSpace(hostArguments) && useHostExecutable ? "exec" : hostArguments,
					ConnectionTimeout = timeoutSeconds is null ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds.Value)),
				};
				ApplyEnvironmentChanges(options.Environment, inheritEnvironment, environmentVariables, removeEnvironmentVariables);
				return options;
			}
			case ".NET Framework": {
				var options = new DotNetFrameworkStartDebuggingOptions {
					Filename = filename,
					CommandLine = arguments,
					WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(filename) : workingDirectory,
					BreakKind = normalizedBreakKind,
					DebuggeeVersion = string.IsNullOrWhiteSpace(debuggeeVersion) ? null : debuggeeVersion,
				};
				ApplyEnvironmentChanges(options.Environment, inheritEnvironment, environmentVariables, removeEnvironmentVariables);
				return options;
			}
			case "Mono": {
				var options = new MonoStartDebuggingOptions {
					Filename = filename,
					CommandLine = arguments,
					WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(filename) : workingDirectory,
					BreakKind = normalizedBreakKind,
					MonoExePath = string.IsNullOrWhiteSpace(monoExePath) ? null : monoExePath,
					ConnectionPort = monoConnectionPort ?? 0,
					ConnectionTimeout = timeoutSeconds is null ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds.Value)),
				};
				ApplyEnvironmentChanges(options.Environment, inheritEnvironment, environmentVariables, removeEnvironmentVariables);
				return options;
			}
			default:
				throw new InvalidOperationException($"Unsupported debug engine '{engine}'.");
			}
		}

		string NormalizeBreakKind(string? breakKind) {
			if (string.IsNullOrWhiteSpace(breakKind))
				return PredefinedBreakKinds.DontBreak;
			if (string.Equals(breakKind, PredefinedBreakKinds.DontBreak, StringComparison.OrdinalIgnoreCase))
				return PredefinedBreakKinds.DontBreak;
			if (string.Equals(breakKind, PredefinedBreakKinds.CreateProcess, StringComparison.OrdinalIgnoreCase))
				return PredefinedBreakKinds.CreateProcess;
			if (string.Equals(breakKind, PredefinedBreakKinds.ModuleCctorOrEntryPoint, StringComparison.OrdinalIgnoreCase))
				return PredefinedBreakKinds.ModuleCctorOrEntryPoint;
			if (string.Equals(breakKind, PredefinedBreakKinds.EntryPoint, StringComparison.OrdinalIgnoreCase))
				return PredefinedBreakKinds.EntryPoint;
			throw new InvalidOperationException("Unsupported breakKind. Supported values: DontBreak, CreateProcess, ModuleCctorOrEntryPoint, EntryPoint.");
		}

		void ApplyEnvironmentChanges(DbgEnvironment environment, bool inheritEnvironment, DebugEnvironmentEntry[]? environmentVariables, string[]? removeEnvironmentVariables) {
			if (!inheritEnvironment)
				environment.Clear();
			if (removeEnvironmentVariables is not null) {
				foreach (var variableName in removeEnvironmentVariables.Where(a => !string.IsNullOrWhiteSpace(a)))
					environment.Remove(variableName.Trim());
			}
			if (environmentVariables is not null) {
				foreach (var variable in environmentVariables.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
					environment.Add(variable.Name.Trim(), variable.Value ?? string.Empty);
			}
		}

		DecompilationContext CreateDecompilationContext() => new DecompilationContext {
			GetDisableAssemblyLoad = documentService.DisableAssemblyLoad,
			AsyncMethodBodyDecompilation = false,
		};

		string? GetTargetFramework(AssemblyDef? assembly, ModuleDef? module) {
			var owner = (IHasCustomAttribute?)assembly ?? module;
			var attribute = owner?.CustomAttributes.FirstOrDefault(a => a.TypeFullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
			if (attribute?.ConstructorArguments.Count > 0)
				return attribute.ConstructorArguments[0].Value as string;
			return null;
		}

		ModuleDef? GetPrimaryModule(IDsDocument document) => document.GetModules<ModuleDef>().FirstOrDefault();

		(ModuleDef Module, Resource Resource) ResolveResource(IDsDocument document, string resourceName, string? moduleId) {
			if (string.IsNullOrWhiteSpace(resourceName))
				throw new ArgumentException("Resource name must not be empty.", nameof(resourceName));

			var normalizedResourceName = resourceName.Trim();
			var normalizedModuleId = string.IsNullOrWhiteSpace(moduleId) ? null : moduleId.Trim();
			var matches = document.GetModules<ModuleDef>()
				.SelectMany(module => module.Resources.Select(resource => (Module: module, Resource: resource)))
				.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Resource.Name, normalizedResourceName) &&
					(normalizedModuleId is null || StringComparer.OrdinalIgnoreCase.Equals(item.Module.FullName, normalizedModuleId) || StringComparer.OrdinalIgnoreCase.Equals(item.Module.Name, normalizedModuleId) || StringComparer.OrdinalIgnoreCase.Equals(item.Module.Location, normalizedModuleId)))
				.ToArray();

			if (matches.Length == 1)
				return matches[0];

			if (matches.Length == 0) {
				var candidates = document.GetModules<ModuleDef>()
					.SelectMany(module => module.Resources.Select(resource => $"{module.FullName}::{resource.Name}"))
					.Where(name => Contains(name, normalizedResourceName, false))
					.Take(10)
					.ToArray();
				var message = $"Could not find resource '{resourceName}' in document '{document.Filename}'.";
				if (candidates.Length > 0)
					message += $" Candidate resources: {string.Join(" | ", candidates)}.";
				throw new InvalidOperationException(message);
			}

			throw new InvalidOperationException($"Resource '{resourceName}' matched multiple modules in document '{document.Filename}'. Pass moduleId to disambiguate.");
		}

		AssemblyReferenceInfo ToAssemblyReferenceInfo(AssemblyRef asmRef) {
			var publicKeyToken = asmRef.PublicKeyOrToken?.ToString();
			return new AssemblyReferenceInfo(asmRef.FullName, asmRef.Name, asmRef.Version.ToString(), asmRef.Culture, publicKeyToken);
		}

		AttachableProcessInfo ToAttachableProcessInfo(AttachableProcess attachableProcess) => new AttachableProcessInfo(
			attachableProcess.ProcessId,
			attachableProcess.RuntimeName,
			attachableProcess.RuntimeKindGuid,
			attachableProcess.Name,
			attachableProcess.Title,
			attachableProcess.Filename,
			attachableProcess.Architecture.ToString(),
			attachableProcess.OperatingSystem.ToString());

		DbgEvaluationInfo? TryCreateEvaluationInfo(string? languageName, out string? errorMessage, out DbgLanguage? language, out DbgStackFrame? frame) {
			language = null;
			frame = dbgCallStackService.ActiveFrame;
			var thread = dbgManager.CurrentThread.Current;
			if (thread is null || frame is null) {
				errorMessage = "No active paused debugger frame is available.";
				return null;
			}

			if (!string.IsNullOrWhiteSpace(languageName)) {
				var normalizedLanguageName = languageName.Trim();
				language = dbgLanguageService.GetLanguages(thread.Runtime.RuntimeKindGuid).FirstOrDefault(a => string.Equals(a.Name, normalizedLanguageName, StringComparison.OrdinalIgnoreCase) || string.Equals(a.DisplayName, normalizedLanguageName, StringComparison.OrdinalIgnoreCase));
				if (language is null) {
					errorMessage = $"Could not find language '{normalizedLanguageName}' for runtime kind {thread.Runtime.RuntimeKindGuid}.";
					return null;
				}
			}
			else {
				language = dbgLanguageService.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
			}

			errorMessage = null;
			var context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
			return new DbgEvaluationInfo(context, frame, CancellationToken.None);
		}

		ResourceInfoResult ToResourceInfo(ModuleDef module, Resource resource) {
			var size = resource is EmbeddedResource er ? er.CreateReader().ToArray().Length : (int?)null;
			var assemblyName = (resource as AssemblyLinkedResource)?.Assembly?.FullName;
			var linkedFile = (resource as LinkedResource)?.File?.FullName;
			return new ResourceInfoResult(
				module.FullName,
				module.Location,
				resource.Name,
				resource.ResourceType.ToString(),
				resource.Attributes.ToString(),
				size,
				assemblyName,
				linkedFile,
				resource.ResourceType == ResourceType.Embedded,
				resource.ResourceType == ResourceType.AssemblyLinked,
				resource.ResourceType == ResourceType.Linked);
		}

		string GetArchString(ModuleDef? module) {
			if (module is null)
				return "???";

			if (module.Machine.IsI386()) {
				var c = (module.Is32BitRequired ? 2 : 0) + (module.Is32BitPreferred ? 1 : 0);
				switch (c) {
				case 0:
					if (!module.IsILOnly)
						return "x86";
					return "AnyCPU (64-bit preferred)";
				case 1:
					return "???";
				case 2:
					return "x86";
				case 3:
					return "AnyCPU (32-bit preferred)";
				}
			}

			return GetArchString(module.Machine);
		}

		static string GetArchString(Machine machine) {
			if (machine.IsI386())
				return "x86";
			if (machine.IsAMD64())
				return "x64";
			if (machine == Machine.IA64)
				return "IA-64";
			if (machine.IsARMNT())
				return "ARM";
			if (machine.IsARM64())
				return "ARM64";
			return machine.ToString();
		}

		static string DecodeSectionName(byte[] name) {
			var text = Encoding.ASCII.GetString(name);
			return text.TrimEnd('\0');
		}

		string? NormalizeQuery(string? query) => string.IsNullOrWhiteSpace(query) ? null : query.Trim();

		bool Contains(string? value, string query, bool caseSensitive) =>
			value?.IndexOf(query, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;

		Regex CreateRegex(string pattern, bool caseSensitive) {
			var options = RegexOptions.Compiled;
			if (!caseSensitive)
				options |= RegexOptions.IgnoreCase;
			return new Regex(pattern, options);
		}

		bool IsMatch(string? value, string query, bool caseSensitive, Regex? regex) {
			if (string.IsNullOrEmpty(value))
				return false;
			return regex?.IsMatch(value) ?? Contains(value, query, caseSensitive);
		}

		bool IsSymbolMatch(string? fullName, string? shortName, string query, bool caseSensitive, Regex? regex) =>
			IsMatch(fullName, query, caseSensitive, regex) || IsMatch(shortName, query, caseSensitive, regex);

		HashSet<string> NormalizeSymbolKinds(string[]? symbolKinds) {
			var allKinds = new[] { "type", "method", "field", "property", "event" };
			if (symbolKinds is null || symbolKinds.Length == 0)
				return new HashSet<string>(allKinds, StringComparer.OrdinalIgnoreCase);
			var result = new HashSet<string>(symbolKinds.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
			if (result.Count == 0)
				return new HashSet<string>(allKinds, StringComparer.OrdinalIgnoreCase);
			if (result.Except(allKinds, StringComparer.OrdinalIgnoreCase).Any())
				throw new InvalidOperationException("Unsupported symbolKinds value. Supported values: type, method, field, property, event.");
			return result;
		}

		void AddSearchResult(List<SearchSymbolResult> results, int maxResults, SearchSymbolResult result) {
			if (results.Count < Math.Max(1, maxResults))
				results.Add(result);
		}

		IEnumerable<SettingsSectionInfo> EnumerateSectionInfos(ISettingsSection[] sections, string parentPath, int depth) {
			var counters = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var section in sections) {
				counters.TryGetValue(section.Name, out var index);
				counters[section.Name] = index + 1;
				var segment = FormatSectionSegment(section.Name, index);
				var path = string.IsNullOrEmpty(parentPath) ? segment : parentPath + "/" + segment;
				yield return new SettingsSectionInfo(path, section.Name, depth, section.Attributes.Length, section.Sections.Length);
				foreach (var child in EnumerateSectionInfos(section.Sections, path, depth + 1))
					yield return child;
			}
		}

		string FormatSectionSegment(string name, int index) => $"{name}[{index}]";

		(string name, int index) ParseSectionSegment(string segment) {
			var openBracket = segment.LastIndexOf('[');
			var closeBracket = segment.LastIndexOf(']');
			if (openBracket < 0 || closeBracket != segment.Length - 1 || openBracket >= closeBracket)
				return (segment, 0);
			var name = segment.Substring(0, openBracket);
			var indexText = segment.Substring(openBracket + 1, closeBracket - openBracket - 1);
			if (!int.TryParse(indexText, out var index) || index < 0)
				index = 0;
			return (name, index);
		}

		ISettingsSection ResolveSectionPath(string path) {
			var currentSections = settingsService.Sections;
			ISettingsSection? current = null;
			foreach (var segment in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
				var (name, index) = ParseSectionSegment(segment);
				var matches = currentSections.Where(a => a.Name == name).ToArray();
				if (index >= matches.Length)
					throw new InvalidOperationException($"Could not resolve settings section path '{path}'.");
				current = matches[index];
				currentSections = current.Sections;
			}
			return current ?? throw new InvalidOperationException($"Could not resolve settings section path '{path}'.");
		}

		SettingsSectionData ToSettingsSectionData(ISettingsSection section, string path, int depth, int maxDepth) => new SettingsSectionData(
			path,
			section.Name,
			section.Attributes.Select(a => new SettingAttributeData(a.key, a.value)).ToArray(),
			depth >= maxDepth ? Array.Empty<SettingsSectionData>() : section.Sections.Select((a, index) => new { Section = a, Index = index })
				.GroupBy(a => a.Section.Name, StringComparer.Ordinal)
				.SelectMany(group => group.Select((item, groupIndex) => ToSettingsSectionData(item.Section, path + "/" + FormatSectionSegment(item.Section.Name, groupIndex), depth + 1, maxDepth)))
				.ToArray());

		IEnumerable<SettingsSearchResult> EnumerateSettingsValues(ISettingsSection[] sections, string parentPath) {
			var counters = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var section in sections) {
				counters.TryGetValue(section.Name, out var index);
				counters[section.Name] = index + 1;
				var path = string.IsNullOrEmpty(parentPath) ? FormatSectionSegment(section.Name, index) : parentPath + "/" + FormatSectionSegment(section.Name, index);
				foreach (var attribute in section.Attributes)
					yield return new SettingsSearchResult(path, attribute.key, attribute.value);
				foreach (var child in EnumerateSettingsValues(section.Sections, path))
					yield return child;
			}
		}
	}

	public sealed record LoadedDocumentInfo(string ShortName, string Filename, string? AssemblyFullName, string? ModuleFullName, bool IsAutoLoaded, int ChildCount, string? ErrorMessage = null);
	public sealed record SaveAssemblyResult(bool Saved, string Message, string? OutputPath, string? ErrorMessage = null);
	public sealed record AssemblyEditResult(bool Success, string Message, string? Name, string? Culture, string? Version, string? HashAlgorithm, string? ProcessorArch, string? ContentType, string? ErrorMessage = null);
	public sealed record AssemblySettingsInfoResult(string SourceDocument, string? Name, string? Culture, string? Version, string? HashAlgorithm, string PublicKeyHex, string ProcessorArch, string ContentType, bool FlagPublicKey, bool FlagProcessorArchSpecified, bool FlagRetargetable, bool FlagEnableJitCompileTracking, bool FlagDisableJitCompileOptimizer, int CustomAttributeCount, int SecurityDeclarationCount, string? ErrorMessage = null);
	public sealed record ModuleInfoResult(
		string SourceDocument,
		string Name,
		string ModuleKind,
		string ClrVersion,
		Guid? Mvid,
		Guid? EncId,
		Guid? EncBaseId,
		string EntryPointKind,
		string? ManagedEntryPoint,
		string? ManagedEntryPointMetadataToken,
		string? NativeEntryPointRva,
		string? RuntimeVersion,
		string TablesHeaderVersion,
		string Cor20HeaderRuntimeVersion,
		string Machine,
		bool RelocsStripped,
		bool ExecutableImage,
		bool LineNumsStripped,
		bool LocalSymsStripped,
		bool AggressiveWsTrim,
		bool LargeAddressAware,
		bool CharacteristicsReserved1,
		bool BytesReversedLo,
		bool Bit32Machine,
		bool DebugStripped,
		bool RemovableRunFromSwap,
		bool NetRunFromSwap,
		bool System,
		bool Dll,
		bool UpSystemOnly,
		bool BytesReversedHi,
		bool DllReserved1,
		bool DllReserved2,
		bool DllReserved3,
		bool DllReserved4,
		bool DllReserved5,
		bool HighEntropyVA,
		bool DynamicBase,
		bool ForceIntegrity,
		bool NxCompat,
		bool NoIsolation,
		bool NoSeh,
		bool NoBind,
		bool AppContainer,
		bool WdmDriver,
		bool GuardCf,
		bool TerminalServerAware,
		bool ILOnly,
		bool Bit32Required,
		bool ILLibrary,
		bool Bit32Preferred,
		bool TrackDebugData,
		bool StrongNameSigned,
		int CustomAttributeCount,
		string? ErrorMessage = null);
	public sealed record ModuleEditResult(bool Success, string Message, string? Name, string? ModuleKind, string? RuntimeVersion, string? TablesHeaderVersion, string? Cor20HeaderRuntimeVersion, string? Machine, string? EntryPointKind, string? ManagedEntryPoint, string? NativeEntryPointRva, string? ErrorMessage = null);
	public sealed record AssemblyCustomAttributeInfo(int Index, string ConstructorFullName, string? ConstructorMetadataToken, string[] ConstructorArguments, string DisplayText, string? ErrorMessage = null);
	public sealed record ModuleCustomAttributeInfo(int Index, string ConstructorFullName, string? ConstructorMetadataToken, string[] ConstructorArguments, string DisplayText, string? ErrorMessage = null);
	public sealed record CompilerLikeDiagnostic(string Severity, string Code, string Message, string? Filename = null, int? Line = null, int? Column = null, string? ErrorMessage = null);
	public sealed record AssemblyCustomAttributeEditResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, AssemblyCustomAttributeInfo? Attribute, string? ErrorMessage = null);
	public sealed record ModuleCustomAttributeEditResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, ModuleCustomAttributeInfo? Attribute, string? ErrorMessage = null);
	public sealed record AddTypeFromCSharpResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, TypeInfo[] AddedTypes, string? ErrorMessage = null);
	public sealed record DeleteTypeResult(bool Success, string Message, string? DeletedType, string? MetadataToken, string? ErrorMessage = null);
	public sealed record MethodIlOperandSpec(string Kind, string? Text = null, int? Index = null, int? Offset = null, int? Int32 = null, long? Int64 = null, float? Float32 = null, double? Float64 = null, string? MetadataToken = null, string? TypeName = null, string? DeclaringTypeName = null, string? MemberName = null, string? MethodSignature = null, string[]? ParameterTypes = null, int? ParameterCount = null, int? TargetInstructionIndex = null, int? TargetInstructionOffset = null, int[]? TargetInstructionIndices = null, int[]? TargetInstructionOffsets = null, string? Label = null, bool? BodyEnd = null, string? ReturnTypeName = null, string? CallingConventionName = null, bool? HasThis = null, bool? ExplicitThis = null, int? GenericParameterCount = null, string[]? ParameterTypesAfterSentinel = null, string? ErrorMessage = null);
	public sealed record MethodIlInstructionDefinition(string OpCode, MethodIlOperandSpec? Operand = null, string? Label = null, string? ErrorMessage = null);
	public sealed record MethodIlInstructionPatchOperation(string Action, int? Index = null, int? Offset = null, MethodIlInstructionDefinition[]? Instructions = null, string? ErrorMessage = null);
	public sealed record MethodIlLocalDefinition(string? TypeName = null, string? Name = null, string? Attributes = null, string? ErrorMessage = null);
	public sealed record MethodIlLocalPatchOperation(string Action, int? Index = null, MethodIlLocalDefinition? Local = null, string? ErrorMessage = null);
	public sealed record MethodIlExceptionHandlerDefinition(string? HandlerType = null, int? TryStartIndex = null, int? TryStartOffset = null, bool? TryStartBodyEnd = null, int? TryEndIndex = null, int? TryEndOffset = null, bool? TryEndBodyEnd = null, int? HandlerStartIndex = null, int? HandlerStartOffset = null, bool? HandlerStartBodyEnd = null, int? HandlerEndIndex = null, int? HandlerEndOffset = null, bool? HandlerEndBodyEnd = null, int? FilterStartIndex = null, int? FilterStartOffset = null, bool? FilterStartBodyEnd = null, string? CatchTypeName = null, string? ErrorMessage = null);
	public sealed record MethodIlExceptionHandlerPatchOperation(string Action, int? Index = null, MethodIlExceptionHandlerDefinition? ExceptionHandler = null, string? ErrorMessage = null);
	public sealed record MethodIlOperandInfo(string Kind, string? DisplayText, int? TargetInstructionIndex, int? TargetInstructionOffset, int[]? TargetInstructionIndices, int[]? TargetInstructionOffsets, int? VariableIndex, string? VariableKind, int? Int32Value, long? Int64Value, double? Float64Value, string? StringValue, string? DeclaringTypeName, string? MemberName, string? MetadataToken, string? ErrorMessage = null);
	public sealed record MethodIlInstructionInfo(int Index, int Offset, string Label, string OpCode, string Code, string OperandType, MethodIlOperandInfo Operand, string? SequencePoint, string? ErrorMessage = null);
	public sealed record MethodIlLocalInfo(int Index, string TypeName, string? Name, string Attributes, string? ErrorMessage = null);
	public sealed record MethodIlExceptionHandlerInfo(int Index, string HandlerType, int? TryStartIndex, int? TryStartOffset, int? TryEndIndex, int? TryEndOffset, bool TryEndIsBodyEnd, int? HandlerStartIndex, int? HandlerStartOffset, int? HandlerEndIndex, int? HandlerEndOffset, bool HandlerEndIsBodyEnd, int? FilterStartIndex, int? FilterStartOffset, string? CatchTypeName, string? ErrorMessage = null);
	public sealed record MethodIlBodyResult(string SourceDocument, string MethodFullName, bool HasBody, bool? KeepOldMaxStack, bool? InitLocals, ushort? MaxStack, uint? LocalVarSigTok, int? InstructionCount, MethodIlInstructionInfo[] Instructions, MethodIlLocalInfo[] Locals, MethodIlExceptionHandlerInfo[] ExceptionHandlers, string? ErrorMessage = null);
	public sealed record MethodIlPatchResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, MethodIlBodyResult? MethodBody, string? ErrorMessage = null);
	public sealed record AssemblySecurityDeclarationEdit(string Action, string? Net1xXml, string? ErrorMessage = null);
	public sealed record AssemblySecurityEditResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, int SecurityDeclarationCount, string? ErrorMessage = null);
	public sealed record AssemblyCSharpEditResult(bool Success, string Message, CompilerLikeDiagnostic[] Diagnostics, string? ErrorMessage = null);
	public sealed record AttachableProcessInfo(int ProcessId, string RuntimeName, Guid RuntimeKindGuid, string Name, string Title, string Filename, string Architecture, string OperatingSystem, string? ErrorMessage = null);
	public sealed record AttachProcessResult(bool Attached, string Message, AttachableProcessInfo[]? Candidates, string? ErrorMessage = null);
	public sealed record DecompilerInfo(string UniqueName, string GenericName, string FileExtension, string Guid, bool IsDefault, string? ErrorMessage = null);
	public sealed record TypeInfo(string FullName, string Name, string Namespace, bool IsPublic, bool IsNested, string SourceDocument, string? ErrorMessage = null);
	public sealed record MethodInfoResult(string Name, string FullName, string ReturnType, int ParameterCount, string[] ParameterTypes, bool IsStatic, bool IsConstructor, bool IsVirtual, bool IsSpecialName, string MetadataToken, string? ErrorMessage = null);
	public sealed record EntryPointInfoResult(bool HasEntryPoint, string SourceDocument, string? DeclaringType, MethodInfoResult? Method, string? Message = null, string? ErrorMessage = null);
	public sealed record SearchSymbolResult(string Kind, string Name, string FullName, string? DeclaringType, string DisplayName, string SourceDocument, string MetadataToken, string? ErrorMessage = null);
	public sealed record MetadataSummaryResult(string Filename, string? AssemblyFullName, string? ModuleFullName, string? ModuleKind, string? TargetFramework, string? EntryPoint, int TypeCount, int PublicTypeCount, int MethodCount, int FieldCount, int PropertyCount, int EventCount, int ResourceCount, int ModuleCount, string[] ReferencedAssemblies, string? ErrorMessage = null);
	public sealed record EvaluateExpressionResult(bool Succeeded, string Expression, string? Language, int? ProcessId, ulong? ThreadId, string? Type, string? Value, string? Error, string? Message, string? ErrorMessage = null);
	public sealed record AssemblyInfoResult(string Filename, string? AssemblyFullName, string AssemblyName, string? Version, string? Culture, string? PublicKeyToken, string? EntryPoint, string? TargetFramework, int ModuleCount, int ResourceCount, string[] ReferencedAssemblies, string[] AssemblyAttributes, string? ErrorMessage = null);
	public sealed record AssemblyReferenceInfo(string FullName, string Name, string? Version, string? Culture, string? PublicKeyToken, string? ErrorMessage = null);
	public sealed record ResourceInfoResult(string ModuleFullName, string ModuleFilename, string Name, string ResourceType, string Attributes, int? Size, string? AssemblyFullName, string? FileName, bool IsEmbedded, bool IsAssemblyLinked, bool IsLinked, string? ErrorMessage = null);
	public sealed record ResourceExportResult(string Filename, string ModuleFullName, string ResourceName, string ResourceType, string OutputPath, bool Exported, int? ByteCount, string? Message, string? ErrorMessage = null);
	public sealed record PeSectionInfo(string Name, uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer, string Characteristics, string? ErrorMessage = null);
	public sealed record PeInfoResult(string Filename, string Machine, string Architecture, string Characteristics, bool? IsILOnly, bool? Is32BitRequired, bool? Is32BitPreferred, string? Cor20HeaderFlags, ulong ImageBase, uint FileAlignment, uint SectionAlignment, uint SizeOfImage, uint SizeOfHeaders, string Subsystem, string Magic, uint TimeDateStamp, DateTime? TimestampUtc, PeSectionInfo[] Sections, string? ErrorMessage = null);
	public sealed record DecompiledTextResult(string Decompiler, string Target, string Text, string? ErrorMessage = null, bool Truncated = false);
	public sealed record DebugSessionStatusResult(bool IsDebugging, bool? IsRunning, int ProcessCount, DebugProcessInfo? CurrentProcess, DebugThreadInfo? CurrentThread, CallStackFrameInfo? ActiveFrame, int ActiveFrameIndex, int VisibleFrameCount, bool FramesTruncated, string? ErrorMessage = null);
	public sealed record DebugProcessInfo(int Id, string Name, string Filename, string DisplayName, string State, bool IsRunning, int Bitness, string Architecture, string OperatingSystem, string[] DebuggingTargets, string DebuggingSummary, string[] RuntimeNames, string[] AppDomainNames, int ThreadCount, int RuntimeCount, string? ErrorMessage = null);
	public sealed record DebugModuleInfo(int ProcessId, string ProcessName, string RuntimeName, string? AppDomainName, int Order, string Name, string Filename, bool IsExe, bool IsDynamic, bool IsInMemory, bool? IsOptimized, ulong Address, uint Size, string Version, DateTime? TimestampUtc, string? ErrorMessage = null);
	public sealed record DebugThreadInfo(int ProcessId, ulong Id, ulong? ManagedId, string Name, string UIName, string Kind, bool IsMain, int SuspendedCount, string[] State, int? AppDomainId, string? AppDomainName, string? ErrorMessage = null);
	public sealed record DebugContextSelectionResult(bool Success, string Message, DebugThreadInfo? CurrentThread, CallStackFrameInfo? ActiveFrame, int? ActiveFrameIndex, bool? FramesTruncated, string? ErrorMessage = null);
	public sealed record CallStackResult(DebugThreadInfo Thread, int ActiveFrameIndex, bool FramesTruncated, CallStackFrameInfo[] Frames, string? ErrorMessage = null);
	public sealed record CallStackFrameInfo(int ProcessId, ulong ThreadId, string? ModuleName, string? ModuleFilename, string? FunctionToken, uint FunctionOffset, string Flags, string? LocationType, string? LocationDisplay, string? ErrorMessage = null);
	public sealed record DebugControlResult(string Action, string Status, bool IsDebugging, bool? IsRunning, string? ErrorMessage = null);
	public sealed record DebugEnvironmentEntry(string Name, string Value, string? ErrorMessage = null);
	public sealed record StartDebuggingResult(bool Started, string? ErrorMessage, string? Engine, string? Filename, DebugProcessInfo? Process, string? EffectiveFilename, string? NormalizationMessage);
	public sealed record LogMessageInfo(DateTimeOffset TimestampUtc, string Level, string Message, string? ErrorMessage = null);
	public sealed record ClearDebugEventsResult(int ClearedCount, string? ErrorMessage = null);
	public sealed record MethodResolutionPreviewResult(bool Resolved, MethodInfoResult? Method, string? Message, MethodInfoResult[] CandidateMethods, string? ErrorMessage = null);
	public sealed record TypeRelationInfo(string SourceDocument, string SourceType, string RelatedType, string RelationKind, int Distance, string? MetadataToken, string? ErrorMessage = null);
	public sealed record DebugEventInfo(long Sequence, DateTimeOffset TimestampUtc, string Kind, string Severity, string Message, bool IsOutputLine, int? ProcessId, string? ProcessName, string? ProcessFilename, string? RuntimeName, ulong? ThreadId, string? ModuleName, string? ModuleFilename, string? AppDomainName, string? ErrorMessage = null);
	public sealed record DebugOutputLine(long Sequence, DateTimeOffset TimestampUtc, string Message, int? ProcessId, string? ProcessName, string? ProcessFilename, string? ErrorMessage = null);
	public sealed record DebugEventWaitResult(bool TimedOut, DebugEventInfo? Event, long LatestSequence, string? ErrorMessage = null);
	public sealed record StepOperationResult(string Action, string StepKind, DebugThreadInfo Thread, bool IsDebugging, bool? IsRunning, string? ErrorMessage = null);
	public sealed record BreakpointInfo(int Id, bool IsEnabled, bool IsTemporary, bool IsHidden, bool IsOneShot, string LocationType, string? LocationDisplay, int BoundBreakpointCount, int? CurrentHitCount, string? Condition, string? HitCount, string? Filter, string? TraceMessage, bool? TraceContinues, string BoundMessageSeverity, string BoundMessage, string[] Labels, string? ErrorMessage = null);
	public sealed record BreakpointSetResult(bool Created, BreakpointInfo? Breakpoint, string? Message, MethodInfoResult[]? CandidateMethods = null, string? ErrorMessage = null);
	public sealed record BreakpointUpdateResult(bool Updated, BreakpointInfo? Breakpoint, string? Message, string? ErrorMessage = null);
	public sealed record BreakpointOperationResult(bool Success, string Message, string? ErrorMessage = null);
	public sealed record UsageLocationInfo(string SourceDocument, string SourceMemberKind, string SourceType, string SourceName, string SourceFullName, string MetadataToken, string UsageKind, uint? IlOffset, string? OpCode, string? ErrorMessage = null);
	public sealed record DependencyInfo(string Kind, string Name, string FullName, string? DeclaringType, string? SourceDocument, string? MetadataToken, uint? IlOffset, string? OpCode, string? ErrorMessage = null);
	public sealed record SettingsSectionInfo(string Path, string Name, int Depth, int AttributeCount, int ChildCount, string? ErrorMessage = null);
	public sealed record SettingAttributeData(string Name, string Value, string? ErrorMessage = null);
	public sealed record SettingsSectionData(string Path, string Name, SettingAttributeData[] Attributes, SettingsSectionData[] Children, string? ErrorMessage = null);
	public sealed record SettingsSearchResult(string SectionPath, string AttributeName, string AttributeValue, string? ErrorMessage = null);
	public sealed record ExceptionCategoryInfo(string Name, string DisplayName, string ShortDisplayName, string IdentifierKind, string? ErrorMessage = null);
	public sealed record ExceptionConditionInfo(string Type, string Value, string? ErrorMessage = null);
	public sealed record ExceptionSettingInfo(string Category, string CategoryDisplayName, string TargetKind, string Identifier, string DisplayName, string? Description, bool IsDefault, bool BreakWhenThrown, bool BreakOnSecondChance, ExceptionConditionInfo[] Conditions, string? ErrorMessage = null);
	public sealed record ExceptionSettingsListResult(ExceptionCategoryInfo[] Categories, ExceptionSettingInfo[] Settings, string? ErrorMessage = null);
	public sealed record ExceptionSettingsUpdateResult(bool Success, string Message, int UpdatedCount, ExceptionSettingInfo[] Settings, string? ErrorMessage = null);
	sealed record MethodResolutionResult(MethodDef? Method, string? Message, MethodInfoResult[] CandidateMethods);
}
