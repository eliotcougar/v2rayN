using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Extensions;
using ReactiveUI.Primitives.Signals;
using ServiceLib.Services.AppRouting;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.AppRouting;

[NotInParallel]
public class ServicePickerTests
{
    static ServicePickerTests() => RxAppBuilder.CreateReactiveUIBuilder()
        .WithMainThreadScheduler(ImmediateSequencer.Instance).WithCoreServices().BuildApp();

    [Test]
    public async Task PickerListsStoppedServicesAndKeepsSelectionsHiddenBySearch()
    {
        using var picker = new AppRoutingServiceViewModel(["MissingSvc", "Dnscache"], () =>
        [
            new("Dnscache", "DNS Client", 0),
            new("OtherSvc", "Other Service", 345),
            new("ThirdSvc", "Third Service", 0),
        ]);
        await picker.Initialize();
        await picker.Services.Count.Should().BeEqualTo(4);
        await picker.Services.Single(row => row.Name == "Dnscache").Selected.Should().BeTrue();
        await picker.Services.Single(row => row.Name == "MissingSvc").Details.Contains(ServiceLib.Resx.ResUI.AppRoutingServiceUnavailable).Should().BeTrue();
        await picker.CanConfirm.Should().BeTrue();

        picker.Search = "Other Service";
        await picker.Services.Single().Name.Should().BeEqualTo("OtherSvc");
        await picker.SelectVisibleCmd.Execute().ToTask();
        picker.Search = "thirdsvc";
        await picker.Services.Single().DisplayName.Should().BeEqualTo("Third Service");
        await picker.ClearVisibleCmd.Execute().ToTask();
        await picker.SelectedServiceNames().Keys.Order().SequenceEqual(new[] { "Dnscache", "MissingSvc", "OtherSvc" }).Should().BeTrue();
        await picker.SelectedServiceNames()["Dnscache"].Should().BeEqualTo("DNS Client");
    }

    [Test]
    public async Task PickerCanBeClosedBeforeServiceInventoryCompletes()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var picker = new AppRoutingServiceViewModel([], () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return [new("One", "One", 0)];
        });
        var load = picker.Initialize();
        await entered.Task;
        picker.Dispose();
        release.SetResult();
        await load;
        await picker.Services.Count.Should().BeEqualTo(0);
    }
}
