<img src="assets/images/apim-logo-transparent.png">


#  Code Of Conduct

Before we dive into learning about the APIOPs tool we would like to share some guidance on how to navigate the associated Github repo as you learn your way around the tool:
- Always use the latest [release](https://github.com/Azure/apiops/releases) which includes the latest features and bug fixes
- Please use the [issues](https://github.com/Azure/apiops/issues) section to report any issues that you encounter as well as any requested features that you would like to raise to the development team. Always remember to close your issues when its gets resolved 
- Remember to subscribe to this repo (at least subscribe to be notified about new releases) to stay in the loop as we are always adding new features and squashing these pesky bugs
- Please read this guide before opening any issues
- This is an open source project and hence we are always accepting contributions from the community so make sure you fork and submit your PRs. We take them seriously and they help us make the tool better

#  Versions
Please note that starting with release V.3.0.0 we changed the way the updates to the system get delivered. Thus when reading the docs you will notice that there are some sections that are designated with the <= V.3.0.0 and >= V.3.0.0 suffixes to distinguish between the old and the new update delivery methodology. We recommend switching to the new methodology whenever possible as it greatly simplifies fetching the latest version of the code. 

##  Old method (version < 3.0.0)

Each time a new release was pushed you would have to:
- Download the updated code folder as well as the updated pipelines
- You would have to build the extractor and publisher pipelines and store them within your internal feed
## New method (version >= 3.0.0)

- The binaries are now hosted for you on the public github repo as part of the release
- Utilizing the new version simply involves pointing the pipelines to the new version (refer to the docs on how to achieve that). No more rebuilding of binaries
- You only have to download the updated pipelines if they are updated
- As part of the release we now push two different sets of pipelines. The Azure_DevOps.zip if you are a Azure Devops user and Github.zip if you are a Github user
- We dropped the publish-publisher and publish-extractor pipelines as we now host the binaries for you. If you would rather host the binaries yourself you still have access to the source code which you can download and build yourself. Just keep in mind that these deprecated pipelines now live in the releases themselves and won't be found in the repository. We also now publish the compiled publisher and extractor binaries as part of the release so you can download these and host them internally if you want. The publish-publisher and publish-extractor pipelines are now legacy pipelines that can be found under the V.2.x release if you still need access to them.


#  About this Guide

This guide will walk you through the concept of APIOps. It applies the concepts of GitOps and DevOps to API deployment. By using practices from these two methodologies, APIOps can enable everyone involved in the lifecycle of API design, development, and deployment with self-service and automated tools to ensure the quality of the specifications and APIs that they're building.

APIOps places the Azure API Management infrastructure under version control to achieve these goals. Rather than making changes directly in API Management, most operations happen through code changes that can be reviewed and audited. This workshop is designed to bring customers and partners to a 400-level understanding of automating API deployments in Azure Api Management. This is meant to be a hands-on lab experience, all instructions are provided, but a basic level of understanding of apis, devops and gitops is expected.


Slides

- [APIOps Slides](assets/slides/APIOps.pptx)

Diagrams

- [APIOps Basic Concepts](apiops/0-labPrerequisites/apim-basic-concepts-0-2.md)



# Contributors

<ul class="list-style-none">
{% for contributor in site.github.contributors %}
  <li class="d-inline-block mr-1">
     <a href="{{ contributor.html_url }}"><img src="{{ contributor.avatar_url }}" width="32" height="32" alt="{{ contributor.login }}"/></a>
  </li>
{% endfor %}
</ul>


# Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.