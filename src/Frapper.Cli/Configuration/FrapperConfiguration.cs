using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;

namespace Frapper.Cli.Configuration;

internal sealed class FrapperConfiguration
{
    private readonly IConfiguration _configuration;

    public FrapperConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? ResolveConnectionString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        if (LooksLikeRawConnectionString(input))
            return input;

        var fromNamedConnection = _configuration.GetConnectionString(input);
        if (!string.IsNullOrWhiteSpace(fromNamedConnection))
            return fromNamedConnection;

        var fromKey = _configuration[input];
        if (!string.IsNullOrWhiteSpace(fromKey))
            return fromKey;

        return null;
    }

    public string RequireConnectionString(string? input)
    {
        var resolved = ResolveConnectionString(input);

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                "Could not resolve connection. Provide a valid connection string or configuration key.");
        }

        return resolved;
    }

    private static bool LooksLikeRawConnectionString(string value)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            return !string.IsNullOrEmpty(builder.DataSource);
        }
        catch
        {
            return false;
        }
    }
}