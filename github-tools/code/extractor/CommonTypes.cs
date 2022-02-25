namespace extractor;

internal class NonAuthenticatedHttpClient
{
    private readonly HttpClient client;

    public NonAuthenticatedHttpClient(HttpClient client)
    {
        if (client.DefaultRequestHeaders.Authorization is not null)
        {
            throw new InvalidOperationException("Client cannot have authorization headers.");
        }

        this.client = client;
    }

    public async Task<Stream> GetSuccessfulResponseStream(Uri uri, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(uri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        else
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"Response was unsuccessful. Status code is {response.StatusCode}. Response content was {responseContent}.";
            throw new InvalidOperationException(errorMessage);
        }
    }
}