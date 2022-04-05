---
title: Reruning Failed Builds
parent: Additional Topics
has_children: false
nav_order: 4
---

There are couple scenarios Which require rerunning the publisher build pipeline. In this section we will cover these two use cases so you can accommodate for them in your environment.
## Reverting Failed Commits
There are situations where you would like to undo some changes and retrigger the build pipeline. The recommended solution here is to utilize **git revert [commit id]** feature in git. For example the git revert HEAD command will revert the latest commit. Similar to a merge, a revert will create a new commit minus the changes that were part of the reverted commit. So for example if you added an api in the latest commit then executing the git revert command will trigger a new commit with the api deletion.

## Rerunning the build pipeline against a specific commit
There are situations where multiple teams could be working against the apim instance on two different apis. If for example you would like to rerun the publisher pipeline against against a specific commit then you can simply do that and the publisher tool will pick up the changes that are part of that commit. This allows you for example to have a situation where two different teams committed changes against two different apis and both would like to rerun the build pipelines to ensure that their changes make it all the way to prodcution. Note that this won't protect you against changes by two teams against the same api or against changes that have dependency on each other which is a use case that goes against devops best practices.

