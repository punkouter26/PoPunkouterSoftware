using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

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

    // HasTouch tracks IsMobile because the viewport-fit paging on /azure is gated on
    // `(hover: none)`, which Chromium only reports under touch emulation. Without it the
    // "mobile" page here would quietly get the desktop layout and every mobile assertion
    // below would be testing something the real device never sees.
    private async Task<IPage> NewPageAsync(int width, int height, bool isMobile) =>
        await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
            HasTouch = isMobile,
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
        // "Month-end forecast" is the first always-present glance card heading; the hero h1
        // is dynamic ("Everything looks good" / "N items need attention") so it is not a
        // stable anchor. "All resources" only exists once the advanced section is expanded.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Month-end forecast" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".azure-glance-grid > article")).ToHaveCountAsync(3);
        await CaptureAsync(page, $"02-azure-status-{label}.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToBeVisibleAsync();
        await CaptureAsync(page, $"03-azure-advanced-{label}.png");
    }

    /// <summary>
    /// Both routes must reflow without horizontal overflow AND without logging an error.
    ///
    /// <para>The console half was added with the GPU/audio layers. Every one of them is
    /// designed to degrade rather than fail — a missing WebGL2, a refused WebGPU adapter, a
    /// blocked AudioContext — and each degradation path is a `console.warn`. An
    /// <c>error</c> means a path nobody designed was taken, and because the layers are
    /// decorative, nothing else on the page would show it.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_ReflowWithoutOverflowOrConsoleErrors(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var errors = new List<string>();
        page.Console += (_, msg) => { if (msg.Type == "error") errors.Add(msg.Text); };
        page.PageError += (_, err) => errors.Add(err);

        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"home must not overflow horizontally at {label}");
        await CaptureAsync(page, $"04-home-reflow-{label}.png");

        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"/azure must not overflow horizontally at {label}");
        await CaptureAsync(page, $"05-azure-reflow-{label}.png");

        errors.Should().BeEmpty($"no route may log a console error at {label}");
    }

    /// <summary>
    /// The app must be silent until a visitor asks for sound, and the backdrop ladder must
    /// resolve to a state it knows about rather than throwing.
    ///
    /// <para>Silence-by-default is an accessibility floor, not a preference: an AudioContext
    /// constructed during page load is both an unrequested hardware claim and — under every
    /// current autoplay policy — a suspended context that silently swallows whatever is
    /// played through it. So the assertion is specifically that no context has been
    /// CONSTRUCTED, which is stronger and more observable than "nothing was audible".</para>
    ///
    /// <para>The tier is deliberately not asserted. Headless Chromium usually resolves to
    /// SwiftShader, which <c>failIfMajorPerformanceCaveat</c> correctly refuses, so
    /// <c>no-gpu</c> is a legitimate outcome here — the invariant is that the ladder reached
    /// one of its DEFINED states, never that it reached the top one.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Backdrop_ResolvesATier_AndStaysSilentUntilAsked(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        var known = new[] { "webgpu", "webgl2", "webgl2-field", "webgl1", "no-gpu", "reduced-motion", "context-lost" };
        var tier = await page.EvaluateAsync<string?>(
            "() => document.getElementById('app-gpu-backdrop')?.dataset.gpu ?? null");
        tier.Should().BeOneOf(known, $"the backdrop ladder must land on a defined state at {label}");

        var audio = await page.EvaluateAsync<bool[]>(
            "() => [window.audioKit.enabled, window.audioKit.stats().contextCreated]");
        audio[0].Should().BeFalse("sound must default to off");
        audio[1].Should().BeFalse("no AudioContext may be constructed before the visitor asks for one");

        // The toggle click is itself the user gesture the autoplay policy requires, so one
        // press must both flip the preference and bring a live context into existence.
        await page.Locator("[data-sound-toggle]").First.ClickAsync();
        await Assertions.Expect(page.Locator("[data-sound-toggle]").First).ToHaveAttributeAsync("aria-pressed", "true");
        (await page.EvaluateAsync<bool>("() => window.audioKit.stats().contextCreated"))
            .Should().BeTrue($"turning sound on must construct the context at {label}");
    }

    /// <summary>
    /// Reduced motion must STOP every frame loop, not merely hide its output.
    ///
    /// <para>Each animated layer used to own a private rAF loop and a private pause; the
    /// governor in js/motion-kit.js now owns all of them, and its `running` flag is the
    /// observable form of the guarantee. A layer that faded to `opacity: 0` while still
    /// being scheduled would pass any screenshot check and still burn a phone's battery.</para>
    /// </summary>
    [Fact]
    public async Task ReducedMotion_StopsEveryFrameLoop()
    {
        await using var ctx = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1000 },
            ReducedMotion = ReducedMotion.Reduce,
        });
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        // Two bools rather than the whole stats object: EvaluateAsync of a nested structure
        // adds a deserialisation step that can only obscure what is being asserted.
        var flags = await page.EvaluateAsync<bool[]>(
            "() => [window.motionKit.stats().reduced, window.motionKit.stats().running]");
        flags[0].Should().BeTrue("the governor must see the reduced-motion query");
        flags[1].Should().BeFalse("no rAF loop may be scheduled under reduced motion");

        // And the backdrop must not have built GPU resources it can never draw with.
        (await page.EvaluateAsync<string?>("() => document.getElementById('app-gpu-backdrop')?.dataset.gpu ?? null"))
            .Should().Be("reduced-motion");
    }

    // ─── Regressions inherited from the 2026 UI pass ──────────────────────────
    // These four survived the temporary UiRedesignVerification harness because each
    // guards a defect that can recur from an ordinary CSS or markup edit. The rest of
    // that harness asserted one-time migration facts (cards no longer carry
    // backdrop-filter, the rz-sidebar column is collapsed, the topbar's pixel-level
    // spatial contract) and was retired with it.

    /// <summary>
    /// The glance panels must actually draw their metrics.
    ///
    /// <para>This used to assert two <c>RadzenChart</c> instances had non-zero height and had
    /// emitted SVG geometry — the failure it guarded was a scoped-CSS rule ceasing to match,
    /// after which Radzen measured a zero-height box and drew an empty strip that still looked
    /// "present" in a screenshot. Both charts were replaced by <c>MetricBars</c> (see that
    /// component's header for why), so the same defect now takes a different shape: a bar
    /// whose fill width resolves to zero, or a panel that renders no rows at all. The
    /// invariant is unchanged — a panel that reserves space must put marks in it.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_GlancePanels_DrawTheirMetrics(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-glance-grid", new() { Timeout = 40_000 });
        await page.WaitForSelectorAsync(".azure-glance-grid .metric-bar", new() { Timeout = 40_000 });

        // Every row must have a laid-out track and a fill with real width inside it. A zero
        // here is the bar-shaped form of the collapsed chart this test replaced.
        var fills = await page.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.azure-glance-grid .metric-bar__fill')]" +
            ".map(el => el.getBoundingClientRect().width)");
        fills.Should().NotBeEmpty($"no metric bars rendered at {label}");
        fills.Should().OnlyContain(w => w > 0, $"a metric bar drew no fill at {label}");

        // The leader is scaled to 100%, so the widest bar must fill most of its track.
        var ratio = await page.EvaluateAsync<double>(@"() => {
            const fill = [...document.querySelectorAll('.azure-glance-grid .metric-bar__fill')]
                .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width)[0];
            return fill.getBoundingClientRect().width / fill.parentElement.getBoundingClientRect().width;
        }");
        ratio.Should().BeGreaterThan(0.9, $"the top-ranked bar must fill its track at {label}");

        // The forecast ring is the third panel and is still inline SVG.
        (await page.Locator(".azure-forecast__ring svg").CountAsync())
            .Should().Be(1, $"the forecast ring must render at {label}");
    }

    /// <summary>
    /// The catalogue's first-screen contract at mobile portrait: heading, freshness row and a
    /// WHOLE first card, inside 844px minus the chrome.
    ///
    /// <para>`/` deliberately keeps ordinary scrolling — a 24-item catalogue is a list, and
    /// paging a list fights the reader — so the guarantee it makes instead is that the answer
    /// is complete above the fold. Nothing enforced that before, and it is exactly the sort of
    /// thing an innocent type-scale or padding change breaks silently: the existing
    /// horizontal-overflow test passes just as happily with the first card pushed off-screen.
    /// The assertion is that the card's BOTTOM edge is above the fold, not merely its top —
    /// half a card is not a card.</para>
    /// </summary>
    [Fact]
    public async Task Home_FirstScreen_ShowsAWholeCard()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".app-portfolio-card", new() { Timeout = 30_000 });

        var probe = await page.EvaluateAsync<double[]>(@"() => {
            const card = document.querySelector('.app-portfolio-card');
            const summary = document.querySelector('.portfolio-summary');
            const h1 = document.querySelector('.portfolio-header h1');
            return [
                h1.getBoundingClientRect().bottom,
                summary.getBoundingClientRect().bottom,
                card.getBoundingClientRect().bottom,
                window.innerHeight
            ];
        }");

        var fold = probe[3];
        probe[0].Should().BeLessThan(fold, "the page title must be above the fold");
        probe[1].Should().BeLessThan(fold, "the freshness and reload row must be above the fold");
        probe[2].Should().BeLessThanOrEqualTo(fold,
            "the whole first card must fit the first screen at 390x844 — half a card is not a card");
    }

    /// <summary>
    /// /azure pages instead of scrolling at mobile portrait.
    ///
    /// <para>Three things have to hold together for that to be true rather than merely
    /// configured: the container must actually be snapping (the CSS gate is
    /// <c>portrait AND hover:none</c>, so a wrong gate silently yields a normal long page);
    /// the dot strip must have one dot per pane, since a snap container with no affordance
    /// reads as a page that has mysteriously stopped scrolling; and each ordinary pane must
    /// genuinely FIT one screen. The last is the one that rots — it is what stops a new
    /// section being dropped into whichever pane happens to be nearest.</para>
    ///
    /// <para><c>.app-pane--tall</c> is excluded on purpose. Advanced diagnostics cannot
    /// honestly be compressed to a screen, so it scrolls internally instead of being cut;
    /// that is a declared exception, not a failure. It is closed here anyway.</para>
    /// </summary>
    [Fact]
    public async Task Azure_MobilePortrait_PanesFitTheViewport()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-ops-page .app-pane", new() { Timeout = 40_000 });
        // The dots are injected by a MutationObserver-driven rAF in js/helpers.js, so they
        // land a frame after the panes do.
        await page.WaitForSelectorAsync(".app-pager-dot", new() { Timeout = 10_000 });

        var snapType = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.querySelector('[data-snap-pager]')).scrollSnapType");
        snapType.Should().NotBe("none", "mobile portrait must page, not scroll freely");

        var counts = await page.EvaluateAsync<int[]>(
            "() => [document.querySelectorAll('[data-snap-pager] > .app-pane').length, " +
            "document.querySelectorAll('.app-pager-dot').length]");
        counts[0].Should().BeGreaterThan(1, "paging needs more than one pane");
        counts[1].Should().Be(counts[0], "there must be exactly one dot per pane");

        // Every ordinary pane must fit the pager's own client height.
        var overflows = await page.EvaluateAsync<string[]>(@"() => {
            const pager = document.querySelector('[data-snap-pager]');
            const budget = pager.clientHeight;
            return [...pager.querySelectorAll(':scope > .app-pane')]
                .filter(p => !p.classList.contains('app-pane--tall'))
                .filter(p => p.scrollHeight > budget + 1)
                .map(p => p.className + ' needs ' + p.scrollHeight + 'px of ' + budget + 'px');
        }");
        overflows.Should().BeEmpty("every ordinary pane must fit one screen at 390x844");

        // …and must actually BE a screen tall. The assertion above compares a pane's CONTENT
        // height to the container's, which every pane passes while being a sixth of a screen
        // tall — which is exactly what happened: `[data-snap-pager] { display: block }` lost
        // on specificity to the scoped `.azure-ops-page { display: grid }`, the panes became
        // six grid rows sharing 756px at 126px each, and all six rendered on top of one
        // another with their content overflowing. Every check here passed throughout. Measure
        // the box the visitor sees, not just the content inside it.
        var geometry = await page.EvaluateAsync<string[]>(@"() => {
            const pager = document.querySelector('[data-snap-pager]');
            const budget = pager.clientHeight;
            const bad = [];
            if (getComputedStyle(pager).display !== 'block')
                bad.push('pager display is ' + getComputedStyle(pager).display + ', not block');
            for (const p of pager.querySelectorAll(':scope > .app-pane')) {
                const h = p.getBoundingClientRect().height;
                if (h < budget - 1)
                    bad.push(p.className + ' renders ' + Math.round(h) + 'px of a ' + budget + 'px screen');
            }
            return bad;
        }");
        geometry.Should().BeEmpty("each pane must fill the screen it is supposed to be");
    }

    /// <summary>
    /// The glance grid must actually reflow, not merely avoid overflowing.
    ///
    /// <para>Its responsive rules were written without <c>::deep</c> while the base rule had
    /// it, which compiles to LOWER specificity for the override — media queries add none, so
    /// the 3-column base won at every width and a phone rendered three 109px-wide charts side
    /// by side. Nothing caught it: <c>CoreRoutes_ReflowWithoutHorizontalOverflow</c> passes
    /// because <c>minmax(0,1fr)</c> squeezes rather than overflows, and
    /// <c>Azure_GlanceCharts_HaveHeightAndDrawGeometry</c> only checks height.</para>
    ///
    /// <para>Asserting the tiles are stacked (equal left edge) rather than reading
    /// <c>grid-template-columns</c> keeps this about the visible result.</para>
    /// </summary>
    [Fact]
    public async Task Azure_GlanceCards_StackVertically_OnMobile()
    {
        var page = await NewPageAsync(390, 844, isMobile: true);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".azure-glance-grid > article", new() { Timeout = 40_000 });

        var boxes = await page.EvaluateAsync<double[][]>(
            "() => [...document.querySelectorAll('.azure-glance-grid > article')].map(el => " +
            "{ const r = el.getBoundingClientRect(); return [r.left, r.width]; })");

        boxes.Should().HaveCount(3);
        boxes.Select(b => b[0]).Distinct().Should().ContainSingle(
            "the three glance cards must share a left edge — i.e. be stacked, not side by side");
        boxes.Should().OnlyContain(b => b[1] > 250,
            "a full-width card at 390px must be far wider than a 1/3 column");
    }

    /// <summary>
    /// The resource explorer is ONE tree at every viewport.
    ///
    /// <para>It used to be two — a card list and a RadzenDataGrid — first both rendered with
    /// CSS hiding the wrong one (every row built twice), then behind a matchMedia bridge
    /// picking between them. The grid is gone: it carried sorting, per-column filter menus,
    /// column resize and virtualization over a list that measures 48 rows, and its filter
    /// menus put buttons named "*A*Contains" into the accessibility tree. The cards reflow on
    /// their own, so there is no branch left. This test now guards against a second tree being
    /// reintroduced rather than against both rendering at once.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_ResourceExplorer_RendersExactlyOneTree(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await page.WaitForSelectorAsync(".azure-resource-cards", new() { Timeout = 40_000 });

        (await page.Locator(".azure-resource-cards").CountAsync())
            .Should().Be(1, $"exactly one explorer tree must be in the DOM at {label}");
        (await page.Locator(".azure-resource-grid, .rz-data-grid, .rz-datatable").CountAsync())
            .Should().Be(0, "the data grid was removed; a returning one is a regression");
        (await page.Locator(".azure-resource-card").CountAsync())
            .Should().BeGreaterThan(0, "the explorer must actually list resources");
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

    /// <summary>
    /// "Skip to content" must land somewhere on EVERY route.
    ///
    /// <para>MainLayout renders the link on every page pointing at <c>#main-content</c>, and
    /// <c>/azure</c> had no <c>&lt;main&gt;</c> at all — its page root was a bare div — so the
    /// first focusable element on the ops dashboard was a link to nothing, while the identical
    /// link on "/" worked. A missing landmark is invisible to everything except a keyboard or
    /// a screen reader, which is exactly who the link is for.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_HaveOneMainLandmark_AndAWorkingSkipLink(
        string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);

        foreach (var route in new[] { "/", "/azure" })
        {
            await page.GotoAsync($"{BaseUrl}{route}", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync("main", new() { Timeout = 40_000 });

            (await page.Locator("main").CountAsync()).Should().Be(
                1, $"{route} needs exactly one main landmark at {label}");

            var resolved = await page.EvaluateAsync<bool>(@"() => {
                const link = document.querySelector('.app-skip-link');
                return !!(link && document.querySelector(link.getAttribute('href')));
            }");
            resolved.Should().BeTrue($"the skip link must resolve on {route} at {label}");
        }
    }

    /// <summary>
    /// Every icon must render as a GLYPH, not as its own name.
    ///
    /// <para>Material Symbols draws through ligatures — the element's text is the icon's name —
    /// so the font subset in <c>wwwroot/fonts</c> (see SCRIPTS/Build-IconFontSubset.py, which
    /// replaced a 1 MB full font with 16 KB) fails in one specific way: a name missing from the
    /// subset does not go blank, it renders the literal word "refresh" inside a button. This
    /// measures rendered width against font size, which is the only signal that separates the
    /// two, and it opens every disclosure first so the icons behind them are covered too.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_EveryIcon_RendersAsAGlyphNotItsName(
        string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await page.WaitForSelectorAsync(".azure-attention-list, .azure-resource-cards", new() { Timeout = 40_000 });
        await page.EvaluateAsync("() => document.querySelectorAll('details').forEach(d => { d.open = true; })");
        await page.WaitForTimeoutAsync(800);

        var words = await page.EvaluateAsync<string[]>(@"() => {
            const bad = [];
            for (const el of document.querySelectorAll('*')) {
                const cs = getComputedStyle(el);
                if (!/Material Symbols/i.test(cs.fontFamily)) continue;
                const text = (el.textContent || '').trim();
                if (!text || !/^[a-z0-9_]+$/.test(text)) continue;
                const width = el.getBoundingClientRect().width;
                if (width > parseFloat(cs.fontSize) * 2.2) bad.push(text + ' @' + Math.round(width) + 'px');
            }
            return bad;
        }");
        words.Should().BeEmpty($"these icon names have no glyph in the font subset at {label}");

        var fullFont = await page.EvaluateAsync<int>(
            "() => performance.getEntriesByType('resource').filter(r => /MaterialSymbolsOutlined/i.test(r.name)).length");
        fullFont.Should().Be(0, "the 1 MB Radzen icon font must never be fetched");
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

