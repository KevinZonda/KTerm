using System.Runtime.InteropServices;

namespace KevinZonda.AgentUsageMonitor.KimiCode;

internal static class KimiCodeRequestHeaders
{
    internal static void AddCliIdentity(HttpRequestMessage request, KimiCodeUsageOptions options)
    {
        request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_code_cli");
        request.Headers.TryAddWithoutValidation("X-Msh-Version", "1.0");
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", KimiCodeCredentialStore.ResolveDeviceId(options));
        request.Headers.TryAddWithoutValidation("X-Msh-Device-Name", Environment.MachineName);
        request.Headers.TryAddWithoutValidation("X-Msh-Os-Version", Environment.OSVersion.Version.ToString());
        request.Headers.TryAddWithoutValidation(
            "X-Msh-Device-Model",
            $"{Environment.OSVersion.Platform} {RuntimeInformation.OSArchitecture}");
    }
}
