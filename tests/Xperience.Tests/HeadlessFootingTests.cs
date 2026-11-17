namespace Xperience.Tests;

/// <summary>The window tests' own footing: what a test body does after a wait has to count.</summary>
public class HeadlessFootingTests
{
    [Fact]
    public async Task A_failure_after_a_wait_inside_a_window_test_reaches_the_test()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessApp.OnUi(async () =>
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
            throw new InvalidOperationException("seen");
        }));
        Assert.Equal("seen", failure.Message);
    }

    [Fact]
    public async Task A_window_test_runs_on_the_ui_thread_before_and_after_a_wait()
    {
        bool before = false, after = false;
        await HeadlessApp.OnUi(async () =>
        {
            before = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            await Task.Delay(30, TestContext.Current.CancellationToken);
            after = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
        });
        Assert.True(before);
        Assert.True(after);
    }
}
