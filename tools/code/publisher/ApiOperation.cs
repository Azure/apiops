using common;
using System.IO;

namespace publisher;

internal static class ApiOperation
{
    public static ApiOperationDirectory? TryGetApiOperationDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null)
        {
            return null;
        }

        var apiOperationsDirectory = TryGetApiOperationsDirectory(directory.Parent, serviceDirectory);
        if (apiOperationsDirectory is null)
        {
            return null;
        }

        var operationName = new ApiOperationName(directory.Name);
        return new ApiOperationDirectory(operationName, apiOperationsDirectory);
    }

    private static ApiOperationsDirectory? TryGetApiOperationsDirectory(DirectoryInfo? directory, ServiceDirectory serviceDirectory)
    {
        if (directory is null || directory.Name.Equals(ApiOperationsDirectory.Name) is false)
        {
            return null;
        }

        var apiDirectory = Api.TryGetApiDirectory(directory?.Parent, serviceDirectory);

        return apiDirectory is null
                ? null
                : new ApiOperationsDirectory(apiDirectory);
    }

    public static ApiOperationUri GetApiOperationUri(ApiOperationName operationName, ApiName apiName, ServiceUri serviceUri)
    {
        var apiOperationsUri = GetApiOperationsUri(apiName, serviceUri);
        return new ApiOperationUri(operationName, apiOperationsUri);
    }

    private static ApiOperationsUri GetApiOperationsUri(ApiName apiName, ServiceUri serviceUri)
    {
        var apiUri = Api.GetApiUri(apiName, serviceUri);
        return new ApiOperationsUri(apiUri);
    }
}