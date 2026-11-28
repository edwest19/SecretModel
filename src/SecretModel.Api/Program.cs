// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25; the Home-mode server since
// 2026-11-27 (Task 9, README §3.1). AI-generated.
//
// Run from a PowerShell window, with the settings file and the certificate password in environment
// variables (README §3.1):
//
//   dotnet run -c Release --project src/SecretModel.Api --no-launch-profile
//
// (--no-launch-profile: the template's launch profile would set other addresses.)
//
// Settings come from appsettings.json (committed, no secrets), then the file named by
// SECRETMODEL_SETTINGS (outside the repository), then environment variables.

using SecretModel.Api.Hosting;

var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true);
string? settingsFile = Environment.GetEnvironmentVariable(ServerSettings.SettingsFileVariable);
if (!string.IsNullOrWhiteSpace(settingsFile))
{
    if (!File.Exists(settingsFile))
    {
        Console.Error.WriteLine($"The settings file named by {ServerSettings.SettingsFileVariable} was not found: {settingsFile}");
        return 1;
    }

    settings.AddJsonFile(Path.GetFullPath(settingsFile), optional: false);
}

settings.AddEnvironmentVariables();

WebApplication app;
try
{
    app = ServerHost.Create(settings.Build(), Environment.GetEnvironmentVariable(ServerSettings.PasswordVariable));
}
catch (ServerSettingsException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("SecretModel is running in Home mode on {Addresses}. Press Ctrl+C to stop.", string.Join(", ", app.Urls)));
try
{
    await app.RunAsync();
}
catch (IOException e)
{
    // Such as an address another program is using. The message names the address, nothing more.
    Console.Error.WriteLine($"The server could not start: {e.Message}");
    return 1;
}

return 0;
