using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Licensing;

namespace VideoDownloader.Infrastructure.Tests;

public class LicenseProtocolTests
{
    [LiveLicenseFact]
    public async Task LiveClient_EachStartupQueriesServer_AndAcceptsApprovedMachine()
    {
        using var handler = new Recorder(new SocketsHttpHandler { UseProxy = false });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(new AppOptions().License.Endpoint),
            Timeout = TimeSpan.FromSeconds(8)
        };
        for (var index = 1; index <= 2; index++)
        {
            var service = new LicenseService(client, Options.Create(new AppOptions()), NullLogger<LicenseService>.Instance);
            await service.InitializeAsync();
            Assert.True(handler.Status == HttpStatusCode.OK, $"HTTP {handler.Status}; body: {handler.Body}");
            Assert.Equal(index, handler.Count);
            Assert.Matches("^[0-9a-f]{64}$", service.Current.MachineId);
            Assert.Equal("server", service.Current.Source);
            Assert.True(service.Current.IsFull);
            Assert.True(service.Current.IsValid);
            Assert.Null(service.DownloadLimitBytes);
        }
    }

    private sealed class Recorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count { get; private set; }
        public HttpStatusCode Status { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.Content?.Headers.ContentLength > 0, "License server requires Content-Length, not chunked JSON.");
            Count++;
            var response = await base.SendAsync(request, ct);
            Status = response.StatusCode;
            if (!response.IsSuccessStatusCode) Body = await response.Content.ReadAsStringAsync(ct);
            return response;
        }
    }
}

public sealed class LiveLicenseFactAttribute : FactAttribute
{
    public LiveLicenseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VD_LICENSE_LIVE_TEST") != "1")
            Skip = "Run scripts/verify-licensing.ps1 on the approved test machine.";
    }
}
