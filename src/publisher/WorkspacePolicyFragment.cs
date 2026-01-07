using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace publisher;

internal delegate ValueTask<Option<JsonObject>> GetWorkspacePolicyFragmentDto(ResourceName name, ParentChain parents, CancellationToken cancellationToken);

internal static partial class ResourceModule
{
    private static void ConfigureGetWorkspacePolicyFragmentDto(IHostApplicationBuilder builder)
    {
        CommonModule.ConfigureGetCurrentFileOperations(builder);
        common.ResourceModule.ConfigureGetInformationFileDto(builder);
        common.ResourceModule.ConfigureGetPolicyFileContents(builder);

        builder.TryAddSingleton(ResolveGetWorkspacePolicyFragmentDto);
    }

    internal static GetWorkspacePolicyFragmentDto ResolveGetWorkspacePolicyFragmentDto(IServiceProvider provider)
    {
        var getCurrentFileOperations = provider.GetRequiredService<GetCurrentFileOperations>();
        var getInformationFileDto = provider.GetRequiredService<GetInformationFileDto>();
        var getPolicyFileContents = provider.GetRequiredService<GetPolicyFileContents>();

        var resource = WorkspacePolicyFragmentResource.Instance;

        return async (name, parents, cancellationToken) =>
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
