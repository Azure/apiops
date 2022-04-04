---
title: Reverting Failed Commits
parent: Additional Topics
has_children: false
nav_order: 4
---


## Reverting Failed Commits
 The publisher tool scans for the latest commit id and detects the changes and applies them Azure APIM instance. Whereas this works perfectly in detecting the latest changes, there are some cases that could cause the publisher tool to loose track of the your latest changes. Mainly we would like to point out the following two cases:
 - You submit your changes and the build fails due to some error (e.g. bad yaml configuration). The issue here is that if you were to fix the issue and submit a new commit then you would loose the changes from the previous commit as the latest commit would only include changes to the files that caused the error.
 - You may decide to abandon executing the publisher tool in the higher environments (e.g. UAT and Prdo) after publishing the changes to the dev environment and realizing that there are some issues that need to be addressed before reattempting to promote the changes again. 

The recommended solution to the aforementioned scenarios is to utilize git revert feature. For example the git revert HEAD command will revert the latest commit. Similar to a merge, a revert will create a new commit which will open up the configured system editor prompting for a new commit message. Once a commit message has been entered and saved Git will resume operation. We can now examine the state of the repo using git log and see that there is a new commit added to the previous log.
