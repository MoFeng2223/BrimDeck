using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// The icon of a chat BrimDeck watches by its taskbar button (ChatFlashApps): such a chat sends no Windows notification, so no
// notice names an icon for it. Its program is found in Windows' list of installed programs (the Uninstall keys of the computer,
// in both views, and of the user), and the icon is the program file's own ("exe:<path>", read by NoticeIcons). Found once; a chat
// that is not installed is looked for again after a minute.
internal static class ChatAppIcons
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, (string? Ref, DateTime At)> s_found = new(StringComparer.Ordinal);

    // "exe:<path>" for a listed chat's key ("flash:wechat") when its program is installed; null otherwise.
    public static string? Ref(string appKey)
    {
        if (!appKey.StartsWith("flash:", StringComparison.Ordinal) || ChatFlashApps.All.FirstOrDefault(app => "flash:" + app.Id == appKey) is not { } app) return null;
        var now = DateTime.UtcNow;
        if (s_found.TryGetValue(app.Id, out var known) && (known.Ref is not null || now - known.At < Retry)) return known.Ref;
        string? found = ChatFlashApps.InstalledPath(app, DisplayIcons()) is { } path && File.Exists(path) ? "exe:" + path : null;
        s_found[app.Id] = (found, now);
        return found;
    }

    private static IEnumerable<string?> DisplayIcons()
    {
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            var icons = new List<string?>();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(Uninstall);
                if (list is null) continue;
                foreach (var name in list.GetSubKeyNames())
                {
                    try { using var entry = list.OpenSubKey(name); icons.Add(entry?.GetValue("DisplayIcon") as string); }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            foreach (var icon in icons) yield return icon;
        }
    }
}
