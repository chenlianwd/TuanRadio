using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public class WindowsSecureStorage : ISecureStorage
{
    private const string Prefix = "AIRadio:";

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredRead(string target, CredentialType type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDelete(string target, CredentialType type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private enum CredentialType
    {
        Generic = 1
    }

    public Task SaveApiKeyAsync(string service, string apiKey)
    {
        var targetName = Prefix + service;
        var blobPtr = Marshal.StringToCoTaskMemUni(apiKey);
        var credential = new CREDENTIAL
        {
            TargetName = targetName,
            UserName = service,
            Type = CredentialType.Generic,
            Persist = 2, // LOCAL_MACHINE
            CredentialBlob = blobPtr,
            CredentialBlobSize = (uint)(Encoding.Unicode.GetByteCount(apiKey))
        };

        try
        {
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"Failed to save credential. Error: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blobPtr);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetApiKeyAsync(string service)
    {
        var targetName = Prefix + service;
        if (CredRead(targetName, CredentialType.Generic, 0, out var credPtr))
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2));
            CredFree(credPtr);
            return Task.FromResult<string?>(password);
        }

        return Task.FromResult<string?>(null);
    }

    public void DeleteApiKey(string service)
    {
        var targetName = Prefix + service;
        CredDelete(targetName, CredentialType.Generic, 0);
    }
}
