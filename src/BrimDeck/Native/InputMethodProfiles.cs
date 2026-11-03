using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BrimDeck.Native;

internal sealed record InstalledInputMethod(string Id, string Name, ushort Language, uint Type,
    Guid ClassId, Guid ProfileId, nint Layout, bool Enabled);

// Owns TSF interfaces on the thread which created it. Never call this from a keyboard hook.
internal sealed class InputMethodProfiles : IDisposable
{
    private static readonly Guid KeyboardCategory = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    private readonly object _profiles = Activator.CreateInstance(Type.GetTypeFromCLSID(new("33C53A50-F456-4884-B049-85FD643ECFED"))!)!;
    private IProfileManager Manager => (IProfileManager)_profiles;

    internal IReadOnlyList<InstalledInputMethod> Enumerate()
    {
        Marshal.ThrowExceptionForHR(Manager.EnumProfiles(0, out var enumerator));
        var result = new List<InstalledInputMethod>();
        try
        {
            int hr;
            while ((hr = enumerator.Next(1, out var profile, out uint fetched)) >= 0 && fetched == 1)
            {
                if (profile.Type == 1 && profile.Category != KeyboardCategory) continue;
                if (profile.Type == 2 && (profile.Flags & 4) != 0) continue;
                string name = "";
                if (profile.Type == 1)
                    ((IProfiles)_profiles).GetLanguageProfileDescription(profile.ClassId, profile.Language, profile.ProfileId, out name);
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{(uint)(long)profile.Layout:X8}");
                    name = key?.GetValue("Layout Text") as string ?? "";
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try { name = CultureInfo.GetCultureInfo(profile.Language).NativeName; }
                    catch (CultureNotFoundException) { name = $"{profile.Language:X4}"; }
                }
                result.Add(ToInstalled(profile, name));
            }
            Marshal.ThrowExceptionForHR(hr);
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result.DistinctBy(p => p.Id).ToArray();
    }

    internal InstalledInputMethod? ActiveOnCallingThread()
        => Manager.GetActiveProfile(KeyboardCategory, out var profile) == 0 ? ToInstalled(profile, "") : null;

    private static InstalledInputMethod ToInstalled(Profile p, string name)
        => new(p.Type == 1 ? $"tip:{p.Language:X4}:{p.ClassId:D}:{p.ProfileId:D}" : $"layout:{(uint)(long)p.Layout:X8}",
            name, p.Language, p.Type, p.ClassId, p.ProfileId, p.Type == 1 ? p.Substitute : p.Layout, (p.Flags & 2) != 0);

    public void Dispose() => Marshal.ReleaseComObject(_profiles);

    [StructLayout(LayoutKind.Sequential)]
    private struct Profile
    {
        public uint Type;
        public ushort Language;
        public Guid ClassId, ProfileId, Category;
        public nint Substitute;
        public uint Capabilities;
        public nint Layout;
        public uint Flags;
    }

    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProfileManager
    {
        [PreserveSig] int ActivateProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort language, in Guid classId, in Guid profileId, nint layout, out Profile profile);
        [PreserveSig] int EnumProfiles(ushort language, out IProfileEnumerator enumerator);
        [PreserveSig] int ReleaseInputProcessor(in Guid classId, uint flags);
        [PreserveSig] int RegisterProfile(in Guid classId, ushort language, in Guid profileId, nint description, uint descriptionLength,
            nint iconFile, uint iconLength, uint iconIndex, nint substitute, uint layout, int enabled, uint flags);
        [PreserveSig] int UnregisterProfile(in Guid classId, ushort language, in Guid profileId, uint flags);
        [PreserveSig] int GetActiveProfile(in Guid category, out Profile profile);
    }

    [ComImport, Guid("71C6E74D-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProfileEnumerator
    {
        [PreserveSig] int Clone(out IProfileEnumerator enumerator);
        [PreserveSig] int Next(uint count, out Profile profile, out uint fetched);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(uint count);
    }

    [ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProfiles
    {
        [PreserveSig] int Register(in Guid classId);
        [PreserveSig] int Unregister(in Guid classId);
        [PreserveSig] int AddLanguageProfile(in Guid classId, ushort language, in Guid profileId, nint description, uint descriptionLength, nint icon, uint iconLength, uint iconIndex);
        [PreserveSig] int RemoveLanguageProfile(in Guid classId, ushort language, in Guid profileId);
        [PreserveSig] int EnumInputProcessorInfo(out nint enumerator);
        [PreserveSig] int GetDefaultLanguageProfile(ushort language, in Guid category, out Guid classId, out Guid profileId);
        [PreserveSig] int SetDefaultLanguageProfile(ushort language, in Guid classId, in Guid profileId);
        [PreserveSig] int ActivateLanguageProfile(in Guid classId, ushort language, in Guid profileId);
        [PreserveSig] int GetActiveLanguageProfile(in Guid classId, out ushort language, out Guid profileId);
        [PreserveSig] int GetLanguageProfileDescription(in Guid classId, ushort language, in Guid profileId, [MarshalAs(UnmanagedType.BStr)] out string description);
    }
}
