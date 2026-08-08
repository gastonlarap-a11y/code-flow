using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CodeFlow.Security;

/// <summary>
/// Windows Credential Manager access through the <c>Cred*</c> API in advapi32.
/// </summary>
/// <remarks>
/// <para>
/// Credentials are stored as <c>CRED_TYPE_GENERIC</c> with a target name of
/// <c>{service}.{account}</c>. That exact composition is what makes a CodeFlow 1.7.2 install's
/// credentials readable here — the strings alone are not enough if the target name is assembled
/// differently, so this format is a compatibility contract, not a choice.
/// </para>
/// <para>
/// <b>Not verified on Windows.</b> There is no Windows machine in this environment, so this is
/// written against the documented API and reviewed, not run. It must be exercised before the
/// credential slice is called done.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialManager : ICredentialBackend
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Get(string service, string account)
    {
        if (!CredRead(TargetName(service, account), CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw Failure(error, $"reading '{account}'");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Set(string service, string account, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        var pinned = Marshal.AllocHGlobal(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, pinned, blob.Length);

            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName(service, account),
                CredentialBlob = pinned,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CredPersistLocalMachine,
                UserName = account,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw Failure(Marshal.GetLastWin32Error(), $"storing '{account}'");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pinned);
        }
    }

    public void Delete(string service, string account)
    {
        if (CredDelete(TargetName(service, account), CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();

        // Nothing to delete is the caller's intended end state either way.
        if (error == ErrorNotFound)
        {
            return;
        }

        throw Failure(error, $"deleting '{account}'");
    }

    private static string TargetName(string service, string account) => $"{service}.{account}";

    private static CredentialStoreException Failure(int error, string operation) =>
        new($"the Windows Credential Manager failed while {operation}: " +
            $"{new Win32Exception(error).Message} (error {error})");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
