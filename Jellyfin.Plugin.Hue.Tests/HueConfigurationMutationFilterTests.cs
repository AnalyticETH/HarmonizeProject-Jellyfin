using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Api;
using Jellyfin.Plugin.Hue.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueConfigurationMutationFilterTests
{
    [Theory]
    [InlineData("/HueSync/SceneSchedules/BulkRun")]
    [InlineData("/HueSync/SceneSchedules/BulkCancel")]
    [InlineData("/HueSync/SceneSchedules/cue-1/Run")]
    [InlineData("/HueSync/SceneSchedules/cue-1/Cancel")]
    public async Task SceneRunAndCancelRoutesDoNotAcquireConfigurationMutation(string path)
    {
        var gate = new HueBridgeLifecycleGate();
        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext("POST", path);
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                actionExecuted = true;
                Assert.False(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.True(actionExecuted);
        Assert.False(gate.IsConfigurationMutationActive);
    }

    [Fact]
    public async Task BulkCancelPassesThroughWhileScheduledCueOwnsTheSchedulerLease()
    {
        var gate = new HueBridgeLifecycleGate();
        using var activeRun = gate.TryEnterSchedulerEvaluation();
        Assert.NotNull(activeRun);

        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext("POST", "/HueSync/SceneSchedules/BulkCancel");
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                actionExecuted = true;
                Assert.True(gate.IsSchedulerEvaluationActive);
                Assert.False(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.True(actionExecuted);
    }

    [Theory]
    [InlineData("POST", "/HueSync/Configuration")]
    [InlineData("POST", "/HueSync/SceneSchedules/BulkEnabled")]
    [InlineData("DELETE", "/HueSync/SceneSchedules/History")]
    public async Task ConfigurationWriterRoutesAcquireConfigurationMutation(
        string method,
        string path)
    {
        var gate = new HueBridgeLifecycleGate();
        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext(method, path);
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                actionExecuted = true;
                Assert.True(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.True(actionExecuted);
        Assert.False(gate.IsConfigurationMutationActive);
    }

    [Theory]
    [InlineData("GET", "/HueSync/Diagnostics")]
    [InlineData("POST", "/HueSync/ScenePlaylists/playlist-1/Preview")]
    public async Task ReadAndPreviewRoutesDoNotAcquireConfigurationMutation(
        string method,
        string path)
    {
        var gate = new HueBridgeLifecycleGate();
        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext(method, path);

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                Assert.False(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.False(gate.IsConfigurationMutationActive);
    }

    [Theory]
    [InlineData("POST", "/HueSync/ColorPresets/PreviewLights/Rename")]
    [InlineData("POST", "/HueSync/ColorPresets/PreviewLights/Duplicate")]
    [InlineData("DELETE", "/HueSync/ColorPresets/PreviewLights")]
    [InlineData("DELETE", "/HueSync/ColorPresets/Preview")]
    [InlineData("POST", "/HueSync/ScenePlaylists/PreviewPlaylist/Rename")]
    [InlineData("POST", "/HueSync/ScenePlaylists/PreviewPlaylist/Duplicate")]
    [InlineData("DELETE", "/HueSync/ScenePlaylists/PreviewPlaylist")]
    [InlineData("POST", "/HueSync/SceneSchedules/RunCue/Enabled")]
    [InlineData("POST", "/HueSync/SceneSchedules/CancelCue/Duplicate")]
    [InlineData("DELETE", "/HueSync/SceneSchedules/CancelCue")]
    [InlineData("DELETE", "/HueSync/SceneSchedules/Run")]
    [InlineData("DELETE", "/HueSync/SceneSchedules/BulkRun")]
    [InlineData("DELETE", "/HueSync/SceneSchedules/BulkCancel")]
    public async Task ResourceNamesContainingPreviewRunOrCancelStillAcquireConfigurationMutation(
        string method,
        string path)
    {
        var gate = new HueBridgeLifecycleGate();
        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext(method, path);
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                actionExecuted = true;
                Assert.True(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.True(actionExecuted);
        Assert.False(gate.IsConfigurationMutationActive);
    }

    [Theory]
    [InlineData("POST", "/HueSync/ColorPresets/PreviewLights/Preview")]
    [InlineData("POST", "/HueSync/ColorPresets/BulkPreview")]
    [InlineData("POST", "/HueSync/ScenePlaylists/PreviewPlaylist/Preview")]
    [InlineData("POST", "/HueSync/ScenePlaylists/BulkPreview")]
    [InlineData("POST", "/HueSync/SceneSchedules/RunCue/Run")]
    [InlineData("POST", "/HueSync/SceneSchedules/CancelCue/Cancel")]
    [InlineData("POST", "/HueSync/SceneSchedules/BulkRun")]
    [InlineData("POST", "/HueSync/SceneSchedules/BulkCancel")]
    public async Task ExactPreviewRunAndCancelActionsDoNotAcquireConfigurationMutation(
        string method,
        string path)
    {
        var gate = new HueBridgeLifecycleGate();
        var filter = new HueConfigurationMutationFilter(gate);
        var context = CreateExecutingContext(method, path);
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                actionExecuted = true;
                Assert.False(gate.IsConfigurationMutationActive);
                return Task.FromResult(CreateExecutedContext(context));
            });

        Assert.True(actionExecuted);
        Assert.False(gate.IsConfigurationMutationActive);
    }

    private static ActionExecutingContext CreateExecutingContext(
        string method,
        string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext context)
        => new(
            context,
            new List<IFilterMetadata>(),
            controller: new object());
}
