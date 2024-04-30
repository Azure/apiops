using CsCheck;
using LanguageExt;
using System.Collections.Frozen;
using System.Linq;

namespace common.tests;

public sealed record ServicePolicyModel
{
    public ServicePolicyName Name { get; } = ServicePolicyName.From("policy");
    public required string Content { get; init; }

    public static Gen<ServicePolicyModel> Generate() =>
        from content in GenerateContent()
        select new ServicePolicyModel
        {
            Content = content
        };

    public static Gen<string> GenerateContent() =>
        Gen.OneOfConst("""
                       <policies>
                           <inbound>
                               <mock-response status-code="200" content-type="application/json" />
                           </inbound>
                           <backend />
                           <outbound />
                           <on-error />
                       </policies>
                       """,
                       """
                       <policies>
                           <inbound />
                           <backend>
                               <forward-request />
                           </backend>
                           <outbound />
                           <on-error />
                       </policies>
                       """,
                       """
                       <policies>
                           <inbound />
                           <backend>
                               <forward-request />
                           </backend>
                           <outbound />
                           <on-error />
                       </policies>
                       """);

    public static Gen<FrozenSet<ServicePolicyModel>> GenerateSet() =>
        from model in Generate()
        select new[] { model }.ToFrozenSet(x => x.Name);
}