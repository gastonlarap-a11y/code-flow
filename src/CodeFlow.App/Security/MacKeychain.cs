using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CodeFlow.Security;

/// <summary>
/// macOS Keychain access through Security.framework's <c>SecItem</c> API.
/// </summary>
/// <remarks>
/// <para>
/// Items are generic passwords identified by <c>kSecAttrService</c> plus <c>kSecAttrAccount</c>,
/// which is exactly how a CodeFlow 1.7.2 install filed them. That is why an existing install's
/// credentials are readable here: the item shape matches, not just the strings.
/// </para>
/// <para>
/// The legacy <c>SecKeychainAddGenericPassword</c> family would be a third of this code, and is
/// deprecated — it also targets the file-based keychain rather than the data protection keychain,
/// so it is not a like-for-like substitute.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychain : ICredentialBackend
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";

    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>No item matched the query. Not an error — the caller asked, the answer is "none".</summary>
    private const int ErrSecItemNotFound = -25300;

    private const int ErrSecSuccess = 0;

    public string? Get(string service, string account)
    {
        using var query = new CFDictionaryBuilder()
            .Add(SecClass, SecClassGenericPassword)
            .Add(SecAttrService, service)
            .Add(SecAttrAccount, account)
            .Add(SecReturnData, CFBooleanTrue)
            .Add(SecMatchLimit, SecMatchLimitOne)
            .Build();

        var status = SecItemCopyMatching(query.Handle, out var result);

        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        Check(status, $"reading '{account}'");

        try
        {
            return CopyBytes(result);
        }
        finally
        {
            if (result != IntPtr.Zero)
            {
                CFRelease(result);
            }
        }
    }

    public void Set(string service, string account, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);

        // Update first: SecItemAdd fails with errSecDuplicateItem on an existing account, and
        // delete-then-add would leave a window where the credential does not exist at all.
        using var identity = new CFDictionaryBuilder()
            .Add(SecClass, SecClassGenericPassword)
            .Add(SecAttrService, service)
            .Add(SecAttrAccount, account)
            .Build();

        using (var changes = new CFDictionaryBuilder().Add(SecValueData, bytes).Build())
        {
            var updated = SecItemUpdate(identity.Handle, changes.Handle);
            if (updated == ErrSecSuccess)
            {
                return;
            }

            if (updated != ErrSecItemNotFound)
            {
                Check(updated, $"updating '{account}'");
            }
        }

        using var addition = new CFDictionaryBuilder()
            .Add(SecClass, SecClassGenericPassword)
            .Add(SecAttrService, service)
            .Add(SecAttrAccount, account)
            .Add(SecValueData, bytes)
            .Build();

        Check(SecItemAdd(addition.Handle, IntPtr.Zero), $"storing '{account}'");
    }

    public void Delete(string service, string account)
    {
        using var query = new CFDictionaryBuilder()
            .Add(SecClass, SecClassGenericPassword)
            .Add(SecAttrService, service)
            .Add(SecAttrAccount, account)
            .Build();

        var status = SecItemDelete(query.Handle);

        // Nothing to delete is the caller's intended end state either way.
        if (status == ErrSecItemNotFound)
        {
            return;
        }

        Check(status, $"deleting '{account}'");
    }

    private static void Check(int status, string operation)
    {
        if (status == ErrSecSuccess)
        {
            return;
        }

        throw new CredentialStoreException(
            $"the macOS Keychain failed while {operation}: {Describe(status)} (OSStatus {status})");
    }

    private static string Describe(int status)
    {
        var message = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (message == IntPtr.Zero)
        {
            return "no description available";
        }

        try
        {
            return CopyString(message) ?? "no description available";
        }
        finally
        {
            CFRelease(message);
        }
    }

    // -----------------------------------------------------------------------
    // CoreFoundation interop
    // -----------------------------------------------------------------------

    /// <summary>Builds a CFDictionary and releases every value it created.</summary>
    /// <remarks>
    /// CoreFoundation is manually reference counted, and a leak here is a leak per credential
    /// operation for the life of the process. Collecting the created references in one place is
    /// what makes releasing them reliable rather than a discipline.
    /// </remarks>
    private sealed class CFDictionaryBuilder
    {
        private readonly List<IntPtr> _keys = [];
        private readonly List<IntPtr> _values = [];
        private readonly List<IntPtr> _owned = [];

        public CFDictionaryBuilder Add(IntPtr key, IntPtr value)
        {
            _keys.Add(key);
            _values.Add(value);
            return this;
        }

        public CFDictionaryBuilder Add(IntPtr key, string value)
        {
            var handle = CFStringCreate(value);
            _owned.Add(handle);
            return Add(key, handle);
        }

        public CFDictionaryBuilder Add(IntPtr key, byte[] value)
        {
            var handle = CFDataCreate(IntPtr.Zero, value, value.Length);
            _owned.Add(handle);
            return Add(key, handle);
        }

        public CFDictionaryHandle Build()
        {
            var dictionary = CFDictionaryCreate(
                IntPtr.Zero, _keys.ToArray(), _values.ToArray(), _keys.Count,
                kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks);

            return new CFDictionaryHandle(dictionary, _owned);
        }
    }

    private sealed class CFDictionaryHandle(IntPtr handle, List<IntPtr> owned) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CFRelease(Handle);
            }

            foreach (var value in owned)
            {
                if (value != IntPtr.Zero)
                {
                    CFRelease(value);
                }
            }
        }
    }

    private static IntPtr CFStringCreate(string value) =>
        CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length);

    private static string? CopyString(IntPtr handle)
    {
        var length = CFStringGetLength(handle);
        var buffer = new byte[(length * 4) + 1];
        // 0x08000100 is kCFStringEncodingUTF8.
        return CFStringGetCString(handle, buffer, buffer.Length, 0x08000100)
            ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0))
            : null;
    }

    private static string? CopyBytes(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }

        var length = (int)CFDataGetLength(data);
        var pointer = CFDataGetBytePtr(data);
        if (pointer == IntPtr.Zero || length <= 0)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>
    /// Reads a <c>CFStringRef</c>/<c>CFBooleanRef</c> constant, which is a pointer <em>variable</em>.
    /// </summary>
    /// <remarks>
    /// The exported symbol is the address of the variable, not the object, so it has to be
    /// dereferenced. Passing the export address straight through compiles, links and then
    /// crashes the process with SIGBUS the first time CoreFoundation dereferences it — which is
    /// exactly how this was found.
    /// </remarks>
    private static IntPtr PointerConstant(string library, string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(library), name));

    /// <summary>
    /// Reads the address of a constant <em>struct</em>, such as the dictionary callback tables.
    /// </summary>
    /// <remarks>
    /// The opposite case: <c>CFDictionaryCreate</c> takes a pointer to the callbacks struct, so
    /// the export address is already the value to pass and dereferencing it would be the bug.
    /// </remarks>
    private static IntPtr StructConstant(string library, string name) =>
        NativeLibrary.GetExport(NativeLibrary.Load(library), name);

    private static readonly IntPtr SecClass = PointerConstant(SecurityFramework, "kSecClass");
    private static readonly IntPtr SecClassGenericPassword = PointerConstant(SecurityFramework, "kSecClassGenericPassword");
    private static readonly IntPtr SecAttrService = PointerConstant(SecurityFramework, "kSecAttrService");
    private static readonly IntPtr SecAttrAccount = PointerConstant(SecurityFramework, "kSecAttrAccount");
    private static readonly IntPtr SecValueData = PointerConstant(SecurityFramework, "kSecValueData");
    private static readonly IntPtr SecReturnData = PointerConstant(SecurityFramework, "kSecReturnData");
    private static readonly IntPtr SecMatchLimit = PointerConstant(SecurityFramework, "kSecMatchLimit");
    private static readonly IntPtr SecMatchLimitOne = PointerConstant(SecurityFramework, "kSecMatchLimitOne");
    private static readonly IntPtr CFBooleanTrue = PointerConstant(CoreFoundation, "kCFBooleanTrue");

    private static readonly IntPtr kCFTypeDictionaryKeyCallBacks =
        StructConstant(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");

    private static readonly IntPtr kCFTypeDictionaryValueCallBacks =
        StructConstant(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityFramework)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(SecurityFramework)]
    private static partial IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr handle);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(IntPtr handle);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFStringGetCString(IntPtr handle, byte[] buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);
}
