using System.Runtime.CompilerServices;

namespace Haworks.BuildingBlocks.Testing;

/// <summary>
/// Per-assembly module initializer that fixes two Testcontainers / Docker
/// integration issues observed on macOS Docker Desktop:
///
/// 1. **Catastrophic regex backtracking in Testcontainers 4.3.0's `MatchImage`.**
///    Triggered from the static cctor of <c>ResourceReaper</c>. The default
///    .NET 9 regex match timeout trips before the regex completes, throwing
///    <c>RegexMatchTimeoutException</c> at type-init time and bringing down
///    the entire test run with a <c>TypeInitializationException</c>. We
///    raise the timeout to 1 minute so the regex can finish (slow once,
///    fine after).
///
/// 2. **Docker socket not found via Testcontainers' default discovery.**
///    Testcontainers tries the standard Docker socket paths first, then
///    falls back to shelling out to `docker context inspect` and parsing
///    its output via the same pathological regex. On modern Docker Desktop
///    Macs the socket lives at <c>~/.docker/run/docker.sock</c> rather than
///    <c>/var/run/docker.sock</c>; pinning <c>DOCKER_HOST</c> to the user
///    socket avoids the fallback path entirely.
///
/// This file is the canonical source. Each integration test project links
/// it via a <c>&lt;Compile Include="..."&gt;</c> so the
/// <c>[ModuleInitializer]</c> attribute is compiled INTO that test
/// assembly (module initializers only fire for the assembly they're
/// compiled into — referencing this assembly via ProjectReference is not
/// enough). When the fix needs to evolve, edit this file once.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMinutes(1));

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            var defaultSocket = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".docker", "run", "docker.sock");
            if (File.Exists(defaultSocket))
            {
                Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{defaultSocket}");
            }
        }

        // Ryuk (Testcontainers' resource-reaper) needs to bind-mount the
        // Docker socket INTO its own container. Docker Desktop on macOS
        // can't bind-mount `~/.docker/run/docker.sock` into a Linux container
        // (the path is in the user's home and Mac's file-sharing rejects it
        // with "operation not supported"). The compatibility alias at
        // `/var/run/docker.sock` IS bind-mountable. Force the reaper to
        // mount that path regardless of what DOCKER_HOST points to.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE")))
        {
            Environment.SetEnvironmentVariable(
                "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", "/var/run/docker.sock");
        }
    }
}
