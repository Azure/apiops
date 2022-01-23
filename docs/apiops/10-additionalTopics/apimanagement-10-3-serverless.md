---
title: API Proxy to Serverless
parent: Additional Topics
has_children: false
nav_order: 3
---

## Additional Topics - API Proxy to Serverless

Azure Serverless (Functions and Logic Apps) can be configured to benefit from the advantages of API Management.

### Azure Functions

- Create a simple function that is Triggered by an HTTP Request

Example:

![](../../assets/images/apim-azure-function-example.png)

```c#
    //string[] strColors = { "blue", "lightblue", "darkblue" };
    string[] strColors = { "green", "lightgreen", "darkgreen" };

    Random r = new Random();
    int rInt = r.Next(strColors.Length);

    return  (ActionResult)new OkObjectResult(strColors[rInt]);
```

Lets add the function to API Management.   In the API blade select [+Add API] and the [Function App] tile

![](../../assets/images/apim-azure-function-add-api.png)

- Select the [Browse] button to get a list of Functions in the subscription

![](../../assets/images/apim-azure-function-add-browse.png)

- Select the Function App and then the Function

![](../../assets/images/apim-azure-function-select-1.png)

![](../../assets/images/apim-azure-function-select-2.png)

- Amend the Names / Descriptions, URL suffix and select the Products

![](../../assets/images/apim-azure-function-create.png)

- As previously add CORS policy

- Validate the function works - either from the Azure management portal or the developer portal

![](../../assets/images/apim-azure-function-test-1.png)

![](../../assets/images/apim-azure-function-test-2.png)

### Azure Logic Apps

- Create a simple logic app that is Triggered by an HTTP Request

Example:

![](../../assets/images/apim-logic-app-example-1.png)

![](../../assets/images/apim-logic-app-example-2.png)

Use the following sample message to generate the schema of the Request body payload.  By specifying the schema, the individual fields (in this case `msg`) can be extracted and referred to in the subsequent logic

```json
{
  "msg": "text"
}
```

Lets add the function to API Managament. In the API blade select [+Add API] and the [Logic App] tile

![](../../assets/images/apim-logic-app-add-api.png)

- Select the [Browse] button to get a list of Logic Apps in the subscription

![](../../assets/images/apim-logic-app-add-browse.png)

- Select the Logic App

![](../../assets/images/apim-logic-app-select.png)

- Amend the Names / Descriptions, URL suffix  and select the Products

![](../../assets/images/apim-logic-app-create.png)

 As previously add CORS policy

- Validate the Logic App works - either from the Azure management portal or the developer poral

![](../../assets/images/apim-logic-app-test-1.png)

![](../../assets/images/apim-logic-app-test-2.png)

- Check the Logic App audit

![](../../assets/images/apim-logic-app-test-3.png)

- Check the email was sent

![](../../assets/images/apim-logic-app-test-4.png)


