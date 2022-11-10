using System;

namespace common;

public sealed record GatewayApisUri : IArtifactUri
{
    public Uri Uri { get; }

    public GatewayApisUri(GatewayUri gatewayUri)
    {
        Uri = gatewayUri.AppendPath("apis");
    }
}

public sealed record GatewayApiUri : IArtifactUri
{
    public Uri Uri { get; }

    public GatewayApiUri(ApiName apiName, GatewayApisUri gatewayApisUri)
    {
        Uri = gatewayApisUri.AppendPath(apiName.ToString());
    }
}

public sealed record GatewayApisFile : IArtifactFile
{
    public static string Name { get; } = "apis.json";

    public ArtifactPath Path { get; }

    public GatewayDirectory GatewayDirectory { get; }

    public GatewayApisFile(GatewayDirectory gatewayDirectory)
    {
        Path = gatewayDirectory.Path.Append(Name);
        GatewayDirectory = gatewayDirectory;
    }
}