using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Locara.Core;

namespace Locara.Desktop;

internal static class ProviderCredentials
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;

    private static string IdentityFor(ProviderOptions options, string prefix = "Locara")
    {
        var endpoint = ApiProvider.ResolveBaseAddress(options);
        string connection = options.Provider + "\n" + (options.BaseUrl ?? endpoint.GetLeftPart(UriPartial.Authority));
        return $"{prefix}/Provider/" + Identity.Hash(Encoding.UTF8.GetBytes(connection));
    }

    private static string IdentityForProfile(string profileId, string prefix = "Locara") =>
        $"{prefix}/Profile/" + Identity.Hash(Encoding.UTF8.GetBytes(profileId));

    private static string ReadTarget(string target)
    {
        if (!CredRead(target, Generic, 0, out nint pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return "";
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Secret, checked((int)credential.Size / 2)) ?? "";
        }
        finally { CredFree(pointer); }
    }

    internal static string Read(ProviderOptions options, string? profileId = null)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            string scoped = ReadTarget(IdentityForProfile(profileId, "Locara"));
            if (scoped.Length > 0) return scoped;
            string legacyScoped = ReadTarget(IdentityForProfile(profileId, "Locara2"));
            if (legacyScoped.Length > 0) return legacyScoped;
        }
        string primary = ReadTarget(IdentityFor(options, "Locara"));
        return primary.Length > 0 ? primary : ReadTarget(IdentityFor(options, "Locara2"));
    }

    internal static void Write(ProviderOptions options, string? profileId = null)
    {
        if (options.ApiKey.Length == 0 || options.ApiKey.Length > 1280)
            throw new ArgumentException("أدخل مفتاحاً صالحاً للحفظ في Windows.");
        string target = !string.IsNullOrWhiteSpace(profileId) ? IdentityForProfile(profileId) : IdentityFor(options);
        nint pointer = Marshal.StringToCoTaskMemUni(options.ApiKey);
        try
        {
            var credential = new Credential
            {
                Type = Generic, Target = target, Size = checked((uint)options.ApiKey.Length * 2),
                Secret = pointer, Persist = LocalMachine, UserName = "Locara"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    private static void DeleteTarget(string target)
    {
        if (!CredDelete(target, Generic, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error);
        }
    }

    internal static void Delete(ProviderOptions options, string? profileId = null, bool deleteLegacy = true)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            DeleteTarget(IdentityForProfile(profileId, "Locara"));
            DeleteTarget(IdentityForProfile(profileId, "Locara2"));
        }
        if (deleteLegacy || string.IsNullOrWhiteSpace(profileId))
        {
            DeleteTarget(IdentityFor(options, "Locara"));
            DeleteTarget(IdentityFor(options, "Locara2"));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target;
        public nint Comment;
        public FILETIME LastWritten;
        public uint Size;
        public nint Secret;
        public uint Persist, AttributeCount;
        public nint Attributes, Alias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
