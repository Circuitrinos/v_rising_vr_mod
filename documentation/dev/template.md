---
title: "V Rising Mod Wiki"
source: "https://wiki.vrisingmods.com/dev/template.html"
author:
published: 2026-03-10
created: 2026-10-20
description: "Information about mods for the game V Rising: setup, modding, development, and prefab references."
tags:
  - "clippings"
---
## Template

We have a mod template to make getting started easy. Adding dependencies will require that anyone using your mod also install those dependencies. [VampireCommandFramework](https://github.com/decaprime/VampireCommandFramework/) is recommended for "public" mods as a common command framework.

### Installation

`dotnet new --install VRising.ModTemplate`

### Example usage

`dotnet new vrisingmod -n NameOfYourMod --use-vcf --description "Description of your mod"`

This will get you started with a mod named *NameOfYourMod*, using [VampireCommandFramework](https://github.com/decaprime/VampireCommandFramework/) that has a sample server command to uncomment.

### Optional Dependencies (add using the --use tag)

- [VampireCommandFramework](https://github.com/decaprime/VampireCommandFramework/)

---