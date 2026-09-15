using Ipc.Core.System;

namespace Ipc.Services.Security;

public sealed class PasswordPolicy
{
    private readonly SystemValueRegistry _registry;

    public PasswordPolicy(SystemValueRegistry registry)
    {
        _registry = registry;
    }

    public int MaximumLength => _registry.Get(SystemValueNames.PasswordSystemLevel).Value is "0" or "1" ? 10 : 128;

    public int MinimumLength => ParseInt(_registry.Get(SystemValueNames.PasswordMinimumLength).Value, 6);

    public bool RequiresDigit =>
        ParseYesNo(_registry.Get(SystemValueNames.PasswordRequiredDigit).Value, defaultValue: true);

    public bool RepeatedCharactersNotAllowed =>
        ParseYesNo(_registry.Get(SystemValueNames.PasswordRepeatedCharacters).Value, defaultValue: false);

    public IReadOnlyList<string> Validate(string password)
    {
        var issues = new List<string>();
        if (password.Any(char.IsControl)) issues.Add("Password cannot contain control characters.");

        if (password.Length < MinimumLength)
        {
            issues.Add($"Password must be at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            issues.Add($"Password cannot be more than {MaximumLength} characters.");
        }

        if (RequiresDigit && !password.Any(char.IsDigit))
        {
            issues.Add("Password must contain a digit.");
        }

        if (RepeatedCharactersNotAllowed && password.Distinct().Count() != password.Length)
        {
            issues.Add("Password cannot contain repeated characters.");
        }

        return issues;
    }

    public bool IsValid(string password) => Validate(password).Count == 0;

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool ParseYesNo(string value, bool defaultValue)
    {
        if (string.Equals(value, "*YES", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "*NO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}
