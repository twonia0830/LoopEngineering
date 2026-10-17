namespace LoopEngineering.Core.Copilot;

/// <summary>
/// Reads the GitHub token from the one configured environment variable. On Windows it also checks the
/// user / machine scopes so a variable set after the app started is picked up without a restart.
/// </summary>
public static class TokenResolver
{
    public static string? Resolve(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName)) return null;

        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
