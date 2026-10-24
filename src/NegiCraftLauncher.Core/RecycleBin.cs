using System.IO;
using System.Runtime.InteropServices;

namespace NegiCraftLauncher.Core;

/// <summary>
/// 删除用户文件（模组、存档、实例目录……）一律先进回收站：确认框挡得住手滑，
/// 挡不住点错对象。非 Windows 或外壳调用失败时退回永久删除 —— 宁可直接删，
/// 也不能出现「点了删除文件还在」的假成功。
/// </summary>
public static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoErrorUi = 0x400;

    /// <summary>删一个文件或一整棵目录。能进回收站就进，失败就永久删除。</summary>
    public static void Delete(string path)
    {
        if (!OperatingSystem.IsWindows() || !TrySendToRecycleBin(path)) PermanentDelete(path);
    }

    private static void PermanentDelete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    private static bool TrySendToRecycleBin(string path)
    {
        if (path.Length == 0) return false;

        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            // SHFileOperation 要的是双 \0 结尾；LPWStr 编组会补一个，这里再补一个。
            From = path + "\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };

        try
        {
            return SHFileOperation(ref op) == 0 && !op.AnyOperationsAborted;
        }
        catch
        {
            // 外壳不在了 / 权限异常 —— 交给调用方的永久删除兜底。
            return false;
        }
    }
}
