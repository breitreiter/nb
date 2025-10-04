using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

public interface IChatClientProvider
{
    string Name { get; }
    string[] RequiredConfigKeys { get; }
    IChatClient CreateClient(IConfiguration config);
    bool CanCreate(IConfiguration config);
}
