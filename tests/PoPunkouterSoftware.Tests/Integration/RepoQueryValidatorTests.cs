using PoPunkouterSoftware.Shared.Validation;

namespace PoPunkouterSoftware.Tests.Integration;

/// <summary>
/// Direct FluentValidation tests for the shared "owner/repo" query rule. The validator has
/// exactly two rules — NotEmpty and the owner/repo pattern — with no explicit length limit,
/// so length abuse is bounded elsewhere (HybridCache MaximumKeyLength in Program.cs).
/// </summary>
public class RepoQueryValidatorTests
{
    private readonly RepoQueryValidator _validator = new();

    // ── NotEmpty rule ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \r\n ")]
    public void EmptyOrWhitespace_IsInvalid_WithRequiredMessage(string repo)
    {
        var result = _validator.Validate(repo);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "repo is required.");
    }

    // ── Pattern rule: missing slash ───────────────────────────────────────────

    [Theory]
    [InlineData("norepo")]
    [InlineData("owner")]
    [InlineData("owner-repo")]
    [InlineData("owner.repo")]
    public void MissingSlash_IsInvalid(string repo)
    {
        var result = _validator.Validate(repo);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == "Invalid repo parameter. Expected format: owner/repo");
    }

    // ── Pattern rule: invalid characters / malformed segments ────────────────

    [Theory]
    [InlineData("has spaces/repo")]
    [InlineData("owner/has spaces")]
    [InlineData("own$er/repo")]
    [InlineData("owner/repo!")]
    [InlineData("<script>/xss")]
    [InlineData("owner/re;po")]
    [InlineData("a/b/c")]              // more than one slash
    [InlineData("../../traversal")]    // path traversal shape
    [InlineData("/repo")]              // empty owner segment
    [InlineData("owner/")]             // empty repo segment
    [InlineData("/")]
    public void InvalidCharactersOrShape_IsInvalid(string repo)
    {
        _validator.Validate(repo).IsValid.Should().BeFalse();
    }

    // ── Valid inputs ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("a/b")]
    [InlineData("my-org/My.Repo-123")]
    [InlineData("punkouter26/PoPunkouterSoftware")]
    [InlineData("A_a.9-x/B_b.8-y")]    // full allowed character set in both segments
    [InlineData("1234/5678")]          // digits-only segments are legal GitHub names
    [InlineData("dot.owner/dot.repo")]
    public void ValidOwnerRepo_IsValid(string repo)
    {
        var result = _validator.Validate(repo);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void VeryLongButWellFormedInput_IsValid_NoLengthRuleExists()
    {
        // Documents current behavior: the validator itself imposes no length cap.
        var repo = new string('a', 300) + "/" + new string('b', 300);

        _validator.Validate(repo).IsValid.Should().BeTrue();
    }
}
