using System.Runtime.InteropServices;

namespace NitTray.Services.Native;

// Just enough of wintrust.dll to ask Windows whether a file's Authenticode signature is
// intact and chains to a trusted root. Reading the certificate alone is not enough: a
// tampered copy keeps it, and only WinVerifyTrust checks it against the file's hash.
internal static class WinTrustNative
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2, the standard Authenticode policy provider.
    public static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;

    // Off deliberately: revocation needs network access, so an offline machine would
    // otherwise fail a good signature and block driver setup.
    public const uint WTD_REVOKE_NONE = 0;

    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_SAFER_FLAG = 0x00000100;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        // LPCWSTR. A pointer rather than a string keeps the struct blittable.
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    public static extern int WinVerifyTrust(
        IntPtr hwnd,
        ref Guid pgActionID,
        ref WINTRUST_DATA pWVTData);
}
