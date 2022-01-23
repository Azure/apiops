---
title: Calling Apis
parent: Adding APIs
has_children: false
nav_order: 3
---


## Calling API and testing Subscription Keys

Let's add another API, the [Color API](https://markcolorapi.azurewebsites.net/swagger/).

![APIM Color API](../../assets/images/apim-color-api.png)

- Create a new API with OpenAPI specification and import swagger from <https://markcolorapi.azurewebsites.net/swagger/v1/swagger.json>. 
- Use the API URL suffix **color**.

  ![APIM Add Color API](../../assets/images/apim-add-color-api-1.png)

  ![APIM Add Color API](../../assets/images/apim-add-color-api-2.png)

- We can test the newly-added API from the *Test* tab. Note the successful **200** response.

  ![APIM Test Color API](../../assets/images/apim-test-color-api.png)

- Products can be configured after the API is initially created as well. On the *Settings* tab, set *Products* to include *Starter* and *Unlimited*, then press *Save*.

  ![APIM Color API Add Products](../../assets/images/apim-color-api-add-products.png)

- Switch to the Developer portal and look at the *Color* API.
- Try the *ApiRandomColorGet* operation.
- Notice the successful `200` response and the returned random color.

  ![APIM Developer Portal Color API Try It](../../assets/images/apim-developer-portal-color-api-try-it-1.png)

  ![APIM Developer Portal Color API Try It](../../assets/images/apim-developer-portal-color-api-try-it-2.png)

### Rate limit

API Management uses rate limiting to protect APIs from being overwhelmed and helps prevent exposure to DDoS attacks. As APIM sits in between your API and their callers, it effectively governs access to your APIs.  

We are going to use the [Color](https://markcolorweb.azurewebsites.net) website to demonstrate how rate limiting is applied. The website displays 500 lights. Each light will randomly make a call to the RandomColor API and then apply the returned color to the lights.

![Color Website](../../assets/images/color-website.png)

First, we need to enable CORS for the domain name of the frontend. To achieve this we have to do the following in APIM:

- On the sidemenu, click on `APIs`, then select the `All APIs` option.
- Inside the `Inbound processing` area you will see the `cors` policy, which we added in part 2 by pressing the `Enable Cors` button.
- Click on the pencil icon next to that policy to edit it.

  ![APIM Policy CORS All APIs](../../assets/images/apim-policy-cors-all-apis-1.png)  

- Here we will see this form where we can add the domain name of our frontend `https://markcolorweb.azurewebsites.net` or the `*` for all domains. Press *Add allowed origin*, enter the URL, then press *Save*.

  ![APIM Policy CORS All APIs](../../assets/images/apim-policy-cors-all-apis-2.png)

- After enabling CORS in APIM lets go back to our frontend <https://markcolorweb.azurewebsites.net> and follow these steps:

- Click on the hamburger menu next to *Colors* in the top left corner.
- Click on *Config*.
- Replace the *API URL* according to this format: <https://YOURAPIM.azure-api.net/color/api/randomcolor>
- After setting the API URL correctly, press the hamburger menu again and go to *Home*. 
- Press *Start* to see how the frontend is calling the api. You should see a *401* response, indicating an auth error. This happens as our API requires a subscription, but we have not yet entered a subscription key. 

  ![Color Website APIM 401](../../assets/images/color-website-apim-401.png)

- The subscription keys can be fetched from the Developer Portal. Open the main Developer Portal page, then click on *Profile* in the top menu. 
- Copy the following URL into Notepad, modify your APIM instance, then copy the URL, so that you have two of the same URLs. We will use them for the *Starter* and *Unlimited* pathways into APIM.
  - `https://YOURAPIM.azure-api.net/color/api/RandomColor?key=`
- Append the primary keys for both subscriptions - one key per URL - to get unique URLs for *Starter* and *Unlimited*.
  
  ![Notepad Color API Subscription Keys](../../assets/images/notepad-color-api-subscription-keys.png)

- To see that *Unlimited* product has no rate limits:
  - Configure the Color website to use the Unlimited URL.
  - Select [Start].
  - Notice there is no rate limit - every light is randomly and continuously updated. 

    ![Color Website APIM Unlimited Product](../../assets/images/color-website-apim-unlimited-product.png)

- To see that *Starter* product is limited to 5 calls per minute:
  - Configure the Color website to use the Starter URL.
  - Select [Start].
  - Notice that only 5 lights get colored.

    ![Color Website APIM Starter Product](../../assets/images/color-website-apim-starter-product.png)

- Try the same *Starter* URL directly in your web browser:
  - Notice the error status / message returned. For example: `{ "statusCode": 429, "message": "Rate limit is exceeded. Try again in 40 seconds." }`

    ![APIM Color API URL in Browser for Starter Product 429 ](../../assets/images/apim-color-api-url-in-browser-starter-product-429.png)