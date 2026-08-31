using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WindowsSecureStorageTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var storage = new WindowsSecureStorage();
        var service = $"AIRadio.Test.{System.Guid.NewGuid():N}";

        try
        {
            await storage.SaveApiKeyAsync(service, "test-value");
            var loaded = await storage.GetApiKeyAsync(service);
            Assert.Equal("test-value", loaded);
        }
        finally
        {
            storage.DeleteApiKey(service);
        }
    }

    [Fact]
    public async Task GetApiKey_NonexistentKey_ReturnsNull()
    {
        var storage = new WindowsSecureStorage();
        var result = await storage.GetApiKeyAsync("AIRadio.Test.Nonexistent.Key");
        Assert.Null(result);
    }

    [Fact]
    public void DeleteApiKey_NonexistentKey_DoesNotThrow()
    {
        var storage = new WindowsSecureStorage();
        var ex = Record.Exception(() => storage.DeleteApiKey("AIRadio.Test.Nonexistent.Key"));
        Assert.Null(ex);
    }

    // 回归测试：Persist=1 是 CRED_PERSIST_SESSION（注销/重启即丢），
    // 曾导致 API Key 与音源登录态每次重启后"莫名"清空；必须以 LOCAL_MACHINE 持久化
    [Fact]
    public async Task SaveApiKey_PersistsForFutureLogonSessions()
    {
        var storage = new WindowsSecureStorage();
        var service = $"AIRadio.Test.{Guid.NewGuid():N}";

        try
        {
            await storage.SaveApiKeyAsync(service, "test-value");
            var target = "AIRadio:" + service;
            Assert.Equal(2u, NativeCredReadPersist(target)); // CRED_PERSIST_LOCAL_MACHINE
        }
        finally
        {
            storage.DeleteApiKey(service);
        }
    }

    private static uint NativeCredReadPersist(string target)
    {
        if (!NativeCred.CredRead(target, 1 /* GENERIC */, 0, out var ptr))
            throw new InvalidOperationException($"CredRead failed: {Marshal.GetLastWin32Error()}");
        try
        {
            return Marshal.PtrToStructure<NativeCred.CREDENTIAL>(ptr).Persist;
        }
        finally
        {
            NativeCred.CredFree(ptr);
        }
    }

    private static class NativeCred
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }
    }
}
