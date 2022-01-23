---
title: Powerapp + Azure Apim API
parent: Fusion Dev
has_children: false
nav_order: 1
---

The *premier* Star Wars Fan club is growing and the club officers would like to upgrade from their existing member tracking worksheet to a mobile application that would be available to their members all over the world. The members would also like to see information about their favorite Star Wars movies and characters in the application that would update as new shows and movies are released.

In this exercise, you will be using [Star Wars API](https://swapi.dev/) with Azure API Management instance that you created [in part three](https://azure.github.io/apim-lab/apim-lab/3-addingApis/apimanagement-3-1.html) of this lab. The Excel worksheet of member profiles will serve as the primary backing data source and will be used to generate a base application. You will export the Star Wars API from API Management as a Power Platform Custom Connector so that the Canvas App can access real-time Star Wars character information. For each of the Fan Club members, you can then search the Star Wars API character data and show information about their favorite character in the Canvas App.

*Note: This exercise requires access to Power Apps Premium connectors. Sign up for a [free Developer Plan](https://powerapps.microsoft.com/en-us/developerplan/).*

### Create a custom connector

From the existing Star Wars API Api in Azure API Management, click the ellipsis **...** and select the **Create Power Connector** option to generate a custom connector in your Power Platform environment.

![](https://user-images.githubusercontent.com/1610195/134442238-785e77fd-0230-433a-95ab-ac479a1427e6.png)

If you are unable to create a Power Connector from Azure API Management, you can also export an `OpenAPI v2 (JSON)` file that can be imported as a Custom Connector within Power Platform. You can find a sample [here](https://github.com/Azure/apim-lab/blob/updates/fusion-lab-update-testing/apim-lab/9-fusiondev/Star%20Wars%20API.swagger.json).

### View your custom connector in Power Platform

1. Go to [https://make.powerapps.com](https://make.powerapps.com/) and sign in with your organizational account.
2. Select **Data** from the left pane, and then select **Custom Connectors** to see your generated custom connector to your API Management API.

<img width="1437" alt="" src="https://user-images.githubusercontent.com/1610195/134442341-a0dee5ef-a736-432b-88c9-100102980f58.png">

4. From here, select the pencil icon to edit the custom connector.
5. On the **Definition** screen, we need to define a search query string for people so that the Power App can search for character records by name. Select the `GetPeople` action, and in the **Request** section, select **+ Import from sample**. Enter a sample request URL with the search query string:

`https://apim-star-wars-xxxx.azure-api.net/sw/people?search=Luke`

<img width="600" alt="" src="https://user-images.githubusercontent.com/1610195/150045275-6712d93f-eca2-4909-af92-4cd8e4589e9c.png">

5. In the **Response** section of the `getpeople` action, select the `200` response and then select **+ Import from sample**. Copy and paste a sample JSON response into the `Body` section of the response. Close the import panel and select **Update connector**. 

<img width="800" alt="" src="https://user-images.githubusercontent.com/1610195/150047267-5b7874dc-6f30-4c76-ad2e-9e1bc3bf6334.png">

Repeat this import for the `getpeoplebyid` action.

6. On the **Test** screen, create a new connection instance in the **Connections** section. You will then be redirected to the **Connections** area in Power Platform where your connection was created. Navigate back to the **Custom Connectors** page and edit the Star Wars API again. Return to the **Test** page and test each of the API actions.

<img width="800" alt="" src="https://user-images.githubusercontent.com/1610195/150048076-f0c7e4ba-b3d4-4e05-a549-e5d6f19538f7.png">

## **Generate the Star Wars Fan Club Application**

### Connect to the backing data source

1. Download the [**FanClubMembers.xlsx workbook**](../../assets/excel/FanClubMembers.xlsx) and save it to your OneDrive for Business account.
2. Back in the Power Apps Editor, in the left pane, select **Home**.
3. Under **Start from data** , select **Other data sources** and then select **New** from the left pane.
4. Select **OneDrive for Business** data source, and then **Phone layout**.
5. Under **OneDrive for Business** , select **Create**.
6. Under **Connections** , select **OneDrive for Business** and browse to the file location. You might need to select **New Connection** to see the **OneDrive for Business** connection.
7. Under **Choose an Excel file** , select the **FanClubMembers.xlsx** file.
8. Under **Choose a table** , select the **Members** table.
9. Select **Connect** on the bottom right.
10. Power Apps will generate the app by inspecting your data and matching it with Power Apps screens.

## **Add Favorite Character information**

Your generated app will now be in edit mode in the Power Apps Studio.

### Add the Star Wars API Data Source

1. Select **Data** from the left pane and then select **+ Add data** from the drop-down menu.
2. Search for Star Wars in the search field and choose the connection to the Star Wars API.

<img width="367" alt="" src="https://user-images.githubusercontent.com/1610195/134442474-9edfb605-29c6-4eef-a08f-b09dd3bfab88.png">

### Customize the generated app

Your generated app will now be in edit mode in the Power Apps Studio.

You can customize your app theme using the **Theme** drop-down menu and selecting an option. You can change or format the fields that are shown in the Gallery by selecting **Tree view** in the left pane, clicking on the BrowseGallery1, and making edits in the right formatting pane.

<img width="1439" alt="" src="https://user-images.githubusercontent.com/1610195/134442545-f44d863d-d89c-4906-a50c-3b504c7b0ee1.png">

### Add controls to the View Detail screen

1. In the Tree view, select **DetailScreen1**.
1. Select the **+** icon on the left side of the screen to bring up the **Insert** panel.
2. Select **Text Label** and add labels for the Favorite Character section header and for each one of the character description fields.
3. For each label control, change the **Text** property in the right-side **Properties** panel to describe each field.
4. Drag the controls on the screen so they are below the header and are aligned with the center of the screen.

<img width="1440" alt="" src="https://user-images.githubusercontent.com/1610195/134442652-f4747c02-b7ed-4ee8-87c7-72213834912f.png">

### Connect the Detail Screen to the Star Wars API

1. In the left pane, select the **Tree view** and then the **BrowseGallery1** on **Browsescreen1**.
2. Using the drop-down menu, select the **OnSelect** action that will be executed when a user selects a Fan Club member from the gallery.
3. In the **OnSelect** function, we will navigate to **DetailScreen1** and call the Star Wars API to get the character details for the member&#39;s favorite character.

```
Navigate(DetailScreen1, ScreenTransition.None);

ClearCollect(characterCollection, StarWarsAPI.getpeople({search: ThisItem.MemberFavoriteCharacter}).results);
```

<img width="1439" alt="" src="https://user-images.githubusercontent.com/1610195/134442689-1ea145a0-0095-4a28-92f9-f0332a594564.png">

### Show the Star Wars character information on the Detail Screen

1. For each of the description labels on **DetailScreen1** , change the **Text** property in the right-side **Properties** panel to include the data from the API. For example, for the **Name:** label: 
 
 `&quot;Name:&quot; &amp; &quot; &quot; &amp; First(characterCollection).name`

2. Select **Play** in the upper-right corner to practice using the app.

<img width="478" alt="" src="https://user-images.githubusercontent.com/1610195/134442760-d910f2e5-905a-45df-8c6d-2cf25665dfd1.png">

<img width="462" alt="" src="https://user-images.githubusercontent.com/1610195/134442768-42f65239-86a3-4a37-a1e5-0e66a3742efd.png">
