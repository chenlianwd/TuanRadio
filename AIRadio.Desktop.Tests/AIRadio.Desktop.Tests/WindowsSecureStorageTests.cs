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
}
