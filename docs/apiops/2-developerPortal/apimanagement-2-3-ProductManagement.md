---
title: Product Management
parent: Developer Portal
has_children: false
nav_order: 3
---


## Product Management

A product contains one or more APIs as well as a usage quota and the terms of use. Once a product is published, developers can subscribe to the product and begin to use the product's APIs.

### Product definition

- In the Azure Portal, open the resource menu item `Products`.

  ![APIM Products](../../assets/images/apim-products.png)

- Let's add a new product tier called `Gold Tier`. 

  ![APIM Add Product](../../assets/images/apim-add-product-1.png)

  ![APIM Add Product](../../assets/images/apim-add-product-2.png)

- Next, we'll change the access control by clicking on *Gold Tier* and selecting *Access control* in the left pane.

  ![APIM Add Product Access](../../assets/images/apim-add-product-access-1.png)

  Press *Add group*, check *Developers* and *Guests*, then press *Select*. The two added roles are shown now.

  ![APIM Add Product Access](../../assets/images/apim-add-product-access-2.png)

  Back in the private browsing session, browse to *Products* and observe the new *Gold Tier*. 

  ![APIM Developer Portal Added Product](../../assets/images/apim-developer-portal-added-product.png)