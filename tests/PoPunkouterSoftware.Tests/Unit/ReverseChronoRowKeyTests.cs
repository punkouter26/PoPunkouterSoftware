using PoPunkouterSoftware.Infrastructure.Azure;

namespace PoPunkouterSoftware.Tests.Unit;

public class ReverseChronoRowKeyTests
{
    [Fact]
    public void NewerTimestamp_SortsLexicallyBeforeOlder()
    {
        var older = new ReverseChronoRowKey(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString();
        var newer = new ReverseChronoRowKey(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)).ToString();

        // Table Storage returns rows in ascending lexical RowKey order, so the newer
        // report must produce the ordinally smaller key to come back first.
        string.CompareOrdinal(newer, older).Should().BeNegative();
    }

    [Fact]
    public void AscendingLexicalSort_YieldsNewestFirst()
    {
        var timestamps = new[]
        {
            new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero),
        };

        var keys = timestamps.Select(t => (Key: new ReverseChronoRowKey(t).ToString(), At: t))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        keys.Select(x => x.At).Should().BeInDescendingOrder();
    }

    [Theory]
    [InlineData(2026, 7, 9)]
    [InlineData(1, 1, 1)]        // DateTimeOffset.MinValue date — largest inverse value
    [InlineData(9999, 12, 31)]   // near MaxValue — smallest inverse value
    public void RowKey_IsAlwaysTwentyDigits(int year, int month, int day)
    {
        var key = new ReverseChronoRowKey(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero)).ToString();

        key.Should().HaveLength(20);
        key.Should().MatchRegex("^[0-9]{20}$");
    }

    [Fact]
    public void MaxValueTimestamp_ProducesAllZeros()
    {
        new ReverseChronoRowKey(DateTimeOffset.MaxValue).ToString().Should().Be(new string('0', 20));
    }

    [Fact]
    public void SameTimestamp_ProducesIdenticalKey()
    {
        var at = new DateTimeOffset(2026, 7, 9, 15, 30, 0, TimeSpan.Zero);

        new ReverseChronoRowKey(at).ToString().Should().Be(new ReverseChronoRowKey(at).ToString());
    }

    [Fact]
    public void OneTickApart_StillProducesDistinctOrderedKeys()
    {
        var at = new DateTimeOffset(2026, 7, 9, 15, 30, 0, TimeSpan.Zero);
        var older = new ReverseChronoRowKey(at).ToString();
        var newer = new ReverseChronoRowKey(at.AddTicks(1)).ToString();

        newer.Should().NotBe(older);
        string.CompareOrdinal(newer, older).Should().BeNegative();
    }

    [Fact]
    public void WithSuffix_AppendsHyphenThenSuffixToTheBaseKey()
    {
        var key = new ReverseChronoRowKey(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero));

        key.WithSuffix("svc-a").Should().Be($"{key}-svc-a");
    }

    [Fact]
    public void WithSuffix_SameTimestampDifferentSuffixes_AreDistinctRows()
    {
        // Same-tick collisions are the common case for incidents; the suffix is what
        // prevents an Upsert from silently keeping only the last row.
        var key = new ReverseChronoRowKey(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero));

        key.WithSuffix("svc-a").Should().NotBe(key.WithSuffix("svc-b"));
        key.WithSuffix("svc-a").Should().StartWith(key.ToString());
    }
}
