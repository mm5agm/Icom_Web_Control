namespace IcomWebControl.Tests;

/// <summary>
/// A radio with no scope, which is the IC-7100. The frame guard would stop the
/// bytes anyway; these check the controller does not ask, tells the browsers
/// why, and leaves the operator's settings alone.
/// </summary>
public class NoScopeTests
{
    private const byte Ic7100 = 0x88;
    private const byte Ic7300 = 0x94;

    [Fact]
    public async Task BrowsersAreToldItHasNone()
    {
        var (controller, _, _, hub) = TestHost.NewControllerWithHub(new SimulatedRadioClient(Ic7100));
        await controller.ConnectAsync();
        Assert.Contains("A:noscope", hub.ScopeStatuses);
        Assert.DoesNotContain("A:connecting", hub.ScopeStatuses);
    }

    [Fact]
    public async Task ARadioWithAScopeIsNotToldThat()
    {
        var (controller, _, _, hub) = TestHost.NewControllerWithHub(new SimulatedRadioClient(Ic7300));
        await controller.ConnectAsync();
        Assert.DoesNotContain("A:noscope", hub.ScopeStatuses);
    }

    [Fact]
    public async Task DiagnosticsSaySoRatherThanSwitchedOff()
    {
        var (controller, _, _, _) = TestHost.NewControllerWithHub(new SimulatedRadioClient(Ic7100));
        await controller.ConnectAsync();
        var d = controller.GetScopeDiagnostics();
        Assert.True(d.NoScope);
        Assert.False(d.Enabled);
        Assert.Null(d.BlockedReason);
    }

    [Fact]
    public async Task TheScopeControlsDoNothing()
    {
        var bus = new SimulatedRadioClient(Ic7100);
        var (controller, _, settings, _) = TestHost.NewControllerWithHub(bus);
        await controller.ConnectAsync();
        int saves = settings.Saves;
        bool fixedMode = settings.Settings.ScopeFixedMode;
        bus.Sent.Clear();

        await controller.SetScopeEnabledAsync(true);
        await controller.SetScopeEnabledAsync(false);
        await controller.SetScopeModeAsync(fixedMode);   // the opposite of center=!fixedMode
        await controller.SetScopeSpanAsync(25000);

        Assert.Empty(bus.Sent);
        Assert.Equal(saves, settings.Saves);
        Assert.Equal(fixedMode, settings.Settings.ScopeFixedMode);
        Assert.True(controller.GetScopeDiagnostics().NoScope);
    }

    // The poll loop reads the pseudo-dual setting, and the second panel's status
    // only goes out with it on. So a B status shows the loop took it as on.
    [Theory]
    [InlineData(Ic7300, true)]
    [InlineData(Ic7100, false)]
    public async Task PseudoDualIsOffWithNoScope(byte address, bool expectWatchPanel)
    {
        var (controller, _, settings, hub) = TestHost.NewControllerWithHub(new SimulatedRadioClient(address));
        settings.Settings.PseudoDualReceiverEnabled = true;
        settings.Settings.PseudoDualCrossBandEnabled = true;

        await controller.StartAsync(CancellationToken.None);
        try
        {
            // Long enough for several poll loops, each asking for a fresh announcement.
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !hub.ScopeStatuses.Any(s => s.StartsWith("B:")))
            {
                controller.RequestScopeStatusAnnounce();
                await Task.Delay(100);
            }
        }
        finally
        {
            await controller.StopAsync(CancellationToken.None);
        }

        Assert.Equal(expectWatchPanel, hub.ScopeStatuses.Any(s => s.StartsWith("B:")));
    }
}
