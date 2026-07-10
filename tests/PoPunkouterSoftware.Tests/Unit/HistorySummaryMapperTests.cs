using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Tests.Unit;

public class HistorySummaryMapperTests
{
    [Fact]
    public void EmptyReport_ProducesNullSafeDefaults()
    {
        var summary = HistorySummaryMapper.FromReport(new AzureReport());

        summary.GeneratedAt.Should().Be(DateTime.MinValue);
        summary.TotalServices.Should().Be(0);
        summary.ActiveServices.Should().Be(0);
        summary.BrokenServices.Should().Be(0);
        summary.TotalCost30Days.Should().Be(0);
        summary.ProjectedMonthCost.Should().Be(0);
        summary.AvgResponseTimeMs.Should().Be(0);
        summary.Total5xxErrors.Should().Be(0);
        summary.TotalResources.Should().Be(0);
        summary.ScanDurationMs.Should().Be(0);
        summary.BrokenDelta.Should().BeNull();
        summary.Services.Should().BeEmpty();
    }

    [Fact]
    public void HeaderFields_PassThroughFromReport()
    {
        var generatedAt = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc);
        var report = new AzureReport
        {
            GeneratedAt = generatedAt,
            WebServices = new WebServicesInfo
            {
                Total = 7,
                ByStatus = new ByStatusInfo { Active = 5, Broken = 2 },
            },
            Cost = new CostInfo { TotalCost30Days = 12.34 },
            BurnRate = new BurnRateInfo { ProjectedMonthTotal = 56.78 },
            AllResourceSummary = new AllResourceSummaryInfo { Total = 42 },
            StepTimings = new List<StepTimingEntry>
            {
                new() { Step = "connectivity", ElapsedMs = 1_000 },
                new() { Step = "cost", ElapsedMs = 250 },
            },
        };

        var summary = HistorySummaryMapper.FromReport(report);

        summary.GeneratedAt.Should().Be(generatedAt);
        summary.TotalServices.Should().Be(7);
        summary.ActiveServices.Should().Be(5);
        summary.BrokenServices.Should().Be(2);
        summary.TotalCost30Days.Should().Be(12.34);
        summary.ProjectedMonthCost.Should().Be(56.78);
        summary.TotalResources.Should().Be(42);
        summary.ScanDurationMs.Should().Be(1_250, because: "scan duration is the sum of all step timings");
    }

    [Fact]
    public void AvgResponseTime_AveragesOnlySuccessfulConnectivityProbes()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new() { Name = "fast", Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 100 } },
                    new() { Name = "slow", Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 300 } },
                    // Failed probe carries a bogus response time that must NOT skew the average.
                    new() { Name = "down", Connectivity = new ConnectivityInfo { Success = false, ResponseTime = 9_999 } },
                    new() { Name = "unprobed", Connectivity = null },
                },
            },
        };

        HistorySummaryMapper.FromReport(report).AvgResponseTimeMs.Should().Be(200);
    }

    [Fact]
    public void AvgResponseTime_NoSuccessfulProbes_IsZeroNotNaN()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new() { Name = "down", Connectivity = new ConnectivityInfo { Success = false, ResponseTime = 500 } },
                },
            },
        };

        HistorySummaryMapper.FromReport(report).AvgResponseTimeMs.Should().Be(0);
    }

    [Fact]
    public void Total5xx_SumsAcrossServices_MissingMetricsCountAsZero()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new() { Name = "a", Metrics7Days = new MetricsInfo { Http5xx = 2 } },
                    new() { Name = "b", Metrics7Days = new MetricsInfo { Http5xx = 3 } },
                    new() { Name = "c", Metrics7Days = null },
                },
            },
        };

        HistorySummaryMapper.FromReport(report).Total5xxErrors.Should().Be(5);
    }

    [Fact]
    public void Services_ProjectToHistoryPoints_PreferringFriendlyName()
    {
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new()
                    {
                        Name = "app-po-thing",
                        FriendlyName = "PoThing",
                        HttpStatus = "active",
                        Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 87 },
                        Metrics7Days = new MetricsInfo { Requests = 1_500 },
                    },
                },
            },
        };

        var point = HistorySummaryMapper.FromReport(report).Services.Should().ContainSingle().Subject;
        point.Name.Should().Be("PoThing");
        point.HttpStatus.Should().Be("active");
        point.ResponseTimeMs.Should().Be(87);
        point.Requests7d.Should().Be(1_500);
    }

    [Fact]
    public void Services_NullFriendlyName_FallsBackToRawName()
    {
        // A report deserialized from JSON with "friendlyName": null hits the ?? fallback.
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService> { new() { Name = "app-raw-name", FriendlyName = null! } },
            },
        };

        HistorySummaryMapper.FromReport(report).Services.Single().Name.Should().Be("app-raw-name");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(3)]
    public void BrokenDelta_PassesThroughVerbatim(int delta)
    {
        var report = new AzureReport { Delta = new ReportDelta { BrokenServicesDelta = delta } };

        HistorySummaryMapper.FromReport(report).BrokenDelta.Should().Be(delta);
    }

    [Fact]
    public void BrokenDelta_NullOnDelta_StaysNull()
    {
        var report = new AzureReport { Delta = new ReportDelta { BrokenServicesDelta = null } };

        HistorySummaryMapper.FromReport(report).BrokenDelta.Should().BeNull();
    }
}
