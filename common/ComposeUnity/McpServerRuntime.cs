using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ComposeUnity;

static class McpActivation {
    internal static bool Parse() =>
        Parse(Environment.GetEnvironmentVariable("COMPOSE_UNITY_MCP"));

    internal static bool Parse(string? value) =>
        value switch {
            null or "" or "0" => false,
            "1" => true,
            _ => throw new ArgumentException("COMPOSE_UNITY_MCP must be unset, 0, or 1.")
        };
}

sealed class McpServerRuntime : IAsyncDisposable {
    readonly WebApplication application;
    readonly UnityMcpController controller;
    readonly CancellationTokenSource stopping;
    int stopped;

    McpServerRuntime(
        WebApplication application,
        UnityMcpController controller,
        CancellationTokenSource stopping,
        Task completion) {
        this.application = application;
        this.controller = controller;
        this.stopping = stopping;
        this.completion = completion;
    }

    internal Task completion { get; }

    public async ValueTask DisposeAsync() {
        await StopAsync();
        await application.DisposeAsync();
        await controller.DisposeAsync();
        stopping.Dispose();
    }

    internal static async Task<McpServerRuntime> StartAsync(CancellationToken sidecarStopping) {
        var stopping = CancellationTokenSource.CreateLinkedTokenSource(sidecarStopping);
        UnityMcpController? controller = null;
        WebApplication? application = null;
        try {
            controller = await UnityMcpController.CreateAsync(stopping.Token);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(McpServerRuntime).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://0.0.0.0:8080");
            builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
            builder.Services.AddSingleton(controller);
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<UnityMcpTools>();

            application = builder.Build();
            application.UseHostFiltering();
            application.Use(async (context, next) => {
                if (context.Request.Headers.TryGetValue("Origin", out var values)
                    && values.Any(value => !IsLoopbackOrigin(value))) {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Origin is not allowed.");
                    return;
                }

                await next(context);
            });
            application.MapMcp("/mcp");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Lifetime.ApplicationStopped.Register(completion.SetResult);
            await application.StartAsync(stopping.Token);
            return new McpServerRuntime(application, controller, stopping, completion.Task);
        } catch {
            if (application is not null) {
                await application.DisposeAsync();
            }

            if (controller is not null) {
                await controller.DisposeAsync();
            }

            stopping.Dispose();
            throw;
        }
    }

    internal static async Task<bool> CheckHealthAsync() {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, 8080, cancellation.Token);
            await using var docker = new DockerEngineClient();
            await docker.VersionAsync(cancellation.Token);
            await docker.InspectSelfAsync(cancellation.Token);
            return true;
        } catch {
            return false;
        }
    }

    internal async Task StopAsync() {
        if (Interlocked.Exchange(ref stopped, 1) != 0) {
            return;
        }

        application.Lifetime.StopApplication();
        stopping.Cancel();
        await controller.StopActiveWorkersAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            await application.StopAsync(timeout.Token);
        } catch (OperationCanceledException) {
            Console.Error.WriteLine("compose-unity-sidecar: MCP shutdown exceeded 30 seconds");
        }
    }

    internal static bool IsLoopbackOrigin(string? value) {
        return Uri.TryCreate(value, UriKind.Absolute, out var origin)
               && origin.IsLoopback
               && origin.Scheme is "http" or "https";
    }
}

[McpServerToolType]
sealed class UnityMcpTools(UnityMcpController controller) {
    [McpServerTool(
        Name = "get_project_info",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Read a Docker-host Unity project's YAML and JSON metadata and return its root, product metadata, editor and project versions, major code and rendering settings, input handling, and complete package manifest without installing or launching Unity.")]
    public async Task<object> ProjectInfoAsync(
        [Description("Docker-daemon host path to an existing Unity project.")]
        string projectRoot,
        CancellationToken cancellationToken) =>
        await InvokeAsync(() => controller.ProjectInfoAsync(projectRoot, cancellationToken));

    [McpServerTool(
        Name = "run_tests",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Run Unity Test Runner modes for a Docker-host Unity project. Valid JUnit reports return compact pass/fail counts and failure details; infrastructure errors return the complete ordered Unity log.")]
    public async Task<object> RunTestsAsync(
        [Description("Docker-daemon host path to an existing Unity project.")]
        string projectRoot,
        [Description("One or more Unity test modes, such as EditMode, PlayMode, or a platform.")]
        string[] modes,
        CancellationToken cancellationToken) =>
        await InvokeAsync(() => controller.RunTestsAsync(projectRoot, modes, cancellationToken));

    [McpServerTool(
        Name = "execute_method",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Run a fully qualified static Unity editor method, ask Unity to quit afterward, and return its exit status and relevant output.")]
    public async Task<object> ExecuteMethodAsync(
        [Description("Docker-daemon host path to an existing Unity project.")]
        string projectRoot,
        [Description("Fully qualified static Unity editor method name.")]
        string method,
        CancellationToken cancellationToken,
        [Description("Optional arguments forwarded without shell reinterpretation.")]
        string[]? arguments = null) =>
        await InvokeAsync(() => controller.ExecuteMethodAsync(projectRoot, method, arguments, cancellationToken));

    static async Task<object> InvokeAsync(Func<Task<object>> action) {
        try {
            return await action();
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception exception) {
            throw new McpException(exception.Message, exception);
        }
    }
}
