using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandDock;

/// <summary>
/// The icon the taskbar shows for a window, looked up by its AppUserModelID. Store apps like Settings live in an
/// ApplicationFrameHost window whose own icon and exe icon are blank; their real icon is in shell:AppsFolder.
/// </summary>
internal static class ShellIcon
{
    static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Link = new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25");

    public static ImageSource? ForWindow(IntPtr h, int size = 32) => AppIdFor(h) is string id ? ForAppId(id, size) : null;

    public static ImageSource? ForAppId(string id, int size = 32) => ForItem(@"shell:AppsFolder\" + id, size);

    /// <summary>The AppUserModelID a window was given, which ties it to its Store app or taskbar pin.</summary>
    public static string? AppIdFor(IntPtr h)
    {
        var iid = typeof(IPropertyStore).GUID;
        return SHGetPropertyStoreForWindow(h, ref iid, out var store) == 0 ? ReadString(store, AppUserModel, 5) : null;
    }

    /// <summary>A shortcut's AppUserModelID (set by apps like Discord) and target exe (empty for shell items like Explorer).</summary>
    public static (string? AppId, string? Target) ForShortcut(string lnk)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0, ref iid, out var store) != 0) return (null, null);
        string? id = ReadString(store, AppUserModel, 5, release: false);
        string? target = ReadString(store, Link, 2 /* System.Link.TargetParsingPath */);
        return (id, string.IsNullOrEmpty(target) ? null : target);
    }

    static string? ReadString(IPropertyStore store, Guid format, uint id, bool release = true)
    {
        try
        {
            var key = new PropertyKey { FormatId = format, Id = id };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { if (release) Marshal.ReleaseComObject(store); }
    }

    /// <summary>What Explorer calls a file or app, e.g. "Spotify" for its AppsFolder entry.</summary>
    public static string? DisplayName(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) != 0) return null;
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    /// <summary>The icon Explorer draws for a file, shortcut (without the arrow) or app, at the size asked for.</summary>
    public static ImageSource? ForItem(string parsingName, int size = 32)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item) != 0) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var factory = (IShellItemImageFactory)item;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0x4 /* SIIGBF_ICONONLY */, out hbm) != 0) return null;
            return FromDib(hbm);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// Copies a 32-bit DIB section keeping its alpha (Imaging.CreateBitmapSourceFromHBitmap drops it, leaving a black
    /// square behind the icon). The shell hands these out premultiplied, and bottom-up unless the height is negative.
    /// </summary>
    static ImageSource? FromDib(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DibSection>(), out var dib) == 0 || dib.Bits == IntPtr.Zero || dib.BitsPerPixel != 32) return null;
        int w = dib.Width, h = dib.Height, stride = dib.WidthBytes;
        BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, dib.Bits, stride * h, stride);
        if (dib.HeaderHeight > 0) src = new TransformedBitmap(src, new ScaleTransform(1, -1));
        src.Freeze();
        return src;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid FormatId; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Type; ushort r1, r2, r3; public IntPtr Pointer; IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int Width, Height; }

    // DIBSECTION: a BITMAP, then the BITMAPINFOHEADER it was made from, then bitfields and section info.
    [StructLayout(LayoutKind.Sequential)]
    struct DibSection
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public IntPtr Bits;
        public int HeaderSize, HeaderWidth, HeaderHeight;
        public ushort HeaderPlanes, HeaderBitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int BitField0, BitField1, BitField2;
        public IntPtr Section;
        public int Offset;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DibSection dib);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}
