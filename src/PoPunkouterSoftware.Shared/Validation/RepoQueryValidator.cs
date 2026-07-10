using FluentValidation;

namespace PoPunkouterSoftware.Shared.Validation;

/// <summary>
/// Validates a GitHub "owner/repo" identifier supplied as a query parameter.
/// Lives in Shared so the same rule can be reused by the API and the WASM client.
/// </summary>
public sealed class RepoQueryValidator : AbstractValidator<string>
{
    // owner/repo — each segment limited to GitHub's allowed name characters.
    public const string Pattern = @"^[a-zA-Z0-9_.\-]+/[a-zA-Z0-9_.\-]+$";

    public RepoQueryValidator()
    {
        RuleFor(repo => repo)
            .NotEmpty().WithMessage("repo is required.")
            .Matches(Pattern).WithMessage("Invalid repo parameter. Expected format: owner/repo");
    }
}
