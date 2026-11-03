using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BrimDeck.Modules;

// Direct assembly references of every packaged DLL, including nested private helpers. This answers whether
// the module reaches into an assembly that is not contract, without executing any assembly.
// BrimDeck.Modules.Abstractions and BrimDeck.UI are the contract; BrimDeck, BrimDeck.Core and BrimDeck.Modules
// are host internals with no compatibility promise.
internal static class ModuleAssemblyScan
{
    private static readonly string[] Internals = ["BrimDeck", "BrimDeck.Core", "BrimDeck.Modules"];

    internal static bool ReferencesInternals(string directory)
    {
        foreach (string file in ModuleCatalog.Files(directory).Where(file => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ModuleManifestReader.Unlinked(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.AssemblyReferences)
                {
                    string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                    if (Array.IndexOf(Internals, name) >= 0 || Internals.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
                }
            }
            // Native libraries have no managed references. One such file must not hide later helpers.
            catch (BadImageFormatException) { }
        }
        return false;
    }
}
