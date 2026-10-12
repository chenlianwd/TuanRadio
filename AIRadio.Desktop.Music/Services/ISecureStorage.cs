using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public interface ISecureStorage
{
    Task SaveApiKeyAsync(string service, string apiKey);
    Task<string?> GetApiKeyAsync(string service);
    void DeleteApiKey(string service);
}
