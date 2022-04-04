---
title: Abandoning Commits
parent: Additional Topics
has_children: false
nav_order: 4
---


## Restoring Abandoned Commits
 The publisher tool scans for the latest commit id and detects the changes and applies them Azure APIM instance. Whereas this works perfectly in detecting the latest changes, there are some cases that could cause the publisher tool to loose trach of the your latest changes. Mainly we would like to point out the following two cases:
 - You submit your changes and the build fails due to some error (e.g. bad yaml configuration). The issue here is that if you were to fix the issue and submit a new commit then you would loose the changes from the previous commit as the latest commit would only include changes to the files that caused the error.
 - You may decide to abandon executing the publisher tool in the higher environments (e.g. UAT and Prdo) after publishing the changes to the dev environment and realizing that there are some issues that need to be addressed before reattempting to promote the changes again. 

The recommended solution to the aforementioned scenario is to utilize git reset feature. The git reset HEAD~1 command moves the current branch backward by one commits, effectively removing the last snapshot we just created from the project history.
