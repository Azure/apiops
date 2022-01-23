---
title: Revisions
parent: Versions & Revisions
has_children: false
nav_order: 2
---


## Revisions

### Add a new revision

- Select the Star Wars API v2.
- Select the _Revisions_ tab.
- Add a new revision with description `Adding a caching policy.`

  ![APIM Revisions Menu](../../assets/images/apim-revisions-menu.png)
  ![APIM Revision Create](../../assets/images/apim-revisions-create.png)

  > The new revision is online but not yet current. The previous revision continues to remain the active default. Having added the new revision has not resulted in change for your consumers.

  ![APIM Created Revision](../../assets/images/apim-created-revision.png)

### Add caching

- Select the `GetPeople` operation.
  > _Revision 2_ automatically became the active revision you are now making changes in. You can also switch between revisions, but be aware that changes to the _Current_ revisions are live immediately.

  ![APIM Revision Add Caching](../../assets/images/apim-revision-add-caching-1.png)

- Add a 10-second caching policy for the GET operation.

  ![APIM Revision Add Caching](../../assets/images/apim-revision-add-caching-2.png)

### Test the new revision

- From the Azure portal, test the `GetPeople` operation.
  > Note the revision number at the top of the page as well as in the request URL.  
  The request URL should look similar to: `https://<your-apim-name>.azure-api.net/sw/v2;rev=2/people/`.

  ![APIM Revision Caching Test](../../assets/images/apim-revision-test-caching-1.png)

- Test the API twice. The test trace should then show that the cache-lookup occurred. 

  ![APIM Revision Caching Test](../../assets/images/apim-revision-test-caching-2.png)

### Make current revision

- Select the _Revisions_ tab.
- Make `rev2` the current revision.

  ![APIM Revision Make Current](../../assets/images/apim-revision-make-current-1.png)

- Choose to post to the public change log for the API and provide a meaningful update.

  ![APIM Revision Make Current](../../assets/images/apim-revision-make-current-2.png)

- The new revision is now the current one. Note that the URL reflects the URL the consumer calls. The old revision is still online and can be accessed with the `rev` qualifier. 

  > Unlike versioning, revisioning requires no URL updates for the API consumer.

  ![APIM Revision Make Current](../../assets/images/apim-revision-make-current-3.png)