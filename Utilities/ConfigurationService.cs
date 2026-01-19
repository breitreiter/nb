using Microsoft.Extensions.Configuration;

namespace nb.Utilities;

public class ConfigurationService
{
    private readonly IConfiguration _configuration;
    private readonly string _systemPrompt;

    public ConfigurationService()
    {
        _configuration = LoadConfiguration();
        _systemPrompt = LoadSystemPrompt();
        SetupConsoleEncoding();
    }

    public IConfiguration GetConfiguration() => _configuration;
    public string GetSystemPrompt() => _systemPrompt;

    public void SetupConsoleEncoding()
    {
        // Set UTF-8 code page for proper Unicode support on Windows
        try
        {
            SetConsoleCP(65001);
            SetConsoleOutputCP(65001);
        }
        catch
        {
            // Ignore errors on non-Windows platforms
        }
        
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    private static IConfiguration LoadConfiguration()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        return config;
    }

    private static string LoadSystemPrompt()
    {
        try
        {
            var systemPromptPath = Path.Combine(AppContext.BaseDirectory, "system.md");
            
            if (File.Exists(systemPromptPath))
            {
                return File.ReadAllText(systemPromptPath);
            }
            else
            {
                Console.WriteLine("Warning: system.md file not found. Using default system prompt.");
                return "You are a helpful AI assistant.";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading system prompt: {ex.Message}");
            return "You are a helpful AI assistant.";
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);
    
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);
}