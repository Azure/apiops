namespace common;

public sealed record GatewayApiResource : ICompositeResource
{
    private GatewayApiResource() { }

    public string FileName => "gatewayApiInformation.json";

    public IResourceWithDirectory Primary { get; } = GatewayResource.Instance;

    public IResourceWithDirectory Secondary { get; } = ApiResource.Instance;

    public static GatewayApiResource Instance { get; } = new();
}