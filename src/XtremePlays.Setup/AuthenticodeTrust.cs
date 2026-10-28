using System.Runtime.InteropServices;

namespace XtremePlays.Setup;

internal static class AuthenticodeTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint UiChoiceNone = 2;
    private const uint UnionChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    public static bool VerifyFile(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = pathPointer
        };

        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiChoiceNone,
                UnionChoice = UnionChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = StateActionVerify
            };

            var actionId = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData);
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = StateActionClose;
                actionId = GenericVerifyV2;
                _ = WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData);
            }
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
}
