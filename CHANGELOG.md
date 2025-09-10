 # Change log

 ## What's new

 ### Better ID references

 #### The problem
 Many APIM resources reference others through IDs. For example, here is a diagnostic that references a logger through the `loggerId` property:
 ```json
 {
  "properties": {
    ...
    "loggerId": "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg1/providers/Microsoft.ApiManagement/service/apimService1/loggers/azuremonitor"
    ...
  }
}
 ```

If we publish this diagnostic as-is to another APIM instance, the publisher will fail. The logger reference is specific to APIM instance `apimService1` in resource group `rg1` and subscription `00000000-0000-0000-0000-000000000000`. To work around this, we have to edit the JSON or override the publisher configuration to reference the new APIM instance.

#### The solution
Going forward, the extractor will use relative references where possible. In the above scenario, it will write `loggerId: "/loggers/azuremonitor"`. This relative reference will work when publishing across instances.

> [!NOTE]  
> This change was manually implemented across resources. We're not aware of a way to ask the APIM REST API to return relative references. Some resources may have been missed. If you'd like to request that we add a missed resource, please raise an issue.

 ### Nested configuration
We now support nested levels of configuration for the extractor and publisher. This allows the configuration of child resources (e.g. api operations, workspace api diagnostics, etc).

#### Sample extractor configuration
Only operations 1 and 2 will be extracted in api 1. All operations in api 2 will be extracted.
```yaml
apis:
- api1:
    operations:
    - operation1
    - operation2
- api2
...
```

#### Sample publisher configuration
The display name of diagnostic 3 in api 2 in workspace 1 will be overriden with `my display name`.
```yaml
workspaces:
- workspace1:
    apis:
    - api2:
        diagnostics:
        - diagnostic3:
            properties:
              displayName: my display name
...
```

### Empty configuration in extractor

Previously, if we wanted to skip extracting all resources of a type, we had to put a random placeholder value (e.g. `ignore`). We now support the more intuitive `[]`.

#### Before
```yaml
apis: ignore # Workaround to skip extracting all APIs.
```

#### After
````yaml
apis: [] # All APIs will be skipped.
````

## Breaking changes
### New section names in extractor configuration
We've made the section names in the extractor and publisher identical. This provides a more consistent experience and greatly simplifies code maintenance.
#### Before (extractor configuration)
```yaml
apiNames:
- api1
- api2
productNames:
- product1
- product2
```
#### After (extractor configuration)
```yaml
apis:
- api1
- api2
products:
- product1
- product2
```