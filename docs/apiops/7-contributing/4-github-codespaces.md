---
title: Testing changes locally with Github Codespaces
parent: Contributing
has_children: false
nav_order: 4
---

# Inner loop, testing your changes using Github Codespaces

This repo has a github codespace dev container defined, this container is based on ubuntu 20.04 and contains all the libraries and components to run github pages locally in Github Codespaces(ruby 2.7, jekyll, gems, vscode tasks). To test your changes locally do the following:

- Enable [Github codespaces](https://github.com/features/codespaces) for your account
- Fork this repo
- Open the repo in github codespaces

![](https://docs.github.com/assets/images/help/codespaces/new-codespace-button.png)

- Wait for the container to build and connect to it
- Understand the folder structure of the Repo:
    - "apiops" folder , contains all the mark down documentation files for all the challenges
    - "assets" folder, contains all the images, slides, and files used in the lab
- Understand the index, title, and child metadata used by [just-the-docs theme](https://pmarsceill.github.io/just-the-docs/docs/navigation-structure/#ordering-pages) 
- Run the website in github codespaces using the built-in task:
    - the ".vscode" folder contains a tasks.json file with all the tasks necessary 
    - For example, Pressing  ⇧⌘B on a mac, or running Run Build Task from the global Terminal menu will run the website locally in github codespace. [See Vscode docs for other OS key bindings](https://code.visualstudio.com/docs/editor/tasks).


![Enabling Codespace](../../assets/gifs/codespace.gif)