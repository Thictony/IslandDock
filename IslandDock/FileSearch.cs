using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static IslandDock.Native;

namespace IslandDock;

internal sealed record FileHit(string Name, string Folder, string Path, ImageSource? Icon);

/// <summary>
/// Finds files by name the way Start search does, through the Windows Search index. If the index can't be
/// queried (the service is off), falls back to walking the usual user folders.
/// </summary>
internal static class FileSearch
{
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static List<FileHit> Find(string text, int max, CancellationToken ct)
    {
        List<string> paths;
        try { paths = QueryIndex(text, max * 2); }
        catch { paths = Walk(text, max * 2, ct); }

        // Names that start with what was typed first, then the rest (each group stays newest first).
        return paths
            .OrderBy(p => System.IO.Path.GetFileName(p).StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(max)
            .Select(p => new FileHit(System.IO.Path.GetFileName(p), System.IO.Path.GetDirectoryName(p) ?? "", p, IconFor(p)))
            .ToList();
    }

    static List<string> QueryIndex(string text, int max)
    {
        // Every word must prefix-match a word of the file name, e.g. "tax 23" finds "Taxes 2023.pdf".
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", "").Replace("'", "''"))
            .Where(w => w.Length > 0)
            .Select(w => $"\"{w}*\"");
        string match = string.Join(" AND ", words);
        if (match.Length == 0) return new();
        string sql = $"SELECT TOP {max} System.ItemPathDisplay FROM SystemIndex " +
                     $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{match}') ORDER BY System.DateModified DESC";

        var type = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("ADO isn't available");
        dynamic conn = Activator.CreateInstance(type)!;
        conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        var result = new List<string>();
        try
        {
            dynamic rs = conn.Execute(sql);
            while (!(bool)rs.EOF)
            {
                if (rs.Fields.Item(0).Value is string p) result.Add(p);
                rs.MoveNext();
            }
            rs.Close();
        }
        finally { conn.Close(); }
        return result;
    }

    static List<string> Walk(string text, int max, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            System.IO.Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        var result = new List<string>();
        foreach (var root in roots.Where(Directory.Exists).Distinct())
        {
            try
            {
                foreach (var p in Directory.EnumerateFileSystemEntries(root, $"*{text}*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(p);
                    if (result.Count >= max) return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return result;
    }

    /// <summary>Shell icon: shared per extension, except files that carry their own (apps, shortcuts, icons).</summary>
    static ImageSource? IconFor(string path)
    {
        bool dir = Directory.Exists(path);
        string ext = System.IO.Path.GetExtension(path);
        bool own = !dir && ext.ToLowerInvariant() is ".exe" or ".lnk" or ".ico" or ".url";
        string key = dir ? "<folder>" : own ? path : ext;
        return Icons.GetOrAdd(key, _ =>
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | (own ? 0 : SHGFI_USEFILEATTRIBUTES);
            string query = own ? path : dir ? "folder" : "file" + ext;
            if (SHGetFileInfo(query, dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        });
    }
}
