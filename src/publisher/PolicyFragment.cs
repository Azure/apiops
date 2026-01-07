using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<JsonObject>> GetPolicyFragmentDto(ResourceName name, CancellationToken cancellationToken);

internal static partial class ResourceModule
{
    private static void ConfigureGetPolicyFragmentDto(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetCurrentFileOperations(builder);
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);

        builder.TryAddSingleton(ResolveGetPolicyFragmentDto);
    }

    internal static GetPolicyFragmentDto ResolveGetPolicyFragmentDto(IServiceProvider provider)
    {
        var getCurrentFileOperations = provider.GetRequiredService<GetCurrentFileOperations>();
        var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();

        var resource = PolicyFragmentResource.Instance;
        var parents = ParentChain.Empty;

        return async (name, cancellationToken) =>
        {
            var fileOperations = getCurrentFileOperations();
            var informationFileDtoOption = await getInformationFileDto(resource, name, parents, fileOperations.ReadFile, fileOperations.GetSubDirectories, cancellationToken);
            var policyContentsOption = await getPolicyFileContents(resource, name, parents, fileOperations.ReadFile, cancellationToken);

            return (informationFileDtoOption.IfNoneNull(), policyContentsOption.IfNoneNull()) switch
            {
                (null, null) => Option.None,
                (var informationFileDto, null) => informationFileDto,
                (null, var policyContents) => PolicyContentsToDto(policyContents),
                (var informationFileDto, var policyContents) => informationFileDto.MergeWith(PolicyContentsToDto(policyContents))
            };
        };
    }
}
