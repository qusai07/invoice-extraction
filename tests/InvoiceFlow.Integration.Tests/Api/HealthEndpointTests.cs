using System.Net;

namespace InvoiceFlow.Integration.Tests.Api;

[Collection(ApiCollection.Name)]
public class HealthEndpointTests(InvoiceFlowFactory factory)
{
    [Fact]
    public async Task Health_returns_200_without_sign_in()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
