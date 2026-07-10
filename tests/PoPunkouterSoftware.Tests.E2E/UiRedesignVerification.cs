using Microsoft.Playwright;

namespace PoPunkouterSoftware.Tests.E2E;

/// <summary>
/// TEMPORARY verification harness for the 2026 UI/UX pass. Drives a live host, asserts the
/// specific regressions the redesign could have introduced (chart collapse, canvas absent,
/// duplicated explorer DOM, broken topbar contract), and writes screenshots to ARTIFACT_DIR.
/// </summary>
public class UiRedesignVerification : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000";
    private static readonly string ArtifactDir =
        Environment.GetEnvironmentVariable("ARTIFACT_DIR") ?? Path.GetTempPath();

    private IPlaywright _pw = default!;
    private IBrowser _browser = default!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        // Force a usable WebGL implementation in headless: without these flags Chromium
        // exposes no GPU and the backdrop's failIfMajorPerformanceCaveat context returns
        // null, so the shader path would never be exercised.
        _browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = new[]
            {
                "--use-gl=angle",
                "--use-angle=swiftshader",
                "--ignore-gpu-blocklist",
                "--enable-unsafe-swiftshader",
            },
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _pw.Dispose();
    }

    private async Task<IPage> OpenAsync(int width, int height, string path)
    {
        var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            DeviceScaleFactor = 2,
        });
        var page = await ctx.NewPageAsync();
        page.Console += (_, m) =>
        {
            if (m.Type == "error") Console.WriteLine($"[console.error] {m.Text}");
        };
        page.PageError += (_, e) => Console.WriteLine($"[pageerror] {e}");

        await page.GotoAsync(BaseUrl + path, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-topbar", new() { Timeout = 30_000 });
        return page;
    }

    [Fact]
    public async Task Desktop_portfolio_renders_with_contract_and_gpu_layer()
    {
        var page = await OpenAsync(1440, 900, "/");
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        // GPU backdrop initialised (WebGL available in headless chromium via swiftshader).
        var canvasCount = await page.Locator("#app-gpu-backdrop").CountAsync();
        Assert.Equal(1, canvasCount);

        // Spatial contract: brand left, nav centred, session right.
        var brand = await page.Locator(".app-topbar-brand").BoundingBoxAsync();
        var nav = await page.Locator("#app-topbar-drawer").BoundingBoxAsync();
        var session = await page.Locator(".app-topbar-session").BoundingBoxAsync();
        var bar = await page.Locator(".app-topbar").BoundingBoxAsync();
        Assert.NotNull(brand); Assert.NotNull(nav); Assert.NotNull(session); Assert.NotNull(bar);

        var navCentre = nav!.X + nav.Width / 2;
        var barCentre = bar!.X + bar.Width / 2;
        Console.WriteLine($"nav centre={navCentre:F1} bar centre={barCentre:F1} delta={Math.Abs(navCentre - barCentre):F1}");
        Assert.True(Math.Abs(navCentre - barCentre) < 24, $"nav is not centred (delta {Math.Abs(navCentre - barCentre):F1}px)");
        Assert.True(brand!.X < navCentre, "brand must sit left of the nav");
        Assert.True(session!.X > navCentre, "session must sit right of the nav");

        // No card carries a backdrop-filter any more.
        var blurred = await page.EvaluateAsync<int>(
            "() => [...document.querySelectorAll('.app-card')].filter(el => getComputedStyle(el).backdropFilter !== 'none').length");
        Assert.Equal(0, blurred);

        // Regression: Radzen's .rz-layout defaults to a 2-column grid with a rz-sidebar
        // area. If the area map is not collapsed alongside the column track, the page is
        // pushed into a phantom right-hand column and the card grid degrades to 1 column.
        var pageBox = await page.Locator("main.app-page").BoundingBoxAsync();
        Assert.NotNull(pageBox);
        Console.WriteLine($"main.app-page x={pageBox!.X:F0} width={pageBox.Width:F0}");
        Assert.True(pageBox.X < 200, $"page is offset to x={pageBox.X:F0} — rz-sidebar column leaked back in");
        Assert.True(pageBox.Width > 1000, $"page width {pageBox.Width:F0} — layout column collapsed");

        var gridColumns = await page.EvaluateAsync<int>(
            "() => getComputedStyle(document.querySelector('.app-grid.cards')).gridTemplateColumns.split(' ').length");
        Console.WriteLine($"card grid columns at 1440px: {gridColumns}");
        Assert.True(gridColumns >= 3, $"card grid collapsed to {gridColumns} column(s)");

        await page.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "desktop-portfolio.png"), FullPage = false });
    }

    /// <summary>
    /// `.rz-color-base` forces `color: var(--rz-base) !important` (#505f65) onto &lt;body&gt;,
    /// so any heading without its own colour inherited mid-slate grey regardless of theme.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Headings_inherit_the_theme_text_colour(string scheme)
    {
        var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(BaseUrl + "/", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync("h1", new() { Timeout = 30_000 });

        var probe = await page.EvaluateAsync<string[]>(@"() => {
            const h1 = document.querySelector('main h1');
            return [getComputedStyle(h1).color, getComputedStyle(document.body).backgroundColor];
        }");
        Console.WriteLine($"[{scheme}] h1 color={probe[0]} body bg={probe[1]}");

        static double Luma(string css)
        {
            var n = System.Text.RegularExpressions.Regex.Matches(css, @"\d+(\.\d+)?");
            double C(int i)
            {
                var c = double.Parse(n[i].Value) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
        }

        var l1 = Luma(probe[0]);
        var l2 = Luma(probe[1]);
        var contrast = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        Console.WriteLine($"[{scheme}] h1 contrast ratio vs body: {contrast:F2}:1");

        // Large text (h1 is >= 24px bold) needs 3:1 for WCAG AA; the old #505f65 on the
        // light background scored ~1.9:1. Assert comfortably above the large-text floor.
        Assert.True(contrast >= 4.5, $"h1 contrast is only {contrast:F2}:1 in {scheme} mode");
        await ctx.CloseAsync();
    }

    [Fact]
    public async Task Gpu_backdrop_shader_compiles_when_webgl_is_available()
    {
        var page = await OpenAsync(1440, 900, "/");

        // The backdrop resolves to a terminal status; wait for it rather than a fixed sleep.
        await page.WaitForFunctionAsync(
            "() => { const c = document.getElementById('app-gpu-backdrop'); return c && c.dataset.gpu; }",
            null, new PageWaitForFunctionOptions { Timeout = 15_000 });

        var status = await page.EvaluateAsync<string>(
            "() => document.getElementById('app-gpu-backdrop').dataset.gpu");
        Console.WriteLine($"gpu backdrop status: {status}");

        // With SwiftShader forced on, the context must be created and the shader must build.
        // 'ready' is the success path; anything containing 'error' is a real defect. (If the
        // CI runner still exposes no GL at all we tolerate 'no-webgl' — that is the graceful
        // fallback, not a shader bug.)
        Assert.DoesNotContain("error", status ?? "");
        Assert.True(status is "ready" or "no-webgl", $"unexpected backdrop status '{status}'");

        if (status == "ready")
        {
            // The CSS fallback grid must yield to the canvas.
            var hasAttr = await page.EvaluateAsync<bool>(
                "() => document.documentElement.hasAttribute('data-gpu-backdrop')");
            Assert.True(hasAttr, "data-gpu-backdrop not set despite ready status");
        }
    }

    [Fact]
    public async Task Mobile_portfolio_drawer_opens_and_closes()
    {
        var page = await OpenAsync(390, 844, "/");
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });
        await page.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "mobile-portfolio.png") });

        var trigger = page.Locator(".app-topbar-menu");
        Assert.True(await trigger.IsVisibleAsync(), "hamburger must be visible at 390px");
        Assert.Equal("false", await trigger.GetAttributeAsync("aria-expanded"));

        await trigger.ClickAsync();
        await page.WaitForTimeoutAsync(300);
        Assert.Equal("true", await trigger.GetAttributeAsync("aria-expanded"));
        Assert.True(await page.Locator("#app-topbar-drawer").IsVisibleAsync(), "drawer must open");
        await page.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "mobile-drawer-open.png") });

        // Escape closes it — behaviour the old inline onclick had no room for.
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(300);
        Assert.Equal("false", await trigger.GetAttributeAsync("aria-expanded"));
    }

    [Fact]
    public async Task Azure_charts_have_non_zero_height_after_inline_style_removal()
    {
        var page = await OpenAsync(1440, 1000, "/azure");
        await page.WaitForSelectorAsync(".azure-glance-grid", new() { Timeout = 40_000 });
        await page.WaitForSelectorAsync(".rz-chart", new() { Timeout = 40_000 });
        await page.WaitForTimeoutAsync(1200); // let Radzen measure + draw

        var heights = await page.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.azure-glance-grid .rz-chart')].map(el => el.getBoundingClientRect().height)");
        Console.WriteLine("glance chart heights: " + string.Join(", ", heights));
        Assert.NotEmpty(heights);
        Assert.All(heights, h => Assert.True(h > 150, $"chart collapsed to {h}px — CSS height did not apply"));

        // Charts must draw actual geometry, not just reserve a box.
        var svgPaths = await page.EvaluateAsync<int>(
            "() => document.querySelectorAll('.azure-glance-grid .rz-chart svg path, .azure-glance-grid .rz-chart svg rect').length");
        Console.WriteLine($"chart svg geometry nodes: {svgPaths}");
        Assert.True(svgPaths > 0, "charts rendered no geometry");

        // Chart must not draw its own border (double-border regression).
        var chartBorder = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('.azure-glance-grid .rz-chart')).borderTopWidth");
        Assert.Equal("0px", chartBorder);

        await page.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "desktop-azure.png"), FullPage = false });
    }

    /// <summary>
    /// Radzen's component colours are baked per theme sheet. With only software-dark.css
    /// loaded, `ButtonStyle.Light` rendered white text on a white surface in light mode.
    /// Also pins the primary accent, which had silently stayed Radzen's stock teal because
    /// the old bridge overrode `--rz-primary-color` (not a real variable) instead of `--rz-primary`.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task Radzen_buttons_are_legible_and_on_brand(string scheme)
    {
        var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(BaseUrl + "/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-advanced-toggle .rz-button", new() { Timeout = 40_000 });

        var probe = await page.EvaluateAsync<string[]>(@"() => {
            const btn = document.querySelector('.azure-advanced-toggle .rz-button');
            const cs = getComputedStyle(btn);
            let bg = cs.backgroundColor, el = btn;
            while ((bg === 'rgba(0, 0, 0, 0)' || bg === 'transparent') && el.parentElement) {
                el = el.parentElement; bg = getComputedStyle(el).backgroundColor;
            }
            const primary = getComputedStyle(document.documentElement).getPropertyValue('--rz-primary').trim();
            return [cs.color, bg, primary];
        }");
        Console.WriteLine($"[{scheme}] advanced-toggle color={probe[0]} effective bg={probe[1]} --rz-primary={probe[2]}");

        static double Luma(string css)
        {
            var n = System.Text.RegularExpressions.Regex.Matches(css, @"\d+(\.\d+)?");
            double C(int i)
            {
                var c = double.Parse(n[i].Value) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
        }

        var l1 = Luma(probe[0]);
        var l2 = Luma(probe[1]);
        var contrast = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        Console.WriteLine($"[{scheme}] advanced-toggle contrast: {contrast:F2}:1");
        Assert.True(contrast >= 4.5, $"'Advanced diagnostics' button contrast is {contrast:F2}:1 in {scheme} mode");

        // --rz-primary must resolve to the app accent, not Radzen's stock #598087 teal.
        Assert.DoesNotContain("598087", probe[2]);
        Assert.False(string.IsNullOrWhiteSpace(probe[2]), "--rz-primary is unset");

        await ctx.CloseAsync();
    }

    [Fact]
    public async Task Resource_explorer_renders_exactly_one_tree_per_viewport()
    {
        // Desktop: grid present, card list absent from the DOM entirely.
        var desktop = await OpenAsync(1440, 1000, "/azure");
        await desktop.GetByText("Advanced diagnostics").ClickAsync();
        await desktop.WaitForSelectorAsync(".azure-resource-grid", new() { Timeout = 40_000 });
        await desktop.WaitForTimeoutAsync(800);

        Assert.Equal(1, await desktop.Locator(".azure-resource-grid").CountAsync());
        Assert.Equal(0, await desktop.Locator(".azure-resource-cards").CountAsync());

        var gridHeight = await desktop.EvaluateAsync<double>(
            "() => document.querySelector('.azure-resource-grid .rz-data-grid, .azure-resource-grid .rz-datatable').getBoundingClientRect().height");
        Console.WriteLine($"virtualized grid height: {gridHeight}");
        Assert.True(gridHeight > 400 && gridHeight < 700, $"grid height {gridHeight} — virtualization container not bounded");

        var totalRows = await desktop.EvaluateAsync<int>(
            "() => document.querySelectorAll('.azure-resource-grid tbody tr').length");
        Console.WriteLine($"materialised rows: {totalRows}");

        await desktop.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "desktop-explorer.png") });

        // Mobile: card list present, grid absent from the DOM entirely.
        var mobile = await OpenAsync(390, 844, "/azure");
        await mobile.GetByText("Advanced diagnostics").ClickAsync();
        await mobile.WaitForSelectorAsync(".azure-resource-cards", new() { Timeout = 40_000 });
        await mobile.WaitForTimeoutAsync(500);

        Assert.Equal(1, await mobile.Locator(".azure-resource-cards").CountAsync());
        Assert.Equal(0, await mobile.Locator(".azure-resource-grid").CountAsync());

        await mobile.ScreenshotAsync(new() { Path = Path.Combine(ArtifactDir, "mobile-explorer.png") });
    }
}
