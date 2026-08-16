using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using NitTray.Services.Native;

namespace NitTray.Services;

// Guards the one place NitTray asks for administrator rights. Driver setup launches
// NitTray.DriverSetup.exe with "runas" from a per-user folder the signed-in user can
// write to, so a replaced helper would turn NitTray's own UAC prompt into a lure.
//
// This raises the bar rather than sealing the folder. Rewriting NitTray.exe itself, or
// planting a DLL the helper imports, is out of reach of any check we can make here.
internal static class HelperTrust
{
    // Read at startup, not at click time: Windows lets a standard user rename a running
    // executable while Environment.ProcessPath still reports the original path, so an
    // attacker could drop an unsigned file there and win the "not signed" branch below.
    private static volatile SignerIdentity? _appSigner;

    // Subject is for the log. The key is the identity: a certificate can carry any
    // subject an attacker types, but not someone else's public key.
    private sealed record SignerIdentity(string PublicKey, string Subject);

    public static void CaptureAppIdentity()
    {
        var appPath = Environment.ProcessPath;
        _appSigner = string.IsNullOrEmpty(appPath) ? null : ReadSigner(appPath);

        DiagnosticLog.Write(_appSigner is null
            ? "Helper verification is off: NitTray is unsigned, so there is no publisher to match."
            : $"Helper verification anchored to '{_appSigner.Subject}'.");
    }

    // reason always explains the verdict, for the diagnostics log.
    public static bool IsTrustedToElevate(string helperPath, out string reason)
    {
        var app = _appSigner;
        if (app is null)
        {
            reason = "NitTray itself is not signed (a build from source), so there is " +
                     "no publisher to match the helper against";
            return true;
        }

        var status = VerifyEmbeddedSignature(helperPath);
        if (status != 0)
        {
            reason = $"the helper has no valid Authenticode signature " +
                     $"(WinVerifyTrust returned 0x{status:X8})";
            return false;
        }

        var helper = ReadSigner(helperPath);
        if (helper is null)
        {
            reason = "the helper's signing certificate could not be read";
            return false;
        }

        // Release signing stamps the app and helper in one pass, so a genuine pair
        // always carries the same key.
        if (!string.Equals(helper.PublicKey, app.PublicKey, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"the helper is signed by '{helper.Subject}' but NitTray is signed " +
                     $"by '{app.Subject}'";
            return false;
        }

        reason = $"the helper carries a valid signature from '{helper.Subject}'";
        return true;
    }

    // Signer of a signed file, or null when unsigned. Says nothing about whether the
    // signature is valid. Pair it with VerifyEmbeddedSignature.
    private static SignerIdentity? ReadSigner(string path)
    {
        try
        {
            // .NET has no replacement for reading an Authenticode signer
            // (dotnet/runtime#108740), so suppressing is the documented workaround.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var key = certificate.GetPublicKeyString();
            return string.IsNullOrEmpty(key)
                ? null
                : new SignerIdentity(key, certificate.Subject);
        }
        catch (Exception)
        {
            // Unsigned files throw. Anything unreadable must not be trusted either.
            return null;
        }
    }

    // 0 when the file's Authenticode signature is intact and chains to a trusted root.
    private static int VerifyEmbeddedSignature(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustNative.WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(
                new WinTrustNative.WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustNative.WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pathPtr,
                },
                filePtr,
                fDeleteOld: false);

            var data = new WinTrustNative.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustNative.WINTRUST_DATA>(),
                dwUIChoice = WinTrustNative.WTD_UI_NONE,
                fdwRevocationChecks = WinTrustNative.WTD_REVOKE_NONE,
                dwUnionChoice = WinTrustNative.WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WinTrustNative.WTD_STATEACTION_VERIFY,
                dwProvFlags = WinTrustNative.WTD_SAFER_FLAG
                              | WinTrustNative.WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            var action = WinTrustNative.GenericVerifyV2;
            // INVALID_HANDLE_VALUE means "no window": never prompt, just report.
            var result = WinTrustNative.WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            // The provider allocates state on the verify call. A matching close frees it.
            data.dwStateAction = WinTrustNative.WTD_STATEACTION_CLOSE;
            _ = WinTrustNative.WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Driver setup: signature check threw ({ex.Message}).");
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
