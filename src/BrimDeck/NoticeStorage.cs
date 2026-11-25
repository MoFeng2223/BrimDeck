using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// Where the recent notices are kept: a "notices" folder in BrimDeck's data folder in %LOCALAPPDATA%, beside settings.json, so
// every copy of BrimDeck the user runs (another install folder, a portable copy, a new build) reads the same notices. Earlier
// versions kept them in the install folder when it could be written to; a file left there is moved over once.
internal static class NoticeStorage
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "notices");

    public static string InstallFolder => Path.Combine(AppContext.BaseDirectory, "notices");

    // What BrimDeck makes of a folder. Linked: it is a link (junction, symbolic link). Foreign: another account owns it.
    // Shared: the user's own, but other accounts may also create, replace or delete files in it. Unreachable: it could not be
    // checked now (a network share that failed for a moment); it is checked again later.
    public enum FolderTrust { Trusted, Linked, Foreign, Shared, Unreachable }

    // Whether BrimDeck may read and write in the folder: it does not exist yet (BrimDeck creates it), or it is no link, is
    // owned by the current user, the administrators or the system, and no one else may write in it. unknown answers for a
    // drive without owners (FAT, exFAT). On a network share the owner and the rights are the server's accounts (a NAS, Samba),
    // which mean nothing here: only links are refused there (C-M2). repair closes a folder of the user's own that others may
    // write in (see MakePrivate) before judging it. Reads the disk: never on the UI thread for a folder the user chose.
    public static FolderTrust Check(string folder, bool unknown, bool repair = false)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return FolderTrust.Trusted;
            if (NoticeHistory.Linked(folder)) return FolderTrust.Linked;
            if (OnNetwork(folder)) return FolderTrust.Trusted;
            var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return unknown ? FolderTrust.Trusted : FolderTrust.Foreign;
            var user = WindowsIdentity.GetCurrent().User;
            if (owner != user && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return FolderTrust.Foreign;
            if (!OthersMayWrite(security, user)) return FolderTrust.Trusted;
            if (!repair || owner != user) return FolderTrust.Shared;
            MakePrivate(folder);
            return OthersMayWrite(info.GetAccessControl(AccessControlSections.Access), user) ? FolderTrust.Shared : FolderTrust.Trusted;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException) { return unknown ? FolderTrust.Trusted : FolderTrust.Foreign; }
        catch (IOException) { return FolderTrust.Unreachable; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return FolderTrust.Foreign; }
    }

    // A UNC path or a mapped network drive.
    private static bool OnNetwork(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal) && !full.StartsWith(@"\\.\", StringComparison.Ordinal)) return true;
            return Path.GetPathRoot(full) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return false; }
    }

    // The rights that let an account put another file, folder or link in place of BrimDeck's, or change who may.
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Whether an account other than the user, the system, the administrators or the folder's owner (CREATOR OWNER, OWNER
    // RIGHTS) is allowed to write in the folder or in what is created in it.
    private static bool OthersMayWrite(FileSystemSecurity security, SecurityIdentifier? user)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || ((FileSystemRights)NoticeHistory.SpecificRights((int)rule.FileSystemRights) & WriteRights) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || sid == user) continue;
            if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid.Value == "S-1-3-4" || sid.Value.StartsWith(TrustedInstaller, StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Checks the folder before anything is read from or written to it, and creates it private to the current user (the
    // default one is closed again if it was opened up). Anything but Trusted means BrimDeck leaves the folder alone: a folder
    // the user chose is reported, not used. Reads the disk (on a network share, it may wait for it): call it in the background,
    // once per folder. A folder whose drive is missing is not created; reading it reports that.
    public static FolderTrust Prepare(string folder, bool create = true)
    {
        bool isDefault = string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase);
        var trust = Check(folder, unknown: !isDefault, repair: isDefault);
        if (trust != FolderTrust.Trusted || !create || Directory.Exists(folder) || !NoticeHistory.Reachable(folder)) return trust;
        try { Directory.CreateDirectory(folder); if (!OnNetwork(folder)) MakePrivate(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return trust;
    }

    // Lets the user close a folder of their own that others may write in (the settings page), as BrimDeck's own is.
    public static bool Close(string folder)
    {
        MakePrivate(folder);
        return Check(folder, unknown: true) == FolderTrust.Trusted;
    }

    // Whether Close can take effect and changes nothing but BrimDeck's own (M3): the folder is the user's own (or BrimDeck runs
    // elevated and the administrators own it), and it holds only what BrimDeck keeps there, since the new rights pass down to
    // everything inside that inherits its rights. A folder with more than that is better replaced by another one. Reads the
    // disk: in the background.
    public static bool Closable(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || OnNetwork(folder)) return false;
            var user = WindowsIdentity.GetCurrent().User;
            if (info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return false;
            if (owner != user && !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))) return false;
            int seen = 0;
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if (++seen > 1000) return false;
                if (entry is DirectoryInfo icons)
                {
                    if (icons.Name != "icons" || (icons.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    foreach (var inner in icons.EnumerateFileSystemInfos())
                        if (++seen > 1000 || inner is DirectoryInfo || !NoticeHistory.OwnName(inner.Name, inIcons: true)) return false;
                }
                else if (!NoticeHistory.OwnName(entry.Name, inIcons: false)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException
                                   or NotSupportedException or InvalidOperationException or ArgumentException) { return false; }
    }

    public static string Describe(FolderTrust trust, bool closable = true) => trust switch
    {
        FolderTrust.Shared when !closable => Loc.T("其他账户也可以改动该文件夹中的文件，BrimDeck 不会在其中读写。请选择其他文件夹。",
            "Other accounts can change the files in this folder, so BrimDeck neither reads nor writes there. Choose another folder."),
        FolderTrust.Linked => Loc.T("该文件夹是链接，BrimDeck 不会在其中读写。请选择自己的文件夹。",
            "This folder is a link, so BrimDeck neither reads nor writes there. Choose a folder of your own."),
        FolderTrust.Foreign => Loc.T("该文件夹属于其他账户，BrimDeck 不会在其中读写。请选择自己的文件夹。",
            "This folder belongs to another account, so BrimDeck neither reads nor writes there. Choose a folder of your own."),
        FolderTrust.Shared => Loc.T("其他账户也可以改动该文件夹中的文件，BrimDeck 不会在其中读写。可以设为仅限本人访问，或选择其他文件夹。",
            "Other accounts can change the files in this folder, so BrimDeck neither reads nor writes there. Make it yours only, or choose another folder."),
        _ => ""
    };

    // A folder BrimDeck creates outside the user's profile (an install folder on another drive usually lets every signed-in
    // user change what is in it) is opened to the current user, the system and the administrators only (as the installer's
    // SecureAppDir leaves them in: M3), without inheriting the parent's rights.
    // The file inside is encrypted for this user anyway; this keeps other accounts from reading, replacing or deleting it.
    public static void MakePrivate(string folder)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException) { }
    }

    // Moves the saved notices and icons an earlier version kept in this copy's install folder into the data folder, once, when
    // the data folder has none yet. The install folder's "notices" goes once it is empty.
    public static void MoveLegacy(string folder)
    {
        var legacy = InstallFolder;
        if (!Move(legacy, folder)) return;
        try { if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Moves the saved notices and their icons from one folder into another that has no saved notices of its own (life-L1).
    // True when the file moved. The old folder may never have been checked (notices were not kept while it was chosen): only
    // BrimDeck's own file names move, and nothing through a link, as NoticeHistory.Delete removes them (N50-7).
    public static bool Move(string from, string folder)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (NoticeHistory.Linked(from)) return false;
            var file = Path.Combine(from, "notices.dat");
            var target = Path.Combine(folder, "notices.dat");
            if (!File.Exists(file) || File.Exists(target) || NoticeHistory.Linked(file)) return false;
            Directory.CreateDirectory(folder);
            File.Move(file, target);
            var icons = Path.Combine(from, "icons");
            if (Directory.Exists(icons) && !NoticeHistory.Linked(icons))
            {
                var into = Path.Combine(folder, "icons");
                Directory.CreateDirectory(into);
                foreach (var icon in Directory.EnumerateFiles(icons, "*.png"))
                {
                    // Only *.png is listed, so an icon's temporary file (.tmp) never comes up; of those, only names BrimDeck itself
                    // gives a saved icon (OwnName), and none that is a link.
                    if (!NoticeHistory.OwnName(Path.GetFileName(icon), inIcons: true) || NoticeHistory.Linked(icon)) continue;
                    var moved = Path.Combine(into, Path.GetFileName(icon));
                    if (!File.Exists(moved)) File.Move(icon, moved); else File.Delete(icon);
                }
            }
            NoticeIcons.DeleteStored(from);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
