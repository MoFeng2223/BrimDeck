using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BrimDeck.Modules;

public static partial class ModulePackageInspector
{
    private sealed partial class Inspection
    {
        private sealed record TypeLink(string Assembly, string Name);
        private sealed record TypeShape(bool Public, bool Abstract, bool Constructor, TypeLink? Base, IReadOnlyList<TypeLink> Interfaces, bool UnresolvedHierarchy);
        private sealed record AssemblyShape(string File, Dictionary<string, TypeShape> Types);
        private void ReadAssembly(string path)
        {
            string file = Path.GetRelativePath(directory, path).Replace('\\', '/');
            try
            {
                using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
                if (!pe.HasMetadata) { Warning("native-library", file, "Native binary; behavior and compatibility are not inspected."); return; }
                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly) { Error("invalid-assembly", file, "Managed DLL has no assembly definition."); return; }
                if (reader.TypeDefinitions.Count > 50000 || reader.TypeReferences.Count > 50000 || reader.MemberReferences.Count > 100000
                    || reader.AssemblyReferences.Count > 4096 || reader.MethodDefinitions.Count > 100000)
                { Error("metadata-limit", file, "Assembly metadata exceeds inspection limits."); return; }
                var definition = reader.GetAssemblyDefinition(); string name = reader.GetString(definition.Name);
                if (Shared.Contains(name)) Error("shared-assembly-bundled", file, "Do not distribute another copy of the host or shared SDK.");
                var references = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).Order(StringComparer.Ordinal).ToArray();
                var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string TypeName(TypeDefinitionHandle handle, int depth = 0)
                {
                    if (depth > 32) throw new BadImageFormatException("Nested type depth exceeds inspection limits.");
                    var type = reader.GetTypeDefinition(handle); string simple = reader.GetString(type.Name);
                    return type.GetDeclaringType() is { IsNil: false } parent ? TypeName(parent, depth + 1) + "+" + simple
                        : reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." + simple : simple;
                }
                TypeLink? Link(EntityHandle handle, int depth = 0)
                {
                    if (handle.IsNil || depth > 32) return null;
                    if (handle.Kind == HandleKind.TypeDefinition) return new(name, TypeName((TypeDefinitionHandle)handle));
                    if (handle.Kind != HandleKind.TypeReference) return null;
                    var type = reader.GetTypeReference((TypeReferenceHandle)handle);
                    string simple = reader.GetString(type.Name), ns = reader.GetString(type.Namespace);
                    if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                    { var parent = Link(type.ResolutionScope, depth + 1); return parent is null ? null : new(parent.Assembly, parent.Name + "+" + simple); }
                    string owner = type.ResolutionScope.Kind == HandleKind.AssemblyReference
                        ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) : name;
                    return new(owner, ns.Length > 0 ? ns + "." + simple : simple);
                }
                bool Public(TypeDefinitionHandle handle, int depth = 0)
                {
                    if (depth > 32) return false;
                    var type = reader.GetTypeDefinition(handle);
                    var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                    return visibility == TypeAttributes.Public || visibility == TypeAttributes.NestedPublic
                        && !type.GetDeclaringType().IsNil && Public(type.GetDeclaringType(), depth + 1);
                }
                var types = new Dictionary<string, TypeShape>(StringComparer.Ordinal);
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle); bool constructor = false;
                    foreach (var methodHandle in type.GetMethods())
                    {
                        var method = reader.GetMethodDefinition(methodHandle); string methodName = reader.GetString(method.Name);
                        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                        {
                            var import = method.GetImport(); if (!import.Module.IsNil) imports.Add(reader.GetString(reader.GetModuleReference(import.Module).Name));
                        }
                        if (methodName == ".cctor" && reader.GetString(type.Name) == "<Module>") Warning("module-initializer-present", file, "Assembly has a module initializer. It was not executed by this check.");
                        if (methodName != ".ctor" || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
                        var signature = reader.GetBlobReader(method.Signature); var header = signature.ReadSignatureHeader();
                        if (header.IsGeneric) signature.ReadCompressedInteger();
                        if (signature.ReadCompressedInteger() == 0) constructor = true;
                    }
                    types.Add(TypeName(handle), new(Public(handle),
                        (type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0 || type.GetGenericParameters().Count > 0, constructor, Link(type.BaseType),
                        type.GetInterfaceImplementations().Select(h => Link(reader.GetInterfaceImplementation(h).Interface)).OfType<TypeLink>().ToArray(),
                        !type.BaseType.IsNil && Link(type.BaseType) is null || type.GetInterfaceImplementations().Any(h => Link(reader.GetInterfaceImplementation(h).Interface) is null)));
                }
                if (!_shapes.TryAdd(name, new(file, types))) Error("duplicate-assembly-name", file, "Multiple package files declare the same assembly name.");
                _assemblies.Add(new(file, name, definition.Version.ToString(), Array.AsReadOnly(references), Array.AsReadOnly(imports.Order(StringComparer.Ordinal).ToArray())));
                foreach (string import in imports) Warning("native-import", file + " -> " + import, "P/Invoke dependency observed; no native code was loaded or called.");
                foreach (var handle in reader.TypeReferences)
                    if (Link(handle) is { } link) CheckSdkReference(file, link, null);
                foreach (var handle in reader.MemberReferences)
                {
                    var member = reader.GetMemberReference(handle);
                    if (Link(member.Parent) is { } link) CheckSdkReference(file, link, reader.GetString(member.Name));
                }
            }
            catch (Exception error) when (ReadError(error) || error is InvalidOperationException)
            { Error("invalid-assembly", file, error.Message); }
        }
        private void CheckEntry()
        {
            string file = _manifest!.Assembly!;
            var entryAssembly = _shapes.FirstOrDefault(p => string.Equals(p.Value.File, file, StringComparison.OrdinalIgnoreCase));
            if (entryAssembly.Value is null) { Error("entry-assembly-missing", file, "Entry DLL is missing or is not a valid managed assembly."); return; }
            if (!entryAssembly.Value.Types.TryGetValue(_manifest.EntryType!, out var type))
            { Error("entry-type-missing", file, "Declared entryType was not found in the entry DLL."); return; }
            if (!type.Public || type.Abstract || !type.Constructor)
                Error("invalid-entry-type", _manifest.EntryType!, "Entry must be a public, concrete type with a public parameterless constructor.");
            var visited = new HashSet<TypeLink>();
            bool? Implements(TypeLink link)
            {
                if (link.Assembly == "BrimDeck.Modules.Abstractions" && link.Name == "BrimDeck.Modules.IModule") return true;
                if (visited.Count >= 128) return null;
                if (!visited.Add(link)) return false;
                if (!_shapes.TryGetValue(link.Assembly, out var assembly) || !assembly.Types.TryGetValue(link.Name, out var shape))
                    return link.Assembly is "System.Runtime" or "System.Private.CoreLib" || link.Name == "System.Object" ? false : null;
                bool unknown = shape.UnresolvedHierarchy;
                foreach (var contract in shape.Interfaces)
                { var match = Implements(contract); if (match == true) return true; unknown |= match is null; }
                if (shape.Base is { } parent) { var match = Implements(parent); if (match == true) return true; unknown |= match is null; }
                return unknown ? null : false;
            }
            bool? implements = Implements(new(entryAssembly.Key, _manifest.EntryType!));
            if (implements == false) Error("entry-contract-missing", _manifest.EntryType!, "Entry does not implement IModule in the inspected type hierarchy.");
            if (implements is null) Warning("entry-contract-unverified", _manifest.EntryType!, "Could not fully resolve the entry's type hierarchy statically. Runtime validation still applies.");
        }
        private void CheckSdkReference(string file, TypeLink type, string? member)
        {
            if (type.Assembly is not ("BrimDeck.Modules.Abstractions" or "BrimDeck.UI")) return;
            string location = file + " -> " + type.Name + (member is null ? "" : "." + member);
            var capability = ModuleApi.Catalog.FirstOrDefault(c => c.ContractType == type.Name && c.ContractAssembly == type.Assembly);
            if (member is null && capability is { Kind: not ModuleCapabilityKind.Intrinsic })
            {
                if (!ModulePolicy.Declares(_manifest!, capability.Id)) Warning("sdk-capability-not-declared", location, "SDK type references " + capability.Id + "; verify the manifest declares the capability used by your code.");
                if (capability.Permission is { } permission && !_manifest!.Permissions.Contains(permission))
                    Warning("sdk-permission-not-declared", location, "SDK type references a service requiring " + permission + ". Type references alone do not prove a call.");
            }
            int minor = capability?.IntroducedMinor ?? 0;
            if (type.Name is "BrimDeck.IModuleSettingsNavigation" or "BrimDeck.ModuleSettingsPlacement" or "BrimDeck.Modules.ModuleDiagnostic" or "BrimDeck.Modules.ModuleDiagnosticSeverity") minor = 6;
            if (type.Name is "BrimDeck.Modules.IModulePlatform" or "BrimDeck.Modules.IModuleRegistries" or "BrimDeck.Modules.IModuleServices" or "BrimDeck.Modules.IModuleEvents") minor = 5;
            if (type.Name == "BrimDeck.IModuleAsyncActivity") minor = 4;
            if (type.Name == "BrimDeck.ModulePage" && member == "Async") minor = 4;
            if (type.Name == "BrimDeck.Modules.ModuleContextExtensions" && member == "Platform") minor = 5;
            if (type.Name == "BrimDeck.ModuleViewContext" && member is "get_IsVisible" or "OnVisibilityChanged" or "EveryVisible") minor = 6;
            if (type.Name == "BrimDeck.ModuleUi" && member is "get_Secondary" or "get_Tertiary" or "get_OnAccent") minor = 6;
            if (type.Name == "BrimDeck.ModuleSetting" && member is "set_Step" or "set_MaximumLength" or "set_Group" or "set_GroupEn" or "set_Description" or "set_DescriptionEn" or "set_Placeholder" or "set_PlaceholderEn") minor = 6;
            if (minor > Math.Max(_manifest!.ApiMinor, _manifest.Requires?.ContractMinor ?? 0))
                Error("sdk-minor-too-low", location, "Referenced SDK surface requires apiMinor >= " + minor + ". The static map covers known additions, not every possible call.");
        }
    }
}
