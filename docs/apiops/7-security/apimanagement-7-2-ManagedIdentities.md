---
title: Managed Identities
parent: Security
has_children: false
nav_order: 3
---

## Security


### Managed Service Identity

In Azure, an Active Directory identity can be assigned to a managed resource such as an Azure Function, App Service or even an API Management instance. Once an identity is assigned, it has many capabilities to work with other resources that leverage Azure AD for authentication, much like a service principal.

#### Register API Management with Active Directory

![Register APIM](../../assets/images/apim-security-register-principal.png)

#### Key Vault - Create Key Vault and add a secret

- Create a Key Vault instance
- Add a [secret](https://docs.microsoft.com/en-us/azure/key-vault/secrets/quick-create-portal#add-a-secret-to-key-vault) to the Key Vault instance
  - Name:`favoritePerson`
  - Value: `3`

![Create Key Vault](../../assets/images/apim-security-create-key-vault.png)

#### Key Vault - Access policy and principal assignment

Create an access policy

![Create Key Vault](../../assets/images/apim-security-key-vault-1.jpg)

Select the `Get` operation from the list of Secret permissions

![Create Key Vault](../../assets/images/apim-security-key-vault-2.jpg)

Select the principal and search for the name of your API Management instance

![Create Key Vault](../../assets/images/apim-security-key-vault-3.jpg)

![Create Key Vault](../../assets/images/apim-security-key-vault-4.jpg)

Remember to click **Save**

![Create Key Vault](../../assets/images/apim-security-key-vault-5.jpg)

#### API Management, Key Vault and Managed Service Identity

- Add a new operation to the Star Wars API
- Update the policies for the new operation

![New operation](../../assets/images/apim-security-add-operation.png)

```xml
<!-- Inbound -->
<base />
<send-request mode="new" response-variable-name="secretResponse" timeout="20" ignore-error="false">
    <set-url>https://{your-keyvault-base-uri}.azure.net/secrets/favoritePerson/?api-version=7.0</set-url>
    <set-method>GET</set-method>
    <authentication-managed-identity resource="https://vault.azure.net" />
</send-request>
<set-variable name="favoritePersonRequest" value="@{
    var secret = ((IResponse)context.Variables["secretResponse"]).Body.As<JObject>();
    return "/people/" + secret["value"].ToString() + "/";
}" />
<rewrite-uri template="@((string)context.Variables["favoritePersonRequest"])" />
```

#### Test the operation

- Test the operation (use the developer portal, Azure portal or tools like Postman and curl)
- Notice the request URL will be similar to: `https://{your-apim-instance}.azure-api.net/sw/favorite`

