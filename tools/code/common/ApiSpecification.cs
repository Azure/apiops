using Microsoft.OpenApi;
using System;

namespace common;

public abstract record ApiSpecificationFile : IArtifactFile
{
    public abstract ArtifactPath Path { get; }

    public abstract ApiDirectory ApiDirectory { get; }

    public record OpenApi : ApiSpecificationFile
    {
        public override ArtifactPath Path { get; }

        public override ApiDirectory ApiDirectory { get; }

        public OpenApiSpecVersion Version { get; }

        public OpenApiFormat Format { get; }

        public OpenApi(OpenApiSpecVersion version, OpenApiFormat format, ApiDirectory apiDirectory)
        {
            var fileName = GetFileName(format);
            Path = apiDirectory.Path.Append(fileName);
            ApiDirectory = apiDirectory;
            Version = version;
            Format = format;
        }

        private static string GetFileName(OpenApiFormat format) =>
            format switch
            {
                OpenApiFormat.Json => "specification.json",
                OpenApiFormat.Yaml => "specification.yaml",
                _ => throw new NotSupportedException()
            };
    }

    public record GraphQl : ApiSpecificationFile
    {
        public static string Name { get; } = "specification.graphql";

        public override ArtifactPath Path { get; }

        public override ApiDirectory ApiDirectory { get; }

        public GraphQl(ApiDirectory apiDirectory)
        {
            Path = apiDirectory.Path.Append(Name);
            ApiDirectory = apiDirectory;
        }
    }

    public record Wsdl : ApiSpecificationFile
    {
        public static string Name { get; } = "specification.wsdl";

        public override ArtifactPath Path { get; }

        public override ApiDirectory ApiDirectory { get; }

        public Wsdl(ApiDirectory apiDirectory)
        {
            Path = apiDirectory.Path.Append(Name);
            ApiDirectory = apiDirectory;
        }
    }

    public record Wadl : ApiSpecificationFile
    {
        public static string Name { get; } = "specification.wadl";

        public override ArtifactPath Path { get; }

        public override ApiDirectory ApiDirectory { get; }

        public Wadl(ApiDirectory apiDirectory)
        {
            Path = apiDirectory.Path.Append(Name);
            ApiDirectory = apiDirectory;
        }
    }
}