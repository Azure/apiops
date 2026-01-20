---
title: APIM Version Support
parent: Additional Topics
has_children: false
nav_order: 4
---

## APIM API Version Support

APIOps tools (extractor and publisher) use the Azure API Management REST API to interact with your APIM instances. By default, APIOps supports the **latest non-preview (GA) version** of the Azure API Management REST API, which is currently **2024-05-01**.

### Default API Version

The tools automatically use the latest stable API version that was available at the time of the APIOps release. This ensures compatibility and stability for production environments without requiring any additional configuration.

### Overriding the API Version

There may be scenarios where you need to use a different API version:

1. **Using newer or preview API versions** - To access fields or features available in preview versions
2. **Using older API versions** - For compatibility with specific APIM configurations or requirements

#### Setting ARM_API_VERSION

You can override the default API version by setting the `ARM_API_VERSION` environment variable in your pipeline configuration:

**For Azure DevOps:**
```yaml
- task: PowerShell@2
  displayName: Run Publisher
  inputs:
    targetType: 'inline'
    script: |
      Set-Location publisher
      dotnet run
  env:
    ARM_API_VERSION: '2024-06-01-preview'
    # ... other environment variables
```

**For GitHub Actions:**
```yaml
- name: Run Publisher
  env:
    ARM_API_VERSION: '2024-06-01-preview'
    # ... other environment variables
  run: |
    cd publisher
    dotnet run
```

#### Using Publisher Configuration Overrides

When you need to publish fields that are only available in newer or preview API versions, you can combine the `ARM_API_VERSION` setting with configuration overrides in your YAML configuration file (e.g., `configuration.prod.yaml`).

For example, if a new preview version introduces additional properties for an API or product, you can:

1. Set the `ARM_API_VERSION` to the preview version (e.g., `2024-06-01-preview`)
2. Add the new fields to your configuration YAML file to override the artifact definitions
3. The publisher will use the specified API version when making requests to Azure

**Example configuration override:**
```yaml
apimServiceName: my-apim-service
apis:
  - name: my-api
    properties:
      # Standard fields
      displayName: My API
      path: myapi
      # New fields available in preview version
      newPreviewField: value
```

### Finding Available API Versions

You can find all available Azure API Management REST API versions in the [official Microsoft documentation](https://learn.microsoft.com/en-us/rest/api/apimanagement/). The documentation includes:

- Current GA (General Availability) versions
- Preview versions with experimental features
- Deprecated versions that should be avoided

### Important Notes

- **Stability**: Using preview API versions may expose your pipelines to breaking changes as these versions are subject to change
- **Production environments**: We recommend using the latest GA (non-preview) version for production workloads
- **Testing**: Always test with preview versions in non-production environments first
- **Version consistency**: Ensure both your extractor and publisher use compatible API versions when working with the same APIM instance

### Related Documentation

- [Tools - Extractor](../3-apimTools/apiops-2-1-tools-extractor.md) - See the ARM_API_VERSION parameter documentation
- [Tools - Publisher](../3-apimTools/apiops-2-2-tools-publisher.md) - See the ARM_API_VERSION parameter documentation and configuration override examples
- [Configuration Overrides](https://github.com/Azure/apiops/blob/main/configuration.prod.yaml) - Sample configuration file
