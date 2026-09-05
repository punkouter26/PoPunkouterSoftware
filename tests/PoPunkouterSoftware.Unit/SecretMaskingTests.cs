using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class SecretMaskingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t\r\n")]
    public void NullEmptyOrWhitespace_RendersNotSet(string? value)
    {
        SecretMasking.MaskValue(value).Should().Be("(not set)");
    }

    [Theory]
    [InlineData("12345678")] // exactly 8 chars — boundary of the fully-masked bucket
    public void EightCharsOrFewer_FullyMasked(string value)
    {
        SecretMasking.MaskValue(value).Should().Be("****");
    }

    [Theory]
    [InlineData("ABCDEFGHI", "ABCD*FGHI")]        // 9 chars → single star
    [InlineData("ABCDEFGHIJKL", "ABCD****IJKL")]  // 12 chars → four stars
    public void LongerValues_KeepFirstFourAndLastFour(string value, string expected)
    {
        SecretMasking.MaskValue(value).Should().Be(expected);
    }

    /// <summary>Both sides of the star cap — the last length under it and one far past it.</summary>
    [Fact]
    public void StarCount_TracksLength_AndIsCappedAtTwenty()
    {
        // 28 chars is the last length where stars (len - 8 = 20) still fit under the cap.
        SecretMasking.MaskValue(new string('x', 28))
            .Should().Be("xxxx" + new string('*', 20) + "xxxx");

        var masked = SecretMasking.MaskValue("HEAD" + new string('m', 992) + "TAIL"); // 1000 chars

        masked.Should().Be("HEAD" + new string('*', 20) + "TAIL",
            because: "the star run is capped at 20 regardless of input length");
        masked.Length.Should().Be(28);
    }

    /// <summary>
    /// The mask applied to /api/diag/report, which is anonymous and returns every resource id
    /// in the estate. The subscription GUID goes; the rest of the id stays, because the panel
    /// renders resource group and name and those are already public.
    /// </summary>
    [Fact]
    public void MaskSubscriptionIds_RemovesEveryGuid_AndKeepsTheRestOfTheId()
    {
        const string json = """
            {"a":"/subscriptions/bbb8dfbe-9169-432f-9b7a-fbf861b51037/resourceGroups/PoMode/providers/Microsoft.Web/sites/app-pomode",
             "b":"/SUBSCRIPTIONS/BBB8DFBE-9169-432F-9B7A-FBF861B51037/resourceGroups/PoWatch/providers/Microsoft.Storage/storageAccounts/stpo"}
            """;

        var masked = SecretMasking.MaskSubscriptionIds(json);

        masked.Should().NotContain("bbb8dfbe", "the id must go regardless of casing");
        masked.Should().NotContain("BBB8DFBE");
        masked.Should().Contain("/subscriptions/****/resourceGroups/PoMode/providers/Microsoft.Web/sites/app-pomode");
        masked.Should().Contain("/resourceGroups/PoWatch/providers/Microsoft.Storage/storageAccounts/stpo");

        // ...and everything that is not an ARM id passes through untouched, so the mask can
        // be applied to a whole serialized report without corrupting the rest of it.
        SecretMasking.MaskSubscriptionIds("""{"note":"no ids here","n":42}""")
            .Should().Be("""{"note":"no ids here","n":42}""");
        SecretMasking.MaskSubscriptionIds("").Should().Be("");
    }

    [Fact]
    public void MaskedValue_NeverExposesTheMiddleOfTheSecret()
    {
        var masked = SecretMasking.MaskValue("AccountKey=SuperSecretValue123");

        masked.Should().NotContain("SuperSecret");
        masked.Should().StartWith("Acco");
        masked.Should().EndWith("e123");
    }
}

