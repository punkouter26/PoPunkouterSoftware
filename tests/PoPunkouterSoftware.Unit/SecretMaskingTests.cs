using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class SecretMaskingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void NullEmptyOrWhitespace_RendersNotSet(string? value)
    {
        SecretMasking.MaskValue(value).Should().Be("(not set)");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("1234")]
    [InlineData("secret!")]
    [InlineData("12345678")] // exactly 8 chars — boundary of the fully-masked bucket
    public void EightCharsOrFewer_FullyMasked(string value)
    {
        SecretMasking.MaskValue(value).Should().Be("****");
    }

    [Theory]
    [InlineData("ABCDEFGHI", "ABCD*FGHI")]        // 9 chars → single star
    [InlineData("ABCDEFGHIJ", "ABCD**GHIJ")]      // 10 chars → two stars
    [InlineData("ABCDEFGHIJKL", "ABCD****IJKL")]  // 12 chars → four stars
    public void LongerValues_KeepFirstFourAndLastFour(string value, string expected)
    {
        SecretMasking.MaskValue(value).Should().Be(expected);
    }

    [Fact]
    public void StarCount_TracksLength_UntilTheCap()
    {
        // 28 chars is the last length where stars (len - 8 = 20) still fit under the cap.
        var value = new string('x', 28);

        SecretMasking.MaskValue(value).Should().Be("xxxx" + new string('*', 20) + "xxxx");
    }

    [Fact]
    public void VeryLongValue_StarsAreCappedAtTwenty()
    {
        var value = "HEAD" + new string('m', 992) + "TAIL"; // 1000 chars

        var masked = SecretMasking.MaskValue(value);

        masked.Should().Be("HEAD" + new string('*', 20) + "TAIL",
            because: "the star run is capped at 20 regardless of input length");
        masked.Length.Should().Be(28);
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

