using Microsoft.Playwright;

namespace PoPunkouterSoftware.Tests.E2E;

/// <summary>
/// E2EUI — C# Playwright tests driving a real browser against the running app.
/// Point at a live instance via BASE_URL (default http://localhost:8000); start it with
/// F5 or `dotnet run`. These are run locally / on demand — never in CI/CD.
/// Every test runs at BOTH mobile-portrait and desktop-landscape form factors so the two
/// renderings cannot drift apart unnoticed.
/// First run requires browsers: pwsh bin/Debug/net10.0/playwright.ps1 install
/// </summary>
public class PortfolioUiTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000";

    /// <summary>label, width, height, isMobile — the two supported form factors.</summary>
    public static TheoryData<string, int, int, bool> Viewports => new()
    {
        { "mobile", 390, 844, true },
        { "desktop", 1440, 1000, false },
    };

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        // Headless bundled Chromium by default; HEADED=1 shows the browser and
        // BROWSER_CHANNEL=chrome drives the installed Google Chrome instead.
        _browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL"),
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    private async Task<IPage> NewPageAsync(int width, int height, bool isMobile) =>
        await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
        });

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Portfolio_Home_LoadsAndShowsBrand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        (await page.TitleAsync()).Should().Contain("PoPunkouterSoftware");
        await Assertions.Expect(page.GetByText("PoPunkouterSoftware").First).ToBeVisibleAsync();
        // The in-page "Azure status" link was retired in an earlier design pass; the route to
        // Azure now lives in the top-nav (MainLayout). On mobile that nav collapses into the
        // hamburger drawer, so assert the trigger there and the link itself on desktop.
        if (isMobile)
            await Assertions.Expect(page.Locator(".app-topbar-menu")).ToBeVisibleAsync();
        else
            await Assertions.Expect(page.Locator("a.app-topbar-link[href='/azure']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".app-portfolio-card").First).ToBeVisibleAsync();
        await CaptureAsync(page, $"01-home-{label}.png");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_DefaultIsCompact_AndAdvancedDetailsAreOnDemand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        // "Service health" is the first always-present glance card heading; the hero h1 is
        // dynamic ("Everything looks good" / "N items need attention") so it is not a stable
        // anchor. "All resources" only exists once the advanced section is expanded.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Service health" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".azure-glance-grid > article")).ToHaveCountAsync(3);
        await CaptureAsync(page, $"02-azure-status-{label}.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToBeVisibleAsync();
        await CaptureAsync(page, $"03-azure-advanced-{label}.png");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_ReflowWithoutHorizontalOverflow(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);

        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"home must not overflow horizontally at {label}");
        await CaptureAsync(page, $"04-home-reflow-{label}.png");

        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"/azure must not overflow horizontally at {label}");
        await CaptureAsync(page, $"05-azure-reflow-{label}.png");
    }

    // ─── Regressions inherited from the 2026 UI pass ──────────────────────────
    // These four survived the temporary UiRedesignVerification harness because each
    // guards a defect that can recur from an ordinary CSS or markup edit. The rest of
    // that harness asserted one-time migration facts (cards no longer carry
    // backdrop-filter, the rz-sidebar column is collapsed, the topbar's pixel-level
    // spatial contract) and was retired with it.

    /// <summary>
    /// The glance charts take their height from scoped CSS. When that rule stops
    /// matching, Radzen measures a zero-height box and the charts silently collapse to
    /// an empty strip — visually "present" but drawing nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_GlanceCharts_HaveHeightAndDrawGeometry(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-glance-grid", new() { Timeout = 40_000 });
        await page.WaitForSelectorAsync(".rz-chart", new() { Timeout = 40_000 });

        var heights = await page.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.azure-glance-grid .rz-chart')].map(el => el.getBoundingClientRect().height)");
        heights.Should().NotBeEmpty();
        heights.Should().OnlyContain(h => h > 150, $"charts must not collapse at {label}");

        // A reserved box with no marks in it is still a broken chart.
        var geometryNodes = await page.EvaluateAsync<int>(
            "() => document.querySelectorAll('.azure-glance-grid .rz-chart svg path, .azure-glance-grid .rz-chart svg rect').length");
        geometryNodes.Should().BeGreaterThan(0, $"charts rendered no geometry at {label}");
    }

    /// <summary>
    /// The resource explorer has a card layout and a grid layout. Both used to render
    /// unconditionally with CSS hiding the wrong one, so every row was built twice into
    /// two live DOM trees. Exactly one must exist at any viewport.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_ResourceExplorer_RendersExactlyOneTree(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await page.WaitForSelectorAsync(".azure-resource-grid, .azure-resource-cards", new() { Timeout = 40_000 });

        var grids = await page.Locator(".azure-resource-grid").CountAsync();
        var cards = await page.Locator(".azure-resource-cards").CountAsync();

        (grids + cards).Should().Be(1, $"exactly one explorer tree must be in the DOM at {label}");
        if (isMobile)
            cards.Should().Be(1, "mobile must render the card list");
        else
            grids.Should().Be(1, "desktop must render the virtualized grid");
    }

    /// <summary>
    /// Radzen bakes component colours per theme sheet, so a control styled for one scheme
    /// can render invisible in the other (ButtonStyle.Light was white-on-white in light
    /// mode). Both schemes must clear the WCAG AA 4.5:1 floor.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Azure_AdvancedToggle_IsLegibleInBothColorSchemes(string scheme)
    {
        await using var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-advanced-toggle .rz-button", new() { Timeout = 40_000 });

        // Walk up for the first non-transparent ancestor: the button's own background is
        // transparent in the outlined variant, so comparing against it would always pass.
        var probe = await page.EvaluateAsync<string[]>(@"() => {
            const btn = document.querySelector('.azure-advanced-toggle .rz-button');
            const cs = getComputedStyle(btn);
            let bg = cs.backgroundColor, el = btn;
            while ((bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent') && el.parentElement) {
                el = el.parentElement; bg = getComputedStyle(el).backgroundColor;
            }
            return [cs.color, bg, getComputedStyle(document.documentElement).getPropertyValue('--rz-primary').trim()];
        }");

        ContrastRatio(probe[0], probe[1]).Should()
            .BeGreaterThanOrEqualTo(4.5, $"'Advanced diagnostics' is illegible in {scheme} mode");

        // --rz-primary must resolve to the app accent, not Radzen's stock #598087 teal.
        probe[2].Should().NotBeNullOrWhiteSpace("--rz-primary is unset");
        probe[2].Should().NotContain("598087", "the accent silently fell back to Radzen stock");
    }

    /// <summary>The mobile drawer must open on tap and close on Escape.</summary>
    [Fact]
    public async Task Mobile_TopbarDrawer_OpensOnTapAndClosesOnEscape()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        var trigger = page.Locator(".app-topbar-menu");
        await Assertions.Expect(trigger).ToBeVisibleAsync();
        await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

        await trigger.ClickAsync();
        await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(page.Locator("#app-topbar-drawer")).ToBeVisibleAsync();

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
    }

    /// <summary>WCAG relative-luminance contrast ratio between two computed CSS colours.</summary>
    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string css)
        {
            var parts = System.Text.RegularExpressions.Regex.Matches(css, @"\d+(\.\d+)?");
            double Channel(int i)
            {
                var c = double.Parse(parts[i].Value) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
        }

        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static async Task CaptureAsync(IPage page, string filename)
    {
        if (Environment.GetEnvironmentVariable("CAPTURE_SCREENSHOTS") != "1")
            return;

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ux-audit"));
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true });
    }
}
