using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUI3Shell;

/// <summary>
/// 原生 Win32 通用项对话框封装。
/// 非打包的 WinUI 3 应用里 Windows.Storage.Pickers 的洞（FileOpenPicker / FolderPicker）
/// 会一直挂起不返回，这里直接调用 IFileOpenDialog / GetSaveFileName。
/// </summary>
internal static class NativeFileDialog
{
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>选择一个文件，取消时返回 null。</summary>
    public static string? PickFile(IntPtr owner, string title)
    {
        return Show(
            owner,
            title,
            FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
    }

    /// <summary>选择一个文件夹，取消时返回 null。</summary>
    public static string? PickFolder(IntPtr owner, string title)
    {
        return Show(
            owner,
            title,
            FOS_PICKFOLDERS | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
    }

    private static string? Show(IntPtr owner, string title, uint options)
    {
        // CLSID_FileOpenDialog
        var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        Type? clsidType = Type.GetTypeFromCLSID(clsid);
        if (clsidType is null)
        {
            return null;
        }

        object? instance = Activator.CreateInstance(clsidType);
        if (instance is not IFileDialog dialog)
        {
            return null;
        }

        try
        {
            // 让对话框归主窗口所有，确保模态并出现在窗口之上
            (instance as IInitializeWithWindow)?.Initialize(owner);

            dialog.SetOptions(options);
            if (title.Length > 0)
            {
                dialog.SetTitle(title);
            }

            int hr = dialog.Show(owner);
            if (hr != 0)
            {
                // 包括用户取消（0x800704C7）
                return null;
            }

            IShellItem? item = dialog.GetResult();
            if (item is null)
            {
                return null;
            }

            string path;
            item.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>另存为对话框，取消时返回 null。</summary>
    public static string? PickSaveFile(IntPtr owner, string title, string suggestedName, string filter)
    {
        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int MAX_PATH_CHARS = 260;

        IntPtr filterPtr = Marshal.StringToHGlobalUni(filter);
        IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
        IntPtr extensionPtr = Marshal.StringToHGlobalUni("cpp");
        IntPtr filePtr = Marshal.AllocHGlobal(MAX_PATH_CHARS * sizeof(char));

        try
        {
            // 预填建议的文件名，剩余部分清零
            for (int i = 0; i < MAX_PATH_CHARS; i++)
            {
                short code = i < suggestedName.Length ? (short)suggestedName[i] : (short)0;
                Marshal.WriteInt16(filePtr, i * sizeof(char), code);
            }

            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = filePtr,
                nMaxFile = MAX_PATH_CHARS,
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpstrTitle = titlePtr,
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                lpstrDefExt = extensionPtr,
            };

            if (!GetSaveFileNameW(ref dialog))
            {
                return null;
            }

            // 从缓冲区读回用户选择的路径
            var builder = new StringBuilder(MAX_PATH_CHARS);
            for (int i = 0; i < MAX_PATH_CHARS; i++)
            {
                short code = Marshal.ReadInt16(filePtr, i * sizeof(char));
                if (code == 0)
                {
                    break;
                }
                builder.Append((char)code);
            }

            string path = builder.ToString();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(extensionPtr);
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(filterPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName openFileName);

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);

        void SetFileTypes(uint count, IntPtr filterSpec);

        void SetFileTypeIndex(uint index);

        uint GetFileTypeIndex();

        uint Advise(IntPtr events);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        uint GetOptions();

        void SetDefaultFolder(IShellItem folder);

        void SetFolder(IShellItem folder);

        // 这两个方法在 vtable 里真实存在，不能省略：
        // COM 调用是按槽位顺序寻址的，少写一个方法后面的所有调用都会整体错位
        void GetFolder(out IShellItem folder);

        void GetCurrentSelection(out IShellItem item);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        IShellItem GetResult();

        void AddPlace(IShellItem item, int placement);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        void Close(int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);

        void GetParent(out IShellItem parent);

        void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(IShellItem other, uint hint, out int order);
    }
}
