using System.Runtime.InteropServices;

namespace EggmansReleaseManager.Services;

/// <summary>Sends files to the Windows Recycle Bin (SHFileOperation with FOF_ALLOWUNDO) so a cleanup can be undone.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
                         FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static bool Send(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var list = paths.Select(Path.GetFullPath).ToList();
        if (list.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', list) + "\0\0",   // double-null-terminated list
            // FOF_WANTNUKEWARNING: if a file can't go to the bin (e.g. too big), Windows asks instead of silently deleting
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
        };
        // Shell APIs want an STA thread; callers may be on the thread pool.
        var ok = false;
        var t = new Thread(() => ok = SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return ok;
    }
}
